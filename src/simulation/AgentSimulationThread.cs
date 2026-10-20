using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core;
using Game.Simulation.Jobs;
using Game.Simulation.Scheduling;
using Game.Simulation.Simd;
using Vector2 = System.Numerics.Vector2;

namespace Game.Simulation;

/// <summary>
/// P1 (Phase2-пик): ворота stagger'а path-запросов. MoveTowards идёт из
/// параллельных Phase2-воркеров — поле тика должно читаться lock-free.
/// _tickCounter живёт в sim-потоке и мутируется каждый суб-степ: публикуем
/// копию через volatile сюда (один write/тик из sim-потока, N reads/тик
/// из воркеров — гонки нет, stale на суб-степ безвреден: stagger лишь
/// разносит запросы, точное значение тика не важно).
/// </summary>
public static class AgentSimTickGate
{
	public static volatile uint Current;
}

public sealed class AgentSimulationThread : IDisposable
{
	private AgentDataPool _pool;
	private SimulationContext _ctx;
	private readonly WanderJobSystem _wanderSystem = new();
	private readonly NeedsJobSystem _needsSystem = new();
	// §119 P2: система Worker'ов зон (выдача из очереди зоны + прерывание нуждами).
	private readonly WorkerZoneJobSystem _workerJobSystem = new();

	private readonly Vector2[][] _positionSnapRing = new Vector2[4][];
	private int _posSnapCursor = 0;

	public ConcurrentQueue<Vector2[]> PositionQueue { get; } = new();

	public static bool EnableConsoleDebugAudit = false;

	private volatile bool _running;
	public volatile bool IsPaused;

	private volatile float _speedMultiplier = 1.0f;
	private volatile bool _speedResetRequested;
	private Task _simulationTask;
	// Поколение петли симуляции. Start() может НЕ дождаться Stop() (таймаут 5 с) —
	// тогда старая петля ещё жива. Без токена она воскрешалась от `_running = true` и
	// тикала ПАРАЛЛЕЛЬНО с новой (два SimulationLoop на одном _pool/_ctx), а ссылка на
	// неё терялась при перезаписи _simulationTask. Смена поколения гарантирует выход
	// stale-петли на ближайшей границе тика, до касания новых _pool/_ctx.
	// ПОКОЛЕНИЕ ПЕТЛИ — статическое (процесс-глобальное). Main.cs создаёт НОВЫЙ экземпляр
	// AgentSimulationThread (Main.cs:367), поэтому инстансный токен не остановил бы петлю
	// ПРЕДЫДУЩЕГО экземпляра: она живёт вечно и гадит в общие синглтоны
	// (JobDispatcher.Instance.IdleWorkers, JobRegistry, HierarchicalPathfinder.Instance).
	// Любой Start() инкрементирует поколение → все прежние петли выходят на границе тика.
	private static volatile int _loopGeneration;
	private uint _tickCounter;
	// P1 (Phase3b): rebuild SpatialGrid не каждый суб-степ, а раз в 4 тика.
	// При dt=0.05 агент идёт 6px/суб-степ (~1/10 тайла): сетка за 4 тика
	// устаревает максимум на ~0.4 тайла — читатели (overcrowd 5/клетка,
	// stand-проверка, push-away, idle-чанки) толерантны. Commit каждый тик.
	private int _lastSpatialRebuildTick = -12;
	private const int SpatialRebuildPeriodTicks = 12;
	// FIX круг-2 №9: старые значения MinThreads для restore в Stop().
	// Без сохранения глобальный SetMinThreads тёк наружу (меняли пул навсегда).
	private int _prevMinWorkerThreads;
	private int _prevMinCompletionThreads;
	private bool _minThreadsAdjusted;    private float _dispatchTimer;
	private float _cropGrowthTimer;
	// Медленная влага почвы: тик раз в 30с игрового времени.
	private float _humidityTimer;
	// Плодородие почвы: тик раз в 60с игрового (почти геология).
	private float _fertilityTimer;
	private float _stockpileSweepTimer;
	// Аудит работ (JobValidator): тикает по РЕАЛЬНОМУ времени, не игровому —
	// иначе на паузе/1x проверка стояла бы, а на 100x долбила бы каждый тик.
	private float _jobAuditRealTimer;
	// Таймер редкого обновления окружения (потребности): растёт в игровом времени,
	// сбрасывается раз в AgentNeedsConfig.EnvironmentUpdatePeriodGameSec (= 1 игровой час).
	private float _needsEnvTimer;
	// P0-1: гейт секундных sys-рядов (WorkingSet64/GetTotalMemory — дорого).
	private long _sysSeriesWallTicks;

	// Переиспользуемые маски для влаги/плодородия (ноль GC аллокаций в горячем тике)
	private bool[] _staticMountainMask;
	private bool[] _reusableForestMask;
	private bool[] _reusableFarmMask;
	private bool _environmentalMasksDirty = true;

	public void MarkEnvironmentalDirty() => _environmentalMasksDirty = true;

	private void EnsureEnvironmentalMasks(int mw, int mh, TileType[,] ground)
	{
		int n = mw * mh;
		if (_staticMountainMask == null || _staticMountainMask.Length != n)
		{
			_staticMountainMask = new bool[n];
			_reusableForestMask = new bool[n];
			_reusableFarmMask = new bool[n];

			int gw = ground.GetLength(0);
			int gh = ground.GetLength(1);
			int mapW = Math.Min(gw, mw);
			int mapH = Math.Min(gh, mh);
			for (int y = 0; y < mapH; y++)
			{
				int row = y * mw;
				for (int x = 0; x < mapW; x++)
				{
					if (ground[x, y] == TileType.Mountain)
						_staticMountainMask[row + x] = true;
				}
			}
		}
	}

	/// <summary>
	/// Накопленное мировое игровое время (секунды) — авторитет «времени в мире».
	/// Растёт только при реальном исполнении шагов симуляции (пауза останавливает его).
	/// Старт — 7:00 утра дня 1 (светлое утро, а не полночь): DayNumber сразу 1,
	/// тени/тинт корректны с первого кадра.
	/// </summary>
	public volatile float GameTimeSeconds = WorldTime.HoursToSeconds(InitialStartHour);

	/// <summary>Час старта новой игры (утро, свет уже есть, жары полдня нет).</summary>
	public const float InitialStartHour = 7f;

	public float SpeedMultiplier
	{
		get => _speedMultiplier;
		set
		{
			_speedMultiplier = value;
			_speedResetRequested = true;
		}
	}

	public enum SimulationBackend
	{
		CpuSimd,
		GpuCompute
	}

	public static SimulationBackend ActiveBackend { get; set; } = SimulationBackend.CpuSimd;
	public static string SimBackendLabel => ActiveBackend == SimulationBackend.CpuSimd ? "Sim: [CPU SIMD]" : "Sim: [GPU Compute]";

	private const float BaseFixedDeltaTime = 0.05f;
	private const float MinSnapIntervalBase = 1.0f / 30.0f;

	private float GetMinSnapInterval()
	{
		// На высокой скорости (25x+) отправляем снапшоты рендера 20 раз/с вместо 30 раз/с.
		// GPU-интерполятор шейдера плавно интерполирует кадры между снапшотами на 60 FPS,
		// но нагрузка на P/Invoke шину и копирование буферов снижается на 33-50%.
		if (_speedMultiplier >= 25f) return 1.0f / 20.0f;
		return MinSnapIntervalBase;
	}

	// Интервалы диспетчеризации и роста культур измеряются в ИГРОВОМ времени,
	// поэтому частота вызовов в реальном времени = speed / interval и без
	// масштабирования растёт линейно со скоростью (на 100x диспетчер вызывался
	// бы ~333 раза/с, рост культур — ~167 раза/с). Масштаб speed/IntervalScaleSpeed
	// ограничивает частоту сверху (~56 вызовов/с для диспетчера, ~30 для культур).
	// Sweep склада — в том же масштабе: на 100x порог 1.0 игросек при dt=0.5
	// срабатывал бы каждые 2 суб-степа (~8 раз за тик) — скан Ground-словаря
	// под lock каждый раз. Квадратичный масштаб держит его ≤1 раза за тик.
	private const float DispatchIntervalBase = 0.25f;

