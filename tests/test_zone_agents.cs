using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core;
using Game.Simulation;

namespace Game.Tests;

// Имя класса == basename файла (иначе Godot-генератор не эмитит ScriptPath).

// ============================================================================
// §119 P2: тест Worker'ов зон (task-16). Запуск:
//   godot_run_headless --script tests/run_zone_agents_test.gd
// Проверки приёмки:
//   (а) AgentClass: все агенты по умолчанию Laborer; поведение легаси не тронуто;
//   (б) Worker НЕ берёт чужое: DequeueNearest только своей зоны; чужая очередь пуста для него;
//   (в) самовалидация: фантомный jobId (нет в реестре) возвращается в очередь, клейма нет;
//   (г) DesiredWorkers → ровно N ближайших агентов переводится в Worker (BeginAssign);
//       уменьшение заявки разжимает лишних;
//   (д) нужды прерывают Worker'а: работа возвращается в очередь ЗОНЫ, прогресс в клетке сохранён;
//   (е) 60с-правило: свежая работа в очереди зоны не отдаётся Laborer'у (охрана пирамиды),
//       после 60с простоя — доступна;
//   (ж) watchdog (stuck): работа Worker'а возвращается в очередь зоны, кулдаун НЕ ставится,
//       следующий Worker зоны подбирает её.
// ============================================================================

public partial class test_zone_agents : RefCounted
{
	private const int MapSize = 256;

	/// <summary>Всегда-да хендлер TreeChopping: клейм и OnStart без внешних условий.</summary>
	private sealed class AlwaysYesHandler : IJobHandler
	{
		/// <summary>Прогресс «в клетке» (имитация CellProgressStore P7б).</summary>
		public readonly CellProgressStore Progress = new();

		public JobTypeId TypeId => JobTypeId.TreeChopping;
		public JobExecutionType ExecutionType => JobExecutionType.Stationary;
		public JobPriorityTier DefaultPriority => JobPriorityTier.TreeChopping;
		public ToolRequirement RequiredTool => ToolRequirement.None;

		public bool CanAgentExecute(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx) => true;

		public void OnStart(int agentIndex, in JobData job, AgentDataPool pool, SimulationContext ctx)
		{
			pool.States[agentIndex] = AgentState.MovingToSource;
			pool.TargetCellX[agentIndex] = job.TargetX;
			pool.TargetCellY[agentIndex] = job.TargetY;
		}

		public void ExecuteParallel(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx) { }

		public void Commit(int agentIndex, float deltaTime, AgentDataPool pool, SimulationContext ctx) { }

		public void OnCancel(int agentIndex, AgentDataPool pool, SimulationContext ctx)
		{
			// Спека: прерывание сохраняет прогресс В КЛЕТКЕ (P7б).
			if (pool.WorkProgress[agentIndex] > 0f)
				Progress.SaveFraction(pool.TargetCellX[agentIndex], pool.TargetCellY[agentIndex],
					Math.Clamp(pool.WorkProgress[agentIndex] / 10f, 0f, 1f));
		}
	}

	private static AlwaysYesHandler _handler;

	public bool Run()
	{
		try
		{
			_handler = new AlwaysYesHandler();
			JobRegistry.Register(_handler);
			ZoneManager.Instance.ClearAll();
			ZoneJobRouter.Instance.Reset();
			ZoneJobRouter.TestClockOverrideMs = -1;
			// Чистая пирамида на весь прогон: тесты регистрируют работы с одним
			// jobId-пространством; клейма прошлых тестов не должны мешать.
			JobDispatcher.Instance.JobIndex.Clear();
			JobDispatcher.Instance.IdleWorkers.Clear();
			ZoneManager.SetMapBounds(1024, 1024);

			TestDefaultLaborer();
			GD.Print("[ZONE-AGENTS] (а) дефолт Laborer, легаси не тронуто: OK");

			TestDesiredWorkersTranslation();
			GD.Print("[ZONE-AGENTS] (г) DesiredWorkers → ровно N Worker'ов: OK");

			TestWorkerForeignZone();
			GD.Print("[ZONE-AGENTS] (б) Worker не берёт чужое: OK");

			TestSelfValidation();
			GD.Print("[ZONE-AGENTS] (в) самовалидация фантома: OK");

			TestNeedsInterrupt();
			GD.Print("[ZONE-AGENTS] (д) нужды → возврат в очередь зоны + прогресс: OK");

			TestSixtySecondRule();
			GD.Print("[ZONE-AGENTS] (е) 60с-правило для Laborer'а: OK");

			TestWatchdogReturn();
			GD.Print("[ZONE-AGENTS] (ж) watchdog возвращает работу в зону: OK");

			ZoneManager.Instance.ClearAll();
			ZoneJobRouter.Instance.Reset();
			ZoneJobRouter.TestClockOverrideMs = -1;
			GD.Print("==================================================");
			GD.Print("[TEST] ZONE AGENTS P2 PASSED! (Code 0)");
			GD.Print("==================================================");
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[TEST FAILED] {ex}");
			return false;
		}
	}