	/// <summary>§119 P2: «сейчас» для роутера (ms) — тест-овервет или системные часы.</summary>
	private static long RouterNowMs() =>
		ZoneJobRouter.TestClockOverrideMs >= 0 ? ZoneJobRouter.TestClockOverrideMs : System.Environment.TickCount64;
	private const float CropGrowthIntervalBase = 0.5f;
	private const float StockpileSweepIntervalBase = 1.0f;
	private const float IntervalScaleSpeed = 16f;
	private const float IntervalScaleSpeedHi = 64f;
	private const int ParallelThreshold = 128;
	// P-динамика: статический ChunkSize=1024 убран — фазы идут через
	// DynamicWorkBalancer (батчи 32..512 + work-stealing через атомарный курсор).
	// Тяжёлый агент задерживает батч 128, а не чанк 1024: straggler-хвост короче в ~8x.

	public void Start(int agentCount, TileType[,] ground, bool[,] treeOnGrass, int seed = 0, HumidityMap humidity = null, bool[,] stoneOnGrass = null, FertilityMap fertility = null)
	{
		try
		{
			if (_running || (_simulationTask != null && !_simulationTask.IsCompleted))
			{
				GD.Print("[AgentSimulationThread] Предыдущая симуляция активна, останавливаем перед новым стартом...");
				if (!Stop(5000))
				{
					// Раньше результат игнорировался: живая старая петля получала обратно
					// _running = true и работала параллельно с новой. Поколение ниже
					// (++_loopGeneration) инвалидирует её принудительно.
					GD.PrintErr("[AgentSimulationThread] Старая петля не встала за 5000 мс — инвалидирую её поколение (защита от двух петель симуляции).");
				}
			}

			int width = ground.GetLength(0);
			int height = ground.GetLength(1);
			bool[,] solidWalls = new bool[width, height];

			var walkableTiles = new List<(int X, int Y)>(agentCount);
			for (int x = 0; x < width; x++)
			{
				for (int y = 0; y < height; y++)
				{
					// Камень — препятствие (как дерево): ни спавн, ни проход.
					bool stone = stoneOnGrass != null && stoneOnGrass[x, y];
					if (ground[x, y] == TileType.Grass && !treeOnGrass[x, y] && !stone)
					{
						walkableTiles.Add((x, y));
					}
				}
			}

			if (walkableTiles.Count == 0)
				throw new InvalidOperationException("Нет проходимых тайлов травы.");

			var random = new Random(seed == 0 ? System.Environment.TickCount : seed);
			_pool = new AgentDataPool(agentCount);

			var spatialGrid = new AgentSpatialGrid(width, height);
			var movement = new AgentMovementService();

			_ctx = new SimulationContext(ground, humidity, treeOnGrass, solidWalls, walkableTiles, spatialGrid, movement, random, 64, stoneOnGrass, fertility);
			HierarchicalPathfinder.Instance.Initialize(_ctx);

			for (int i = 0; i < agentCount; i++)
			{
				int tileIndex = random.Next(walkableTiles.Count);
				var (tx, ty) = walkableTiles[tileIndex];
				float posX = (tx << 6) + 32f;
				float posY = (ty << 6) + 32f;

				_pool.PositionX[i] = posX;
				_pool.PositionY[i] = posY;
				_pool.LastPositionX[i] = posX;
				_pool.LastPositionY[i] = posY;
				_pool.TargetPositionX[i] = posX;
				_pool.TargetPositionY[i] = posY;
				_pool.StepTargetX[i] = posX;
				_pool.StepTargetY[i] = posY;
				_pool.CurrentCellX[i] = tx;
				_pool.CurrentCellY[i] = ty;
				_pool.States[i] = AgentState.Idle;
				_pool.JobSearchTimer[i] = 4.0f + (float)random.NextDouble() * 6.0f;
				// P1: джиттер голода/сна при спавне (±20): иначе все 10k пересекают порог
				// Hunger>70 в один тик 16:48 и устраивают thundering herd сканов еды.
				_pool.Hunger[i] = (float)random.NextDouble() * 20.0f;
				_pool.Sleep[i] = (float)random.NextDouble() * 20.0f;
				// P1 (Phase2-пик): джиттер кулдауна пути при спавне — иначе все
				// агенты, назначенные одним диспатчем, выходят из кулдауна синхронно
				// и хором бьют в A*. Разброс 0..1.5с = фаза запросов распределена.
				_pool.PathRequestCooldown[i] = (float)random.NextDouble() * 1.5f;

				JobDispatcher.Instance.IdleWorkers.AddIdleWorker(i, _pool);
			}

			JobRegistry.Register(new TreeChoppingJobHandler());
			JobRegistry.Register(new MiningJobHandler());
			JobRegistry.Register(new ConstructionJobHandler());
			JobRegistry.Register(new FarmingJobHandler());
			JobRegistry.Register(new BlueprintDeliveryJobHandler());
			JobRegistry.Register(new StockpileHaulingJobHandler());
			JobRegistry.Register(new PlantingJobHandler());
			JobRegistry.Register(new HarvestJobHandler());

			PushSnapshot();

			// Предварительный прогрев масок окружения и GPU шейдеров во время загрузки мира (ноль спайков на первом тике)
			if (humidity != null)
			{
				EnsureEnvironmentalMasks(humidity.Width, humidity.Height, ground);
			}
			try
			{
				_ = Game.Simulation.Gpu.GpuHumiditySimulator.Instance.IsAvailable;
			}
			catch { }

			// FIX круг-2 №9: сохранить старые значения, проверить bool SetMinThreads,
			// восстанавливать в Stop(). Без restore меняли глобальный пул навсегда.
			// PERF F1: DOP = P-1 — резервируем одно ядро под рендер/Godot main loop.
			int dop = DynamicWorkScheduler.ComputeEffectiveDop(System.Environment.ProcessorCount);
			if (dop < 1) dop = 1;
			ThreadPool.GetMinThreads(out _prevMinWorkerThreads, out _prevMinCompletionThreads);
			if (ThreadPool.SetMinThreads(dop, dop))
				_minThreadsAdjusted = true;

			// Поколение инкрементируем ДО Task.Run: любая предыдущая петля увидит новое
			// значение на своей границе тика и завершится, не тронув новый _pool/_ctx.
			int generation = ++_loopGeneration;

			_running = true;
			_simulationTask = Task.Run(() => SimulationLoop(generation));
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[AgentSimulationThread] Ошибка старта симуляции: {ex.Message}\n{ex.StackTrace}");
		}
	}

	private void SimulationLoop(int generation)
	{
		// Понижаем приоритет фонового потока симуляции относительно UI/ОС Windows,
		// чтобы системные прерывания (Win+Shift+S, клавиатура) не подвисали при 100k агентах.
		Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

		var sw = new Stopwatch();
		var renderTimer = new Stopwatch();
		float accumulator = 0f;
		sw.Start();
		renderTimer.Start();

		while (_running && generation == _loopGeneration)
		{
			try
			{
				if (_speedResetRequested)
				{
					accumulator = 0f;
					_speedResetRequested = false;
					// Finding 3: мягкий сброс — очереди чистятся, EMA размеров
					// батчей ХРАНИТСЯ (полный Reset не успевал стабилизироваться
					// при частом переключении 1x/5x/25x).
					DynamicWorkBalancer.ResetForSpeedChange();
					sw.Restart();
				}

				if (IsPaused)
				{
					Thread.Sleep(20);
					sw.Restart();
					continue;
				}

				float realDelta = (float)sw.Elapsed.TotalSeconds;
				sw.Restart();

				if (realDelta > 0.25f) realDelta = 0.25f; // Clamp 0.25с: при долгих итерациях Phase2 учитываем реальное время честнее; больше — риск спирали смерти (нагрузка растёт лавинообразно).
				accumulator += realDelta * _speedMultiplier;

				float currentStepDt = GetSimStepDelta(_speedMultiplier);
				float dispatchInterval = GetScaledInterval(DispatchIntervalBase, _speedMultiplier);
				float cropGrowthInterval = GetScaledInterval(CropGrowthIntervalBase, _speedMultiplier);
				float sweepInterval = GetScaledIntervalHi(StockpileSweepIntervalBase, _speedMultiplier);
				// Амортизация шагов: на 100x делим работу на порции по 1 шагу (макс 2 при отставании >6 шагов).
				// Поток сразу берёт следующую порцию через Thread.Sleep(0), пиковая нагрузка падает с 5-15 мс до <= 1.0 мс (потолок 1.5 мс).
				int maxAllowedSteps = _speedMultiplier >= 100f
					? (accumulator > currentStepDt * 6f ? 2 : 1)
					: 10;
				float maxAccumulatorCap = _speedMultiplier >= 100f
					? (currentStepDt * 16f)
					: (currentStepDt * 10f);
				int steps = (int)(accumulator / currentStepDt);

				if (steps > maxAllowedSteps)
				{
					steps = maxAllowedSteps;
					// Переносим ограниченный остаток (до 16 шагов backlog), чтобы лишнее время не сгорало, но спираль смерти не разгонялась.
					accumulator = Math.Min(accumulator - steps * currentStepDt, maxAccumulatorCap);
				}
				else
				{
					accumulator -= steps * currentStepDt;
				}

				if (steps > 0)
				{
					// P0-1: тиковые метрики — раз в секунду wall-clock, не каждый тик.
					// WorkingSet64 = дорогой syscall, GetTotalMemory(false) дёргает GC,
					// интерполяция $"dt=..." — аллокация. Всё это было ×2000/с.
					long nowWallTicks = DateTime.UtcNow.Ticks;
					if (nowWallTicks - _sysSeriesWallTicks >= 10000000L)
					{
						_sysSeriesWallTicks = nowWallTicks;
						SimEvents.SetClock(GameTimeSeconds, _speedMultiplier);
						SimEvents.Series("sys.gc0", GC.CollectionCount(0));
						SimEvents.Series("sys.mem_mb", GC.GetTotalMemory(false) / 1048576f);
						try { SimEvents.Series("sys.workingset_mb", (float)(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576.0)); } catch { }
						SimEvents.Series("sys.pool_pending", System.Threading.ThreadPool.PendingWorkItemCount);
						if (steps >= maxAllowedSteps)
							SimEvents.Mark("SIM_LAG", $"steps={steps} acc_cut");
					}
					// Буфер JobAudit-печати: GD.Print из горячего пути убран —
					// копим за цикл шагов, печатаем один раз из sim-потока после фаз.
					int jobAuditFixedTotal = 0;
					// Баланс фаз больше НЕ сбрасывается на тик: планировщик копит
					// кумулятивные суммы, а GameProfiler.SnapshotMetrics берёт дельту
					// за своё окно (0.1 с / N кадров) и нормирует её на кадр.
					// Раньше сброс на каждый проход петли означал, что BALANCE
					// показывал последний тик (часто 1 суб-степ ≈ 0.67ms), а таблица
					// методов — сумму окна (19.62ms): числа было не свести.
					using (GameProfiler.ScopeCustom("Simulation.TotalStepCycle"))
					{
						for (int step = 0; step < steps; step++)
						{
							_tickCounter++;
							// P1: публикация тика для stagger'а path-запросов
							// (AgentMovementService читает AgentSimTickGate.Current
							// из Phase2-воркеров — volatile write, дёшево).
							AgentSimTickGate.Current = _tickCounter;
							_dispatchTimer += currentStepDt;
							_cropGrowthTimer += currentStepDt;
							_stockpileSweepTimer += currentStepDt;
							_needsEnvTimer += currentStepDt;
							// Мировое игровое время: каждый шаг = квант игрового времени.
							GameTimeSeconds += currentStepDt;

							// Редкое обновление окружения (потребности): раз в игровой час.
							bool updateNeedsEnvThisStep = false;
							if (_needsEnvTimer >= AgentNeedsConfig.EnvironmentUpdatePeriodGameSec)
							{
								_needsEnvTimer = 0f;
								updateNeedsEnvThisStep = true;
							}

							if (_cropGrowthTimer >= cropGrowthInterval)
							{
								// GPU-трек удалён: рост культур — чистый CPU
								// (HumidityMap.GrowthMultiplier внутри UpdateGrowth).
								using (GameProfiler.ScopeCustom("Simulation.CropGrowth"))
								{
									CropGrowthManager.Instance.UpdateGrowth(_cropGrowthTimer, _ctx);
								}
								_cropGrowthTimer = 0f;
							}

							// Почвенная влага: медленный тик раз в 30с игрового.
							// Цифры дрейфуют на единицы, у воды держится 150+.
							// Маски — готовые bool[] (без Func-виртуала на клетку):
							// горы/лес строим линейным проходом, грядки — flat-маской.
							_humidityTimer += currentStepDt;
							float humidityInterval = GetScaledIntervalHi(HumidityMap.TickIntervalGameSec, _speedMultiplier);
							if (_humidityTimer >= humidityInterval && _ctx?.Humidity != null)
							{
								_humidityTimer = 0f;
								using (GameProfiler.ScopeCustom("Simulation.Humidity"))
								{
								var humidityMap = _ctx.Humidity;
								var ground = _ctx.Ground;
								var trees = _ctx.TreeOnGrass;
								int mw = humidityMap.Width;
								int mh = humidityMap.Height;
								EnsureEnvironmentalMasks(mw, mh, ground);

								// Кеширование масок (PLAN.md §44): сканирование 262k клеток леса и грядок
								// выполняется ТОЛЬКО при реальном изменении (вырубка/посадка), а не каждый тик!
								if (_environmentalMasksDirty)
								{
									Array.Clear(_reusableForestMask, 0, _reusableForestMask.Length);
									int gw = ground.GetLength(0);
									int gh = ground.GetLength(1);
									int mapW = Math.Min(gw, mw);
									int mapH = Math.Min(gh, mh);
									int tw = trees != null ? trees.GetLength(0) : 0;
									int th = trees != null ? trees.GetLength(1) : 0;
									int limX = Math.Min(mapW, tw);
									int limY = Math.Min(mapH, th);
									for (int y = 0; y < limY; y++)
									{
										int row = y * mw;
										for (int x = 0; x < limX; x++)
										{
											if (trees[x, y])
												_reusableForestMask[row + x] = true;
										}
									}
									FarmJobManager.Instance.FillGardenBedFlatMask(_reusableFarmMask, mw, mh);
									_environmentalMasksDirty = false;
								}

								// Сезон от дня года (п.19.5-П5): лето −1, зима +1.
								// День года из мирового времени (сутки = 500 геймсек).
								float dayOfYear = (GameTimeSeconds / 500f) % 360f;
								float season = MathF.Sin(dayOfYear / 360f * MathF.PI * 2f - MathF.PI / 2f);
								humidityMap.Tick(ground, _staticMountainMask, _reusableForestMask, _reusableFarmMask, _staticMountainMask, season);
								}
							}

							// Плодородие почвы: тик раз в 60с игрового, в 2 раза реже
							// влаги. Лес удобряет, огород истощает, вода/горы = 0.
							// Маски леса/грядок переиспользуем с humidity-тика выше.
							_fertilityTimer += currentStepDt;
							float fertilityInterval = GetScaledIntervalHi(FertilityMap.TickIntervalGameSec, _speedMultiplier);
							if (_fertilityTimer >= fertilityInterval && _ctx?.Fertility != null)
							{
								_fertilityTimer = 0f;
								using (GameProfiler.ScopeCustom("Simulation.Fertility"))
								{
								var fertilityMap = _ctx.Fertility;
								var ground = _ctx.Ground;
								int fw = fertilityMap.Width;
								int fh = fertilityMap.Height;
								EnsureEnvironmentalMasks(fw, fh, ground);
								fertilityMap.Tick(ground, _reusableForestMask, _reusableFarmMask);
								}
							}

							if (_dispatchTimer >= dispatchInterval)
							{
								_dispatchTimer = 0f;
								// P0-2: dispatchScale кап 2 вместо 8. Scale 8 на 100x давал
								// 1024 чанка × claim-скан за вызов — проход тяжелел вместе
								// с ростом чанков (см. ×135). Round-robin покрывает карту
								// за несколько вызовов, опоздание на тик безвредно.
								int dispatchScale = (int)Math.Ceiling(Math.Max(1f, _speedMultiplier / IntervalScaleSpeed));
								dispatchScale = Math.Min(dispatchScale, 2);
								using (GameProfiler.ScopeCustom("Simulation.ZoneDispatch"))
								{
									ZoneManager.Instance.DispatchZones(_pool, _ctx);
								}
								// §119 P1/P2: репуш непокрытых (30с) + чистка мёртвых работ
								// из очередей зон + истёкший 60с-холд Worker'ов. Throttle
								// 1р/2с внутри (как у SweepStockpileHaulJobs).
								ZoneJobRouter.Instance.Sweep(RouterNowMs());
								JobDispatcher.Instance.DispatchPendingJobs(_pool, _ctx, dispatchScale);
								// GPU-трек удалён: агенты идут локальным BFS
								// (FlowFieldManager.CalculateLocalDetourDirection).
							}

							// Плановый «подметальный» проход: гарантирует, что
							// для каждого лежащего на земле предмета есть haul-работа.
							// (Склад может быть нарисован ПОСЛЕ выпадения предметов.)
							// Интервал масштабирован как диспатч (квадратично на 100x):
							// иначе при dt=0.5 порог 1.0с срабатывал каждые 2 суб-степа.
							if (_stockpileSweepTimer >= sweepInterval)
							{
								_stockpileSweepTimer = 0f;
								using (GameProfiler.ScopeCustom("Simulation.Sweep"))
								{
									JobBroker.Instance.SweepStockpileHaulJobs();
								}
								// ЖЁСТКИЙ ПАЙПЛАЙН: самопочинка строек (сверка
								// клеток с фактом мира + пересоздание потерянных
								// работ). Внутри свой wall-clock гейт 5с.
								using (GameProfiler.ScopeCustom("Simulation.PipelineReconcile"))
								{
									ConstructionManager.Instance.ReconcileTick(_ctx);
								}
							}

							// Аудит работ по РЕАЛЬНОМУ времени: раз в 30с порциями
							// (только marked-клетки, не вся карта). На паузе не тикает.
							_jobAuditRealTimer += realDelta;
							if (_jobAuditRealTimer >= JobValidator.AuditIntervalRealSec)
							{
								_jobAuditRealTimer = 0f;
								using (GameProfiler.ScopeCustom("Simulation.JobAudit"))
								{
									jobAuditFixedTotal += JobValidator.Instance.Tick(_pool, _ctx);
								}
							}

							bool isLastSubStep = (step == steps - 1);
							// Rebuild — раз в SpatialRebuildPeriodTicks тиков (не каждый
							// суб-степ): дешёвый O(N), но при 16 суб-степах/тик на 100x
							// давал ×16 проходов. Гарантированно rebuild на последнем
							// суб-степе тика, чтобы снапшот/рендер видели свежую сетку.
							int rebuildPeriod = _speedMultiplier >= 25f
								? (int)(SpatialRebuildPeriodTicks * (_speedMultiplier / 12f))
								: SpatialRebuildPeriodTicks;
							bool rebuildSpatial = isLastSubStep &&
								(_tickCounter - _lastSpatialRebuildTick >= rebuildPeriod);
							if (rebuildSpatial)
								_lastSpatialRebuildTick = (int)_tickCounter;
							// P0-1: суб-степ — ноль замеров. Stopwatch + PushScope (ToArray+
							// string.Join в CurrentPath) + Series(lock) + CheckRegression
							// (Clone+Sort 60 float) на КАЖДЫЙ суб-степ = ×2000/с.
							Phase2_ParallelUpdate(currentStepDt);
							Phase3a_ParallelBookkeeping(currentStepDt, isLastSubStep, updateNeedsEnvThisStep);
							Phase3b_SequentialCommit(currentStepDt, rebuildSpatial);
						}

						if (renderTimer.Elapsed.TotalSeconds >= GetMinSnapInterval())
						{
							renderTimer.Restart();
							using (GameProfiler.ScopeCustom("Simulation.Snapshots"))
							{
							GroundItemManager.Instance.GenerateSnapshot();
							StockpileManager.Instance.GenerateSnapshot();
							CropGrowthManager.Instance.GenerateSnapshot();
							PushSnapshot();
							}
						}

						// П.3: троттлинг событий склада — сливаем накопленные тоталы не чаще 200мс
						// (иначе 1000 Deposit/Withdraw за тик = 1000 CallDeferred в главный поток).
						// P0-3: скоуп — CallDeferred-пачка была невидимкой в дырке.
						using (GameProfiler.ScopeCustom("Simulation.EventThrottle"))
						{
							StockpileManager.Instance.TickEventThrottle(currentStepDt);
						}
					}

					if (jobAuditFixedTotal > 0)
						GD.Print($"[JobValidator] исправлено работ: {jobAuditFixedTotal}");
				}

				// При speed >= 25 уступаем квант времени планировщику Windows,
				// исключая 100% busy-loop зависание ОС при 100k агентах.
				if (_speedMultiplier >= 25f)
				{
					if (steps > 0)
						Thread.Yield();
					else
						Thread.Sleep(1);
				}
				else
				{
					Thread.Sleep(6);
				}
			}
			catch (AggregateException aggEx)
			{
				foreach (var inner in aggEx.Flatten().InnerExceptions)
				{
					GD.PrintErr($"[AgentSimulationThread] Параллельная ошибка: {inner.Message}\n{inner.StackTrace}");
				}
				Thread.Sleep(20);
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[AgentSimulationThread] Ошибка тика: {ex.Message}\n{ex.StackTrace}");
				Thread.Sleep(20);
			}
		}
	}