	private static (SimulationContext Ctx, AgentDataPool Pool) MakeWorld(int agents = 24)
	{
		var ground = new TileType[MapSize, MapSize];
		var trees = new bool[MapSize, MapSize];
		var solid = new bool[MapSize, MapSize];
		var walkable = new List<(int X, int Y)>(MapSize * MapSize);
		for (int x = 0; x < MapSize; x++)
			for (int y = 0; y < MapSize; y++)
				walkable.Add((x, y));
		var ctx = new SimulationContext(ground, null, trees, solid, walkable,
			new AgentSpatialGrid(MapSize, MapSize), new AgentMovementService(), new Random(42), 64);
		var pool = new AgentDataPool(agents);
		// Все idle-агенты: в центре карты, Idle, в пуле бездельников.
		for (int i = 0; i < agents; i++)
		{
			pool.CurrentCellX[i] = MapSize / 2;
			pool.CurrentCellY[i] = MapSize / 2;
			pool.States[i] = AgentState.Idle;
			pool.CurrentJobId[i] = -1;
			pool.NeedsBehavior[i] = NeedBehavior.None;
			JobDispatcher.Instance.IdleWorkers.AddIdleWorker(i, pool);
		}
		return (ctx, pool);
	}

	/// <summary>Регистрация РЕАЛЬНОЙ работы в JobIndex (Sweep чистит по реестру!) + пуш в роутер.</summary>
	private static int RegisterAndPush(int x, int y, long nowMs)
	{
		var jd = new JobData
		{
			TypeId = JobTypeId.TreeChopping,
			ExecutionType = JobExecutionType.Stationary,
			PriorityTier = JobPriorityTier.TreeChopping,
			RequiredTool = ToolRequirement.None,
			TargetX = x, TargetY = y, StandX = x, StandY = y,
			MaxWorkers = 1, WorkDuration = 10f, IsActive = true
		};
		int id = JobDispatcher.Instance.RegisterJob(jd);
		ZoneJobRouter.Instance.Push(JobTypeId.TreeChopping, x, y, id, nowMs);
		return id;
	}

	/// <summary>Чистка зон+роутера+пирамиды между тестами: незакрытые работы прошлых
	/// тестов не должны быть доступны claim-скану следующего.</summary>
	private static void CleanupWorld()
	{
		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
		JobDispatcher.Instance.JobIndex.Clear();
	}

	// (а) все агенты стартуют Laborer; AssignedZoneId = -1; массив классов не съеден GC.
	private void TestDefaultLaborer()
	{
		var pool = new AgentDataPool(16);
		for (int i = 0; i < pool.Capacity; i++)
		{
			if (pool.Classes[i] != AgentClass.Laborer)
				throw new Exception($"агент {i} не Laborer по умолчанию");
			if (pool.AssignedZoneId[i] != -1)
				throw new Exception($"агент {i} привязан к зоне {pool.AssignedZoneId[i]} без назначения");
		}
	}