	/// <summary>
	/// Масштабирует интервал (в игровом времени) под скорость, чтобы частота
	/// вызовов в реальном времени (speed / interval) не росла линейно со скоростью.
	/// При speed &lt;= IntervalScaleSpeed возвращает базовый интервал без изменений.
	/// PERF F2 (уровень 3): выше IntervalScaleSpeedHi (64x) масштаб квадратичный —
	/// на 100x диспетчер вызывается ~1 раз за тик (16 суб-степов), а не каждые
	/// 2–3 суб-степа. Поведение не меняется: claim-проходы покрывают чанки
	/// round-robin'ом, опоздание назначения на тик безвредно.
	/// </summary>
	private static float GetScaledInterval(float baseInterval, float speed)
	{
		float s = Math.Max(1f, speed / IntervalScaleSpeed);
		if (speed > IntervalScaleSpeedHi)
			s *= speed / IntervalScaleSpeedHi;
		return baseInterval * s;
	}

	/// <summary>
	/// Усиленное масштабирование для дешёвых фоновых проходов (sweep склада):
	/// выше 64x интервал растёт квадратично, чтобы проход случался не чаще
	/// ~1 раза за тик (16 суб-степов). Поведение не меняется — sweep лишь
	/// гарантирует наличие haul-работ, опоздание на тик безвредно.
	/// </summary>
	private static float GetScaledIntervalHi(float baseInterval, float speed)
	{
		float s = Math.Max(1f, speed / IntervalScaleSpeed);
		if (speed > IntervalScaleSpeedHi)
			s *= speed / IntervalScaleSpeedHi;
		return baseInterval * s;
	}

	private static float GetSimStepDelta(float speed)
	{
		// Крупный шаг на высокой скорости: грубее движение и риск туннелирования при большом dt (коллизии/путь могут проскакивать), зато пропускная способность выше.
		if (speed >= 100f) return 0.50f; // 100x: dt=0.5 — движение грубое, возможны рывки и туннелирование, но 16*0.5=8.0 игросек за проход.
		if (speed >= 25f)  return 0.20f; // 25x+: dt=0.2 — умеренное огрубление движения ради скорости.
		return BaseFixedDeltaTime;
	}

	private void UpdateSingleAgent(int i, float deltaTime, uint tickBucket)
	{
		// P0-1: горячий путь — ноль замеров. Stopwatch+Series+Count на каждого
		// 256-го агента давали ~8k lock(_cLock)/с при 2000 суб-степов/с.
		// Тяжёлые агенты ловятся квантом ForEachSplittable (ShrinkToMin), не сэмплом.
		UpdateSingleAgentInner(i, deltaTime, tickBucket, _pool.States[i]);
	}

	private void UpdateSingleAgentInner(int i, float deltaTime, uint tickBucket, AgentState state)
	{
		if (state == AgentState.Idle)
		{
			// Тайм-слайсинг: безработные обновляются батчами по 25% через битовую маску
			if ((i & 3) == tickBucket)
			{
				_wanderSystem.ExecuteParallel(i, deltaTime * 4.0f, _pool, _ctx);
			}
			return;
		}

		if (state == AgentState.Evacuating)
		{
			// Поведенческие реакции на потребности имеют приоритет над обычной эвакуацией.
			if (_pool.NeedsBehavior[i] != NeedBehavior.None)
				_needsSystem.ExecuteParallel(i, deltaTime, _pool, _ctx);
			else
				_wanderSystem.ExecuteParallel(i, deltaTime, _pool, _ctx);
			return;
		}

		var jobType = _pool.CurrentJobType[i];
		if (jobType != JobTypeId.None)
		{
			var handler = JobRegistry.GetHandler(jobType);
			handler?.ExecuteParallel(i, deltaTime, _pool, _ctx);
		}
	}

	private void Phase2_ParallelUpdate(float deltaTime)
	{
		using (GameProfiler.Scope())
		{
			int count = _pool.Capacity;
			uint tickBucket = _tickCounter & 3;

			// SCHEDULING RULE (PLAN.md §8.4): из body(i) запрещены ЛЮБЫЕ Godot Node API
			// (AddChild/GetNode/SetCell/MultiMesh/QueueRedraw/EmitSignal/ResourceLoader).
			// Разрешены: чистые вычисления, SoA-массивы, Interlocked/Volatile,
			// ConcurrentQueue.Enqueue (снапшоты), ThreadLocal, ParallelRng.
			// F-008: GPU Compute бэкенд шага агентов отключен во избежание двойного движения
			// (GPU StepAgents смещает координаты, после чего CPU UpdateSingleAgent/MoveTowards повторно сдвигал координаты).
			// Симуляция перемещения выполняется строго через векторизованный CPU SIMD.
			if (ActiveBackend == SimulationBackend.GpuCompute)
			{
				ActiveBackend = SimulationBackend.CpuSimd;
			}

			if (count < ParallelThreshold)
			{
				int errors = 0;
				for (int i = 0; i < count; i++)
				{
					try { UpdateSingleAgent(i, deltaTime, tickBucket); }
					catch { errors++; }
				}
				if (errors > 0)
					GD.PrintErr($"[Phase2] ошибок UpdateSingleAgent: {errors}");
			}
			else
			{
				int errors = 0;
				DynamicWorkScheduler.Shared.ForEachSplittable(
					count,
					(s, e, q) =>
					{
						// SCHEDULING RULE (PLAN.md §8.4): тело — только чистые
						// вычисления + SoA-массивы; cached yield-check ПЕРЕД КАЖДЫМ
						// агентом (FIX круг-2 №4): timestamp кэшируется каждые 8,
						// но дедлайн проверяется каждый i — после 1 тяжёлого агента
						// следующий i сразу yield'ит. Хвост ~1 тяжёлый агент + квант.
						// FIX круг-2 №7: локальный счётчик — без CAS на исключение.
						int localErrors = 0;
						int p = 0;
						long cached = 0;
						int sinceRefresh = 0;
						try
						{
							for (int i = s; i < e; i++)
							{
								// PLAN.md §62: для легких агентов таймер Stopwatch проверяется каждые 32 агента,
								// предотвращая деградацию конвейера команд CPU при 100k агентах.
								if (q.ShouldYieldCached(ref cached, ref sinceRefresh, 32))
									break;
								UpdateSingleAgent(i, deltaTime, tickBucket);
								p++;
								sinceRefresh++;
							}
						}
						catch
						{
							for (int i = s + p; i < e; i++)
							{
								try { UpdateSingleAgent(i, deltaTime, tickBucket); }
								catch { localErrors++; }
								p++;
							}
							localErrors++;
						}
						if (localErrors > 0)
							Interlocked.Add(ref errors, localErrors);
						return p;
					},
					WorkKind.Fast,
					"Simulation.Phase2_Balance");
				errors += DynamicWorkScheduler.Shared.LastPhaseErrorCount;
				if (errors > 0)
					GD.PrintErr($"[Phase2] ошибок UpdateSingleAgent: {errors}");
			}
		}
	}

	// ===== Наблюдаемость Phase3a (постоянные счётчики: пригодятся для будущих фич) =====
	// Считаются ЛОКАЛЬНО в теле диапазона (обычные int), публикуются одним
	// Interlocked.Add на БАТЧ (не на агента) — горячий путь не нагружают.
	// Смысл: видеть, сколько агентов реально входят в state-зависимые ветки
	// (голод/сон/миграция) и сколько меняет клетку, — без этого стоимость
	// Phase3a не разложить на «структурную» и «зависимую от мира».
	// Тайминги блоков Phase3a (тики Stopwatch): needs / cells / rest-тело /
	[StructLayout(LayoutKind.Explicit, Size = 128)]
	private struct Phase3aWorkerSlot
	{
		[FieldOffset(0)] public long TicksNeeds;
		[FieldOffset(8)] public long TicksCells;
		[FieldOffset(16)] public long TicksRest;
		[FieldOffset(24)] public long CountAgents;
		[FieldOffset(32)] public long CountIdle;
		[FieldOffset(40)] public long CountSlice;
		[FieldOffset(48)] public long CountHungry;
		[FieldOffset(56)] public long CountSleepy;
		[FieldOffset(64)] public long CountEnv;
		[FieldOffset(72)] public long CountChanged;
	}

	private readonly Phase3aWorkerSlot[] _dbgSlots = new Phase3aWorkerSlot[LoadMonitor.MaxSupportedDop];
	private long _dbgFusedWall;
	private long _dbgCalls;
	private const int DbgTickNeeds = 0;
	private const int DbgTickCells = 1;
	private const int DbgTickRest = 2;
	private const int DbgTickFusedWall = 3;
	private const int DbgTickRestWall = 4;

	private const int DbgAgents = 0;
	private const int DbgIdle = 1;
	private const int DbgSlice = 2;
	private const int DbgHungry = 3;
	private const int DbgSleepy = 4;
	private const int DbgEnv = 5;
	private const int DbgChanged = 6;
	private const int DbgCalls = 7;
	private long _dbgReportAt;
	// Буфер для диагностики диспетчера (только sim-поток, раз в секунду).
	private readonly int[] _diagIdleBuf = new int[64];

	/// <summary>
	/// Раз в секунду: сколько чанков ВООБЩЕ имеют работы и сколько из них — без
	/// единого бездельника рядом.
	///
	/// Зачем: диспетчер сводит пару «работа ↔ рабочий» ТОЛЬКО внутри одного
	/// чанка (16×16 тайлов): DispatchChunk берёт бездельников своего чанка и
	/// работы своего же чанка. Дальний глобальный проход — добивка с жёстким
	/// капом (≤128 кандидатов, ≤8 назначений, не чаще раза в 2 с). Поэтому если
	/// толпа бездельников стоит в одном месте (еда/склад в центре), а работы
	/// размазаны по всей карте, дальние чанки не будут взяты НИКОГДА — визуально
	/// это и выглядит как «полосы»: где-то пашут, где-то не тронуто вообще.
	/// </summary>
	private void ReportDispatchDistribution()
	{
		var index = JobDispatcher.Instance.JobIndex;
		var idle = JobDispatcher.Instance.IdleWorkers;
		const int chunkDim = JobSpatialPyramid.ChunkGridDim;
		int chunksWithJobs = 0, chunksWithIdle = 0, chunksJobsNoIdle = 0;
		long jobsInNoIdleChunks = 0, idleInJobChunks = 0;
		int[] buf = _diagIdleBuf;
		for (int ci = 0; ci < chunkDim * chunkDim; ci++)
		{
			int jobs = index.GetChunkJobCount(ci);
			int workers = idle.CollectIdleWorkersInChunk(ci, buf.Length, buf, _pool);
			if (jobs > 0)
			{
				chunksWithJobs++;
				idleInJobChunks += workers;
				if (workers == 0)
				{
					chunksJobsNoIdle++;
					jobsInNoIdleChunks += jobs;
				}
			}
			if (workers > 0)
				chunksWithIdle++;
		}
		GD.Print($"[DISP] unclaimed={index.UnclaimedCount} total={index.TotalCount} " +
				 $"idle={idle.TotalIdleCount} | чанков с работами={chunksWithJobs} " +
				 $"с бездельниками={chunksWithIdle} работы-без-бездельников={chunksJobsNoIdle} " +
				 $"(работ там {jobsInNoIdleChunks}, бездельников в чанках с работами {idleInJobChunks})");
	}
	// Снимки для отчёта (переиспользуемые — без аллокаций в горячем пути).
	private readonly long[] _dbgTicksSnap = new long[5];
	private readonly long[] _dbgCountSnap = new long[8];