	// (г) зона r=64 вокруг центра (128,128), DesiredWorkers=5 → 5 ближайших Idle-агентов
	// становятся Worker'ами; остальные остаются Laborer. Заявка 2 → двое дальних разжаты.
	private void TestDesiredWorkersTranslation()
	{
		var (ctx, pool) = MakeWorld(24);
		var wm = WorkZoneManager.Instance;
		if (!wm.CreateZoneAt(128, 128, 1UL << 1, 64, 5, out var zone, out string fail))
			throw new Exception($"зона не создалась: {fail}");

		var (assigned, recruited, released) = wm.BeginAssign(zone.Id, pool, ctx);
		if (assigned != 5 || recruited != 5)
			throw new Exception($"найм: assigned={assigned}, recruited={recruited} (ожидалось 5/5)");
		int workers = 0;
		for (int i = 0; i < pool.Capacity; i++)
			if (pool.Classes[i] == AgentClass.Worker)
			{
				workers++;
				if (pool.AssignedZoneId[i] != zone.Id)
					throw new Exception($"Worker {i} привязан к чужой зоне {pool.AssignedZoneId[i]}");
				// Worker не в общем пуле бездельников — пирамида его не тронет.
				if (JobDispatcher.Instance.IdleWorkers.IsIdle(i))
					throw new Exception($"Worker {i} остался в общем пуле бездельников");
			}
		if (workers != 5)
			throw new Exception($"Worker'ов в пуле: {workers} (ожидалось 5)");

		// Урезание заявки: 5 → 2, дальние разжимаются.
		wm.SetDesiredWorkers(zone.Id, 2);
		var (assigned2, rec2, rel2) = wm.BeginAssign(zone.Id, pool, ctx);
		if (assigned2 != 2 || rel2 != 3)
			throw new Exception($"урезание: assigned={assigned2}, released={rel2} (ожидалось 2/3)");
		workers = 0;
		for (int i = 0; i < pool.Capacity; i++)
			if (pool.Classes[i] == AgentClass.Worker)
				workers++;
		if (workers != 2)
			throw new Exception($"после урезания Worker'ов: {workers} (ожидалось 2)");

		CleanupWorld();
	}

	// (б) две зоны одного типа; Worker зоны A не может вытащить работу из очереди B,
	// и выданная ему работа A не числится за B.
	private void TestWorkerForeignZone()
	{
		var (ctx, pool) = MakeWorld(8);
		var wm = WorkZoneManager.Instance;
		if (!wm.CreateZoneAt(64, 128, 1UL << 1, 48, 1, out var zoneA, out _))
			throw new Exception("зона A не создалась");
		if (!wm.CreateZoneAt(192, 128, 1UL << 1, 48, 1, out var zoneB, out _))
			throw new Exception("зона B не создалась");
		ZoneJobRouter.Instance.ForceRebuild(1000);

		int idA = RegisterAndPush(64, 128, 1000);   // в зоне A
		int idB = RegisterAndPush(192, 128, 1000);  // в зоне B

		// Worker зоны A: DequeueNearest(zoneB) НЕ выдаёт работу B чужому... точнее:
		// API по zoneId — Worker A запрашивает ТОЛЬКО свою зону; вызов с чужим
		// zoneId разрешён, но реальный Worker его никогда не делает. Проверяем
		// выдачу по своей зоне и что после неё очередь A пуста, B — нет.
		if (!ZoneJobRouter.Instance.DequeueNearest(zoneA.Id, 64, 128, out var zj) || zj.JobId != idA)
			throw new Exception("Worker зоны A не получил свою работу");
		if (ZoneJobRouter.Instance.DequeueNearest(zoneA.Id, 64, 128, out _))
			throw new Exception("двойная выдача из зоны A");
		if (ZoneJobRouter.Instance.QueueDepth(zoneB.Id) != 1)
			throw new Exception("очередь зоны B пострадала от выдачи A");

		CleanupWorld();
	}

	// (в) фантомный jobId (нет в реестре): TryAssignZoneJob возвращает работу в
	// очередь и НЕ клеймит агента; следующая попытка с реальной работой успешна.
	private void TestSelfValidation()
	{
		var (ctx, pool) = MakeWorld(4);
		var wm = WorkZoneManager.Instance;
		if (!wm.CreateZoneAt(128, 128, 1UL << 1, 64, 1, out var zone, out _))
			throw new Exception("зона не создалась");
		ZoneJobRouter.Instance.ForceRebuild(1000);

		// Фантом: jobId 987654 в реестре отсутствует (Sweep бы его вычистил,
		// но Worker обязан сам защититься на выдаче — P1-заметка).
		ZoneJobRouter.Instance.Push(JobTypeId.TreeChopping, 128, 128, 987654, 1000);

		int agent = 0;
		// Агент — Worker зоны (промоушен вручную: тестируем выдачу, не найм).
		pool.Classes[agent] = AgentClass.Worker;
		pool.AssignedZoneId[agent] = zone.Id;
		// Симулируем выдачу фантома через систему: DequeueNearest выдал фантом →
		// TryAssignZoneJob отверг, вернул в очередь.
		bool assigned = WorkerZoneJobSystem.Instance.TryAssignZoneJob(agent, pool, ctx);
		if (ZoneJobRouter.Instance.QueueDepth(zone.Id) != 1)
			throw new Exception($"фантом потерялся: очередь={ZoneJobRouter.Instance.QueueDepth(zone.Id)}");
		if (assigned)
			throw new Exception("фантом был назначен агенту без валидации!");
		if (pool.CurrentJobId[agent] != -1)
			throw new Exception("агент склеймил фантом");

		// Реальная работа — успешное назначение.
		int realId = RegisterAndPush(128, 128, 1000);
		assigned = WorkerZoneJobSystem.Instance.TryAssignZoneJob(agent, pool, ctx);
		if (!assigned)
			throw new Exception("реальная работа не назначена после отказа фантома");
		if (pool.CurrentJobId[agent] != realId || pool.States[agent] == AgentState.Idle)
			throw new Exception("состояние агента после назначения некорректно");
		if (ZoneJobRouter.Instance.AssignedRecordCount != 1)
			throw new Exception("выданная работа не учтена в _assigned роутера");

		CleanupWorld();
	}