	/// <summary>
	/// Снять и обнулить всю «матрицу» наблюдаемости Phase3a одним проходом.
	/// Зовётся раз в секунду из sim-потока (воркеры уже joined) — без аллокаций.
	/// </summary>
	private void DrainPhase3aCounters()
	{
		Array.Clear(_dbgTicksSnap, 0, _dbgTicksSnap.Length);
		Array.Clear(_dbgCountSnap, 0, _dbgCountSnap.Length);
		for (int s = 0; s < _dbgSlots.Length; s++)
		{
			ref var st = ref _dbgSlots[s];
			_dbgTicksSnap[DbgTickNeeds] += st.TicksNeeds;
			_dbgTicksSnap[DbgTickCells] += st.TicksCells;
			_dbgTicksSnap[DbgTickRest] += st.TicksRest;
			_dbgCountSnap[DbgAgents] += st.CountAgents;
			_dbgCountSnap[DbgIdle] += st.CountIdle;
			_dbgCountSnap[DbgSlice] += st.CountSlice;
			_dbgCountSnap[DbgHungry] += st.CountHungry;
			_dbgCountSnap[DbgSleepy] += st.CountSleepy;
			_dbgCountSnap[DbgEnv] += st.CountEnv;
			_dbgCountSnap[DbgChanged] += st.CountChanged;
			st = default;
		}
		_dbgTicksSnap[DbgTickFusedWall] = _dbgFusedWall;
		_dbgTicksSnap[DbgTickRestWall] = 0;
		_dbgCountSnap[DbgCalls] = _dbgCalls;
		_dbgFusedWall = 0;
		_dbgCalls = 0;
	}

	private void Phase3a_ParallelBookkeeping(float deltaTime, bool rebuildSpatialGrid, bool updateNeedsEnv = false)
	{
		using (GameProfiler.Scope())
		{
			int count = _pool.Capacity;
			uint tickBucket = _tickCounter & 3;

			if (count < ParallelThreshold)
			{
				for (int i = 0; i < count; i++)
				{
					BookkeepSingleAgent(i, deltaTime, tickBucket, updateNeedsEnv);
				}
			}
			else
			{
				// Тайм-слайсинг потребностей при мега-популяциях (>= 2048 агентов, PLAN.md §37, §44, §61):
				// Делим популяцию на непрерывные срезы с масштабированием под размер пула:
				// 2k-64k: 4 среза (x4), 64k-256k: 8 срезов (x8), 256k+: 16 срезов (x16).
				// Снижает нагрузку Phase3a в 4-16 раз и удерживает срез в L2/L3 кэше процессора!
				bool useSlicing = count >= 2048;
				int sliceCount = 1;
				if (useSlicing)
				{
					if (count >= 262144) sliceCount = 16;
					else if (count >= 65536) sliceCount = 8;
					else sliceCount = 4;
				}
				int sliceSize = (count + sliceCount - 1) / sliceCount;
				int sliceMask = sliceCount - 1;
				int sliceIdx = useSlicing ? (int)(_tickCounter & (uint)sliceMask) : 0;
				int sliceStart = sliceIdx * sliceSize;
				int sliceEnd = Math.Min(count, sliceStart + sliceSize);
				int sliceLen = Math.Max(0, sliceEnd - sliceStart);
				float effDeltaTime = useSlicing ? (deltaTime * (float)sliceCount) : deltaTime;

				long dbgFused0 = Stopwatch.GetTimestamp();
				using (GameProfiler.ScopeCustom("Simulation.Phase3a_Fused"))
				{
					DynamicWorkBalancer.ForEachRange(sliceLen,
						(slot, s, e) =>
						{
							int actualS = sliceStart + s;
							int actualE = sliceStart + e;

							if (EnableConsoleDebugAudit)
							{
								long tn0 = Stopwatch.GetTimestamp();
								SimdNeedsBatch.UpdateNeeds(_pool, actualS, actualE, effDeltaTime, updateNeedsEnv);
								long tn1 = Stopwatch.GetTimestamp();
								SimdNeedsBatch.UpdateCells(_pool, actualS, actualE, effDeltaTime);
								long tn2 = Stopwatch.GetTimestamp();

								int idle = 0, slice = 0, hungry = 0, sleepy = 0, env = 0, changed = 0;
								for (int i = actualS; i < actualE; i++)
								{
									AgentState state = _pool.States[i];
									bool cellChanged = _pool.CellStayTime[i] == 0f;
									if (state == AgentState.Idle)
									{
										idle++;
										if (useSlicing || (i & 3) == tickBucket)
										{
											slice++;
											if (_pool.Hunger[i] > AgentNeedsConfig.HungerSeekThreshold) hungry++;
											if (_pool.Sleep[i] > AgentNeedsConfig.SleepRestThreshold
												|| _pool.Fatigue[i] > AgentNeedsConfig.FatigueRestThreshold) sleepy++;
											if (_pool.EnvironmentSatisfaction[i] < AgentNeedsConfig.EnvironmentMigrateThreshold) env++;
										}
									}
									if (cellChanged) changed++;
									BookkeepSingleAgentRest(i, effDeltaTime, tickBucket, cellChanged, state, useSlicing);
								}
								long tn3 = Stopwatch.GetTimestamp();

								if ((uint)slot < (uint)_dbgSlots.Length)
								{
									ref var st = ref _dbgSlots[slot];
									st.TicksNeeds += tn1 - tn0;
									st.TicksCells += tn2 - tn1;
									st.TicksRest += tn3 - tn2;
									st.CountAgents += actualE - actualS;
									st.CountIdle += idle;
									st.CountSlice += slice;
									st.CountHungry += hungry;
									st.CountSleepy += sleepy;
									st.CountEnv += env;
									st.CountChanged += changed;
								}
							}
							else
							{
								SimdNeedsBatch.UpdateNeeds(_pool, actualS, actualE, effDeltaTime, updateNeedsEnv);
								SimdNeedsBatch.UpdateCells(_pool, actualS, actualE, effDeltaTime);

								for (int i = actualS; i < actualE; i++)
								{
									AgentState state = _pool.States[i];
									bool cellChanged = _pool.CellStayTime[i] == 0f;
									BookkeepSingleAgentRest(i, effDeltaTime, tickBucket, cellChanged, state, useSlicing);
								}
							}
						},
						"Simulation.Phase3a_Fused_Balance");
				}
				if (EnableConsoleDebugAudit)
				{
					_dbgFusedWall += Stopwatch.GetTimestamp() - dbgFused0;
					_dbgCalls++;
				}

				// --- Отчёт Phase3a раз в секунду из sim-потока (наблюдаемость) ---
				long dbgNow = Stopwatch.GetTimestamp();
				if (_dbgReportAt == 0)
				{
					_dbgReportAt = dbgNow;
				}
				else
				{
					double dbgWinSec = (dbgNow - _dbgReportAt) / (double)Stopwatch.Frequency;
					if (dbgWinSec >= 1.0)
					{
						_dbgReportAt = dbgNow;
						DrainPhase3aCounters();
						long calls = _dbgCountSnap[DbgCalls];
						if (calls > 0)
						{
							long tNeeds = _dbgTicksSnap[DbgTickNeeds];
							long tCells = _dbgTicksSnap[DbgTickCells];
							long tRest = _dbgTicksSnap[DbgTickRest];
							double perCall = 1000.0 / Stopwatch.Frequency / calls;
							double cpuMsPerSec = (tNeeds + tCells + tRest) / (double)Stopwatch.Frequency * 1000.0 / dbgWinSec;
							if (EnableConsoleDebugAudit)
							{
								GD.Print($"[P3A] cap={count} calls/s={calls / dbgWinSec:F0} | ms/call: " +
										 $"needs={tNeeds * perCall:F3} cells={tCells * perCall:F3} rest={tRest * perCall:F3} | " +
										 $"wall/call: fused={_dbgTicksSnap[DbgTickFusedWall] * perCall:F3} rest={_dbgTicksSnap[DbgTickRestWall] * perCall:F3} | " +
										 $"cpu_ms/s={cpuMsPerSec:F1} | rest@call: idle={_dbgCountSnap[DbgIdle] / (double)calls:F0} " +
										 $"slice={_dbgCountSnap[DbgSlice] / (double)calls:F0} hungry={_dbgCountSnap[DbgHungry] / (double)calls:F0} " +
										 $"sleepy={_dbgCountSnap[DbgSleepy] / (double)calls:F0} env={_dbgCountSnap[DbgEnv] / (double)calls:F0} " +
										 $"changed={_dbgCountSnap[DbgChanged] / (double)calls:F0}");
								ReportDispatchDistribution();
							}
						}
					}
				}
				int eTotal = DynamicWorkScheduler.Shared.LastPhaseErrorCount;
				if (eTotal > 0)
					GD.PrintErr($"[Phase3a] ошибок Bookkeeping: {eTotal}");
			}
		}
	}