	// (д) работающий Worker: голод выше порога+5 → прерывание, работа в очереди
	// зоны, прогресс в клетке сохранён (P7б).
	private void TestNeedsInterrupt()
	{
		var (ctx, pool) = MakeWorld(4);
		var wm = WorkZoneManager.Instance;
		if (!wm.CreateZoneAt(128, 128, 1UL << 1, 64, 1, out var zone, out _))
			throw new Exception("зона не создалась");
		ZoneJobRouter.Instance.ForceRebuild(1000);

		int agent = 0;
		pool.Classes[agent] = AgentClass.Worker;
		pool.AssignedZoneId[agent] = zone.Id;
		// СВОЯ клетка: RegisterJob дедуплицирует по (x,y,type) — чужая позиция,
		// иначе вернёт уже заклеймленную работу прошлого теста.
		int id = RegisterAndPush(120, 130, 1000);
		if (!WorkerZoneJobSystem.Instance.TryAssignZoneJob(agent, pool, ctx))
			throw new Exception("работа не назначена");
		if (ZoneJobRouter.Instance.QueueDepth(zone.Id) != 0)
			throw new Exception("очередь зоны не опустела после выдачи");

		// Прогресс работы: половина.
		pool.WorkProgress[agent] = 5f;
		float expected = 0.5f;

		// Ниже порога+5 — НЕ прерывает.
		pool.Hunger[agent] = AgentNeedsConfig.HungerSeekThreshold + 2f;
		if (WorkerZoneJobSystem.Instance.TryInterruptForNeeds(agent, pool, ctx))
			throw new Exception("гистерезис не сработал: прервало ниже порога+5");

		// Выше порога+5 — прерывает.
		pool.Hunger[agent] = AgentNeedsConfig.HungerSeekThreshold + 10f;
		if (!WorkerZoneJobSystem.Instance.TryInterruptForNeeds(agent, pool, ctx))
			throw new Exception("голод выше порога+5 не прервал Worker'а");

		// Работа вернулась в ОЧЕРЕДЬ ЗОНЫ (не в непокрытые, не потерялась).
		if (ZoneJobRouter.Instance.QueueDepth(zone.Id) != 1)
			throw new Exception($"работа не вернулась в очередь зоны (depth={ZoneJobRouter.Instance.QueueDepth(zone.Id)})");
		if (ZoneJobRouter.Instance.UncoveredCount != 0)
			throw new Exception("возвращённая работа попала в непокрытые");
		if (ZoneJobRouter.Instance.AssignedRecordCount != 0)
			throw new Exception("запись _assigned не снята после возврата");

		// Прогресс в клетке сохранён хендлером (OnCancel → CellProgressStore).
		float frac = _handler.Progress.GetFraction(120, 130);
		if (Math.Abs(frac - expected) > 0.01f)
			throw new Exception($"прогресс потерян: {frac} (ожидалось ~{expected})");

		// Второй Worker зоны подбирает работу с тем же jobId.
		// (out при false — default(ZoneJob): JobId=0; проверяем отдельно.)
		bool reDeq = ZoneJobRouter.Instance.DequeueNearest(zone.Id, 128, 128, out var again);
		if (!reDeq || again.JobId != id)
			throw new Exception($"возвращённая работа не выдаётся следующему Worker'у (reDeq={reDeq}, got={again.JobId}, wanted={id})");

		CleanupWorld();
	}

	// (е) свежая (<60с) работа в очереди зоны не отдаётся Laborer'у (охрана в
	// TryClaimInChunk); после 60с — доступна.
	private void TestSixtySecondRule()
	{
		var (ctx, pool) = MakeWorld(4);
		var wm = WorkZoneManager.Instance;
		if (!wm.CreateZoneAt(128, 128, 1UL << 1, 64, 1, out var zone, out _))
			throw new Exception("зона не создалась");
		ZoneJobRouter.Instance.ForceRebuild(1000);

		// Своя клетка — прошлые тесты клеймили работу на (128,128), а dedup
		// RegisterJob по позиции вернул бы уже заклеймленный id.
		int id = RegisterAndPush(140, 140, 1000);
		ZoneJobRouter.TestClockOverrideMs = 1000; // «сейчас» = момент пуша

		if (!ZoneJobRouter.Instance.IsHeldForWorkerFresh(id))
			throw new Exception("свежая работа не держится для Worker'а");

		// Laborer (обычный агент из пула) пытается клеймить через пирамиду.
		int laborer = 3; // остаётся Laborer
		bool claimed = JobDispatcher.Instance.JobIndex.TryClaimJobForAgent(
			128, 128, ToolRequirement.None, pool, laborer, ctx, false, false, out var stolen);
		if (claimed)
			throw new Exception($"Laborer украл свежую работу зоны (<60с)! id={stolen.Id} " +
								$"queuedAt={ZoneJobRouter.Instance.GetQueuedAtMs(stolen.Id)} now={ZoneJobRouter.NowMs}");

		// Часы +61с: холд истёк — работа доступна разнорабочему.
		ZoneJobRouter.TestClockOverrideMs = 1000 + 61_000;
		if (ZoneJobRouter.Instance.IsHeldForWorkerFresh(id))
			throw new Exception("холд не истёк после 61с");
		claimed = JobDispatcher.Instance.JobIndex.TryClaimJobForAgent(
			128, 128, ToolRequirement.None, pool, laborer, ctx, false, false, out _);
		if (!claimed)
			throw new Exception("после 60с простоя Laborer не получил работу");

		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
		ZoneJobRouter.TestClockOverrideMs = -1;
		JobDispatcher.Instance.JobIndex.Clear();
	}

	// (ж) watchdog: ReleaseJobWorkerForStuck для Worker'а возвращает работу в
	// очередь зоны и НЕ ставит кулдаун недостижимости (следующий Worker берёт).
	private void TestWatchdogReturn()
	{
		var (ctx, pool) = MakeWorld(6);
		var wm = WorkZoneManager.Instance;
		if (!wm.CreateZoneAt(128, 128, 1UL << 1, 64, 1, out var zone, out _))
			throw new Exception("зона не создалась");
		ZoneJobRouter.Instance.ForceRebuild(1000);

		int agent = 0;
		pool.Classes[agent] = AgentClass.Worker;
		pool.AssignedZoneId[agent] = zone.Id;
		int id = RegisterAndPush(128, 128, 1000);
		if (!WorkerZoneJobSystem.Instance.TryAssignZoneJob(agent, pool, ctx))
			throw new Exception("работа не назначена");

		// Часы далеко вперёд, чтобы холд после возврата не мешал чистым проверкам.
		ZoneJobRouter.TestClockOverrideMs = 10_000_000;

		JobDispatcher.Instance.ReleaseJobWorkerForStuck(agent, pool, ctx, 15000);

		if (ZoneJobRouter.Instance.QueueDepth(zone.Id) != 1)
			throw new Exception($"watchdog не вернул работу в очередь зоны (depth={ZoneJobRouter.Instance.QueueDepth(zone.Id)})");
		if (pool.CurrentJobId[agent] != -1 || pool.States[agent] != AgentState.Idle)
			throw new Exception("агент не освобождён после stuck-релиза");
		if (pool.Classes[agent] != AgentClass.Worker)
			throw new Exception("watchdog сбросил класс агента");

		// Кулдаун НЕ поставлен: второй Worker зоны сразу берёт работу.
		int worker2 = 1;
		pool.Classes[worker2] = AgentClass.Worker;
		pool.AssignedZoneId[worker2] = zone.Id;
		if (!WorkerZoneJobSystem.Instance.TryAssignZoneJob(worker2, pool, ctx))
			throw new Exception("возвращённая работа не подобрана следующим Worker'ом (кулдаун?)");
		if (pool.CurrentJobId[worker2] != id)
			throw new Exception("второй Worker получил не ту работу");

		CleanupWorld();
		ZoneJobRouter.TestClockOverrideMs = -1;
	}
}