	/// <summary>
	/// Фаза обновления базовых потребностей (SoA-расширение).
	/// deltaTime — игровые секунды (скорость уже учтена через accumulator/шаги),
	/// отдельно на _speedMultiplier домножать НЕ нужно.
	/// Вызывается из BookkeepSingleAgent первой строкой, поэтому покрывает все состояния.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void UpdateNeedsSingleAgent(int i, float deltaTime, bool updateEnv)
	{
		// P0-1: горячий путь — ноль счётчиков. ToString() + lock на агента.
		SimdNeedsBatch.UpdateNeedsSingle(_pool, i, deltaTime, updateEnv);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void BookkeepSingleAgent(int i, float deltaTime, uint tickBucket, bool updateNeedsEnv = false)
	{
		UpdateNeedsSingleAgent(i, deltaTime, updateNeedsEnv);

		// G3: cell-tracking делегирован SimdNeedsBatch.UpdateCellSingle
		// (та же семантика, возвращает cellChanged для Idle-ветки ниже).
		bool cellChanged = SimdNeedsBatch.UpdateCellSingle(_pool, i, deltaTime);

		BookkeepSingleAgentRest(i, deltaTime, tickBucket, cellChanged);
	}

	/// <summary>
	/// G3: остаток bookkeeping БЕЗ потребностей и БЕЗ записи cell-tracking —
	/// только Idle/Evacuating-ветки. В параллельном пути Phase3a вызывается после
	/// батчей UpdateNeeds/UpdateCells; Needs уже обновлены, CurrentCellX/Y и
	/// CellStayTime уже записаны батчем. cellChanged передаётся параметром:
	/// полный путь даёт его из UpdateCellSingle, параллельный — эвристикой
	/// CellStayTime[i] == 0f (см. комментарий в Phase3a_ParallelBookkeeping).
	/// Тайм-слайсинг `(i&amp;3)==tickBucket`, TryAssignNeedsBehavior (сканы под lock),
	/// TryAssignJob (ParallelRng) и UpdateWorkerChunk (lock/striped) — НЕ батчатся.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void BookkeepSingleAgentRest(int i, float deltaTime, uint tickBucket, bool cellChanged)
	{
		BookkeepSingleAgentRest(i, deltaTime, tickBucket, cellChanged, _pool.States[i], false);
	}

	/// <summary>
	/// То же, но состояние агента уже прочитано вызывающим: параллельный проход
	/// Phase3a читает States[i] один раз — и для счётчиков наблюдаемости, и для
	/// ветки. Так на агента приходится одно чтение States вместо двух.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void BookkeepSingleAgentRest(int i, float deltaTime, uint tickBucket, bool cellChanged, AgentState state, bool rangeSliced = false)
	{
		// §119 P2: Worker обслуживается ЗОНОЙ, а не общим диспатчером.
		if (state == AgentState.Idle && _pool.Classes[i] == AgentClass.Worker)
		{
			// Тайм-слайсинг как у Laborer'а; выдача ТОЛЬКО из очереди своей зоны.
			if (rangeSliced || (i & 3) == tickBucket)
			{
				float timerDelta = rangeSliced ? deltaTime : (deltaTime * 4.0f);
				if (_pool.NeedsRetryTimer[i] > 0f)
				{
					_pool.NeedsRetryTimer[i] -= timerDelta;
				}
				else
				{
					// Нужды Idle-Worker'а — обычным NeedsJobSystem (тот же путь).
					if (_needsSystem.TryAssignNeedsBehavior(i, _pool, _ctx))
						return;
				}
				_pool.JobSearchTimer[i] -= timerDelta;
				if (_pool.JobSearchTimer[i] > 0f)
					return;
				_pool.JobSearchTimer[i] = WorkerZoneJobSystem.JobSearchIntervalSec;
				_workerJobSystem.TryAssignZoneJob(i, _pool, _ctx);
			}
			return;
		}

		// §119 P2: прерывание РАБОТАЮЩЕГО Worker'а нуждами (+5 гистерезис).
		// Тайм-слайсинг (i & 3) == tickBucket — как у диспатчера.
		if (state != AgentState.Idle && state != AgentState.Evacuating
			&& _pool.Classes[i] == AgentClass.Worker
			&& _pool.NeedsBehavior[i] == NeedBehavior.None
			&& (i & 3) == tickBucket)
		{
			if (_workerJobSystem.TryInterruptForNeeds(i, _pool, _ctx))
				return; // агент ушёл в Needs-поведение, работа вернулась в зону
		}

		if (state == AgentState.Idle)
		{
			// Свободный агент переместился — обновляем его чанк в сетке бездельников,
			// чтобы локальный поиск работы видел актуальное положение.
			if (cellChanged)
			{
				JobDispatcher.Instance.IdleWorkers.UpdateWorkerChunk(i, _pool);
			}

			// Тайм-слайсинг таймеров поиска работы: если уже срезан по диапазону, выполняется для всех в срезе
			if (rangeSliced || (i & 3) == tickBucket)
			{
				float timerDelta = rangeSliced ? deltaTime : (deltaTime * 4.0f);
				if (_pool.NeedsRetryTimer[i] > 0f)
				{
					_pool.NeedsRetryTimer[i] -= timerDelta;
				}
				else
				{
					// Сначала потребности: голодный/уставший свободный агент уходит
					// удовлетворять нужду и НЕ берёт обычную работу в этом тике.
					if (_needsSystem.TryAssignNeedsBehavior(i, _pool, _ctx))
						return;
				}
				_pool.JobSearchTimer[i] -= timerDelta;
				if (_pool.JobSearchTimer[i] > 0f)
					return;
				_pool.JobSearchTimer[i] = 6.0f + (float)ParallelRng.NextDouble() * 6.0f;
				_wanderSystem.TryAssignJob(i, _pool, _ctx);
			}
			return;
		}

		if (state == AgentState.Evacuating)
		{
			if (_pool.NeedsBehavior[i] != NeedBehavior.None)
				_needsSystem.Commit(i, _pool);
			else
				_wanderSystem.Commit(i, deltaTime, _pool, _ctx);
			return;
		}
	}

	// ==================================================================
	// Сторож НЕДОСТИЖИМЫХ целей (баг #3: «ИИ бесконечно идёт к цели за рекой»).
	//
	// Почему штатного детектора застревания не хватает: хендлеры снимают
	// задачу по pool.StuckTimer >= 3с (TreeChoppingJobHandler.cs:72,
	// ConstructionJobHandler.cs:97, ...), а StuckTimer растёт ТОЛЬКО когда
	// агент вообще не сдвинулся (AgentMovementService.MoveToPoint, шаг 3).
	// У берега крупной реки агент не «стоит», а СКОЛЬЗИТ вдоль воды — и
	// MoveToPoint сбрасывает StuckTimer на каждом таком шаге
	// (AgentMovementService.cs:249 в быстром пути и :346 в ветке скольжения).
	// Итог: таймер застревания не набирается НИКОГДА, задача не помечается
	// недостижимой, не снимается — агент идёт «прямо» в реку бесконечно.
	// (Запрос пути при этом тоже молча падает в кулдаун:
	// AgentMovementService.RequestHierarchicalPath.cs:185-190 — путь по земле
	// через разделяющую реку/горы не существует, HierarchicalPathfinder
	// возвращает false, а провал никуда не сообщается.)
	//
	// Здесь меряется ПРОГРЕСС, а не факт движения: если агент, идущий к цели
	// задачи напрямую (без waypoints-маршрута), за UnreachableNoProgressSeconds
	// игровых секунд не приблизился к цели — цель физически недостижима.
	// Тогда задача снимается с кулдауном недостижимости
	// (ReleaseJobWorkerForStuck → JobIndex.MarkJobUnreachable): её не выдадут
	// другим агентам на время кулдауна, а сам агент возьмёт другую работу.
	// Публичные сигнатуры не меняются — только приватные поля/метод.
	// ==================================================================

	/// <summary>Сколько игровых секунд без приближения к цели = цель недостижима.</summary>
	private const float UnreachableNoProgressSeconds = 8f;

	/// <summary>Кулдаун задачи после снятия как недостижимой (мс) — как JobValidator (30с там, здесь мягче).</summary>
	private const int UnreachableReleaseCooldownMs = 15000;

	/// <summary>Порог «приблизился» (пиксели): дрожание у стены не сбрасывает таймер.</summary>
	private const float UnreachableProgressEpsilonPx = 2f;

	/// <summary>Ближе этого (пиксели) цели не трогаем — агента у самой цели разбирает штатный Commit.</summary>
	private const float UnreachableMinTargetDistPx = 48f;

	/// <summary>Игровые секунды без прогресса к цели (индекс = агент).</summary>
	private float[] _noProgressSeconds;

	/// <summary>Лучшая (минимальная) дистанция до цели с последнего прогресса, px.</summary>
	private float[] _bestTargetDistPx;

	/// <summary>Счётчик суб-степов: сторож O(N) считается 1 раз на 4 суб-степа.</summary>
	private int _unreachableWatchdogTick;

	/// <summary>
	/// Снимает задачи с заведомо недостижимыми целями (за рекой/горой/на другом
	/// континенте) и помечает их кулдауном — вместо бесконечного похода «прямо в стену».
	/// Вызывается ТОЛЬКО из sim-потока (Phase3b, последовательная фаза): мутирует
	/// IdleWorkers/JobIndex так же, как это делают хендлеры из своего Commit.
	/// </summary>
	private void WatchdogUnreachableTargets(float deltaTime)
	{
		// Проход O(N) — дорогой: 1 раз на 4 суб-степа, но копим 4 шага,
		// чтобы «секунды без прогресса» остались в игровых секундах.
		if ((++_unreachableWatchdogTick & 3) != 0)
			return;

		int count = _pool.Capacity;
		if (_noProgressSeconds == null || _noProgressSeconds.Length != count)
		{
			_noProgressSeconds = new float[count];
			_bestTargetDistPx = new float[count];
			Array.Fill(_bestTargetDistPx, float.MaxValue);
		}

		float dt = deltaTime * 4f;
		for (int i = 0; i < count; i++)
		{
			AgentState state = _pool.States[i];

			// Только идущие К ЦЕЛИ ЗАДАЧИ и только БЕЗ маршрута waypoints:
			// при следовании по waypoints агент законно обходит препятствие, и
			// дистанция до цели по прямой может временно расти — это не «застрял».
			if ((state != AgentState.MovingToSource && state != AgentState.MovingToTarget)
				|| _pool.CurrentJobId[i] < 0
				|| _pool.WaypointCount[i] > 0)
			{
				_noProgressSeconds[i] = 0f;
				_bestTargetDistPx[i] = float.MaxValue;
				continue;
			}

			float dx = _pool.TargetPositionX[i] - _pool.PositionX[i];
			float dy = _pool.TargetPositionY[i] - _pool.PositionY[i];
			float distSq = dx * dx + dy * dy;

			// Стоит у самой цели (или близко) — это разбирает штатный Commit хендлера.
			if (distSq <= UnreachableMinTargetDistPx * UnreachableMinTargetDistPx)
			{
				_noProgressSeconds[i] = 0f;
				_bestTargetDistPx[i] = float.MaxValue;
				continue;
			}

			float dist = MathF.Sqrt(distSq);
			float best = _bestTargetDistPx[i];
			if (dist < best - UnreachableProgressEpsilonPx)
			{
				// Реально приблизился — маршрут рабочий, таймер обнуляем.
				_bestTargetDistPx[i] = dist;
				_noProgressSeconds[i] = 0f;
				continue;
			}
			if (dist < best)
				_bestTargetDistPx[i] = dist;

			_noProgressSeconds[i] += dt;
			if (_noProgressSeconds[i] < UnreachableNoProgressSeconds)
				continue;

			_noProgressSeconds[i] = 0f;
			_bestTargetDistPx[i] = float.MaxValue;
			// Цель недостижима: снимаем задачу с кулдауном (агент — Idle,
			// задача — под IsJobOnCooldown). Хендлер сам получит OnCancel и
			// вернёт резервы (StockpileHaulingJobHandler.OnCancel:215-234).
			try
			{
				JobDispatcher.Instance.ReleaseJobWorkerForStuck(i, _pool, _ctx, UnreachableReleaseCooldownMs);
			}
			catch (Exception ex)
			{
				GD.PrintErr($"[UnreachableWatchdog] снятие задачи #{i}: {ex.Message}");
			}
		}
	}

	private void Phase3b_SequentialCommit(float deltaTime, bool rebuildSpatialGrid)
	{
		using (GameProfiler.Scope())
		{
			int count = _pool.Capacity;

			// Баг #3: снять задачи с физически недостижимыми целями ДО коммита
			// (последовательно, sim-поток) — иначе агент вечно идёт «прямо» в реку.
			WatchdogUnreachableTargets(deltaTime);

			// P1: разбивка для оверлея — видно, где сидят 7.3с: Rebuild или Commit.
			// ScopeCustom вне горячего цикла: один Scope на фазу, не на агента.
			if (rebuildSpatialGrid)
			{
				// P3: rebuild через полосы — каждая полоса пишет только в свои
				// ячейки (гонок нет), слияние активных ячеек после join.
				using (GameProfiler.ScopeCustom("Simulation.Phase3b_Rebuild"))
				{
					_ctx.SpatialGrid.RebuildParallel(count,
						_pool.CurrentCellX, _pool.CurrentCellY, _pool.NextInSpatialCell);
				}
			}

			// Commit — дорого (TakeItems/SpawnItems/Release под lock'ами).
			// Fast-path: если нет задач в мире или все агенты свободны — коммитить нечего!
			int totalJobs = JobDispatcher.Instance.JobIndex.TotalCount;
			int totalIdle = JobDispatcher.Instance.IdleWorkers.TotalIdleCount;
			int busyWorkers = count - totalIdle;

			if (totalJobs > 0 && busyWorkers > 0)
			{
				using (GameProfiler.ScopeCustom("Simulation.Phase3b_Commit"))
				{
					if (busyWorkers <= ParallelThreshold || count < ParallelThreshold)
					{
						for (int i = 0; i < count; i++)
							CommitSingleAgent(i, deltaTime);
					}
					else
					{
						DynamicWorkBalancer.ForEachRange(count, (s, e) =>
						{
							for (int i = s; i < e; i++)
							{
								var state = _pool.States[i];
								if (state == AgentState.Idle || state == AgentState.Evacuating)
									continue;

								var jobType = _pool.CurrentJobType[i];
								if (jobType != JobTypeId.None)
								{
									var handler = JobRegistry.GetHandler(jobType);
									handler?.Commit(i, deltaTime, _pool, _ctx);
								}
							}
						}, "Simulation.Phase3b_Balance");
					}
				}
			}
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void CommitSingleAgent(int i, float deltaTime)
	{
		var state = _pool.States[i];
		if (state == AgentState.Idle || state == AgentState.Evacuating)
			return;

		var jobType = _pool.CurrentJobType[i];
		if (jobType != JobTypeId.None)
		{
			// P0-1: горячий путь — ноль замеров. Stopwatch + строковая конкатенация
			// типа + lock(_cLock) на КАЖДОГО агента каждый суб-степ (~2M/с при 1k
			// агентов × 2000 суб-степов). Ошибки считает планировщик (LastPhaseErrorCount).
			var handler = JobRegistry.GetHandler(jobType);
			handler?.Commit(i, deltaTime, _pool, _ctx);
		}
	}

	private void PushSnapshot()
	{
		// Finding 6 & PERF: ring из 4 буферов вместо постоянного new Vector2[10000] (80 КБ на 30-60 Гц = Gen2 GC спайки).
		// Резерв очереди = 2, ротация = 4: гарантия полного отсутствия race/tearing с консьюмером.
		int slot = _posSnapCursor % 4;
		_posSnapCursor++;
		var snapshot = _positionSnapRing[slot];
		if (snapshot == null || snapshot.Length != _pool.Capacity)
		{
			snapshot = new Vector2[_pool.Capacity];
			_positionSnapRing[slot] = snapshot;
		}

		_pool.CopyPositionsTo(snapshot);

		PositionQueue.Enqueue(snapshot);
		while (PositionQueue.Count > 2)
			PositionQueue.TryDequeue(out _);
	}

	public bool Stop(int timeoutMs = 5000)
	{
		_running = false;
		bool cleanStop = true;
		try
		{
			if (_simulationTask != null)
			{
				cleanStop = _simulationTask.Wait(timeoutMs);
				if (!cleanStop)
					GD.PrintErr($"[AgentSimulationThread] Таймаут остановки симуляции ({timeoutMs} мс). Поток всё ещё активен!");
			}
		}
		catch (Exception ex)
		{
			cleanStop = false;
			GD.PrintErr($"[AgentSimulationThread] Исключение при остановке симуляции: {ex.Message}");
		}

		// FIX круг-2 №9: восстановить MinThreads, изменённые в Start().
		if (_minThreadsAdjusted)
		{
			try { ThreadPool.SetMinThreads(_prevMinWorkerThreads, _prevMinCompletionThreads); }
			catch { }
			_minThreadsAdjusted = false;
		}

		return cleanStop;
	}

	public void Dispose()
	{
		Stop();
	}
}
