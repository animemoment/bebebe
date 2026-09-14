using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Параллельный диспетчер задач на chunk-based batch assignment.
/// Без глобальных lock'ов — использует CAS на AssignedWorkers.
/// </summary>
public sealed class JobDispatcher
{
	public static JobDispatcher Instance { get; } = new();

	public GenericJobSpatialIndex JobIndex { get; } = new();
	public IdleWorkerSpatialGrid IdleWorkers { get; } = new();

	private const int ChunkDim = 32;
	private const int ChunkCount = ChunkDim * ChunkDim;
	private const int WorkersPerChunkBudget = 48;
	private const int ChunksPerBatch = 512;
	private const int MaxBatchScale = 8;
	// Cap назначений на чанк за один проход: без него весь бюджет рабочих чанка
	// (48) уходит в TryClaim по одной и той же переполненной чанк-очереди,
	// остальные чанки диапазона голодают до следующего вызова.
	private const int MaxAssignPerChunk = 8;

	// Буферы для per-chunk сбора (переиспользуемые)
	// Marker used to atomically reserve an idle worker (CurrentJobId = -2)
	// before a job claim in the parallel dispatcher.
	private const int AgentReservedMarker = -2;

	// Per-thread buffers for collecting idle workers. A shared buffer would
	// cause a data race inside Parallel.ForEach.
	private readonly ThreadLocal<int[]> _workerBuffer = new(() => new int[WorkersPerChunkBudget]);
	// Отдельный буфер для Global-прохода: CollectIdleWorkers возвращает 0,
	// если destination.Length < maxCount, поэтому общий буфер на 48 сюда нельзя.
	// P0-перф: 32 вместо 128 — O(W×J) в TryClaimFromCandidateList режется в 4 раза.
	private const int MaxGlobalWorkers = 32;
	private readonly ThreadLocal<int[]> _globalWorkerBuffer = new(() => new int[MaxGlobalWorkers]);
	// Переиспользуемый список кандидатов global-прохода (без new List каждый тик).
	// DispatchPendingJobs вызывается из одного sim-потока — гонки нет.
	private readonly List<int> _globalCandidates = new(512);
	// Троттлинг global-прохода по РЕАЛЬНОМУ времени: Fill O(N) + Sort O(N log N)
	// + O(W×J) claim-сканов однопоточно — при 200k задач каждый вызов кладёт
	// симуляцию. Не чаще раза в 2 реальные секунды, остальное покрывают chunk+spill.
	private long _lastGlobalPassTicks;
	private static readonly TimeSpan GlobalPassMinInterval = TimeSpan.FromSeconds(2);

	// Round-robin счётчик для равномерной обработки чанков
	private int _chunkScanIndex;

	/// <summary>
	/// Диспетчеризация задач. <paramref name="chunkBatchScale"/> увеличивает
	/// объём скана чанков за вызов на высоких скоростях симуляции (там частота
	/// вызовов урезана масштабированием интервала), чтобы назначение работы не
	/// задерживалось. Потолок — <see cref="MaxBatchScale"/>.
	/// </summary>
	public void DispatchPendingJobs(AgentDataPool pool, SimulationContext ctx, int chunkBatchScale = 1)
	{
		using (GameProfiler.Scope())
		{
			if (JobIndex.UnclaimedCount <= 0 || IdleWorkers.TotalIdleCount <= 0)
				return;

			int totalChunks = ChunkCount;
			int batchScale = Math.Clamp(chunkBatchScale, 1, MaxBatchScale);
			int chunksToProcess = Math.Min(ChunksPerBatch * batchScale, totalChunks);

			// Атомарно резервируем непрерывный диапазон чанков — без дублей/пропусков
			// между потоками (раньше был Interlocked.Increment на каждый offset).
			// Без Math.Abs: Math.Abs(int.MinValue) кидает OverflowException при
			// переполнении счётчика — вместо этого двойной mod в [0, totalChunks).
			int oldScan = Interlocked.Add(ref _chunkScanIndex, chunksToProcess) - chunksToProcess;
			int baseChunk = ((oldScan % totalChunks) + totalChunks) % totalChunks;

			int totalAssigned = 0;

			// Динамика вместо статического Partitioner: чанк с «тяжёлыми»
			// OnStart-резервами (TryReserve под lock) больше не сталлит соседей —
			// свободные потоки разбирают остаток очереди батчами.
			DynamicWorkBalancer.ForEachRange(chunksToProcess, (start, end) =>
			{
				int local = 0;
				for (int offset = start; offset < end; offset++)
				{
					int chunkIndex = (baseChunk + offset) % totalChunks;
					local += DispatchChunk(chunkIndex, pool, ctx);
				}
				if (local > 0)
					Interlocked.Add(ref totalAssigned, local);
			}, "Dispatcher.Balance");
			// totalAssigned больше не гейтит spill/global (см. ниже) — оставлен
			// для профайлинга/диагностики.

			// Spill-over: добираем соседними чанками, если chunk-проход оставил
			// idle-агентов. Гейт по totalAssigned был неверен: 10 назначений могли
			// закрыть гейт при сотнях оставшихся idle. Проверяем факты напрямую.
			// (Spill сам капнут spillBudget=16 — дешёвый, ложное срабатывание
			// стоит только одного CollectIdleWorkers.)
			if (IdleWorkers.TotalIdleCount > 0 && JobIndex.UnclaimedCount > 0)
			{
				SpillOverPass(pool, ctx);
			}

		// Global: fallback для дальних задач. Дорогой проход (Fill O(N) + Sort
		// O(N log N) + O(W×J) однопоточно с lock'ами в CanAgentExecute) — при
		// больших объёмах каждый вызов кладёт симуляцию: sim-цикл ждёт
		// DispatchPendingJobs, шаги не идут, 100x превращается в слайд-шоу при
		// низкой загрузке CPU. Поэтому троттлинг по реальному времени:
		// не чаще раза в 2с, остальное покрывают chunk+spill.
		if (IdleWorkers.TotalIdleCount > 0 && JobIndex.UnclaimedCount > 0)
		{
			long now = DateTime.UtcNow.Ticks;
			if (now - _lastGlobalPassTicks >= GlobalPassMinInterval.Ticks)
			{
				_lastGlobalPassTicks = now;
				GlobalRedistributePass(pool, ctx);
			}
		}
		}
	}

	/// <summary>
	/// Processes one chunk: collects idle workers and assigns them jobs.
	/// </summary>
	private int DispatchChunk(int chunkIndex, AgentDataPool pool, SimulationContext ctx)
	{
		int workerCount = IdleWorkers.CollectIdleWorkersInChunk(
			chunkIndex, WorkersPerChunkBudget, _workerBuffer.Value, pool);
		if (workerCount == 0) return 0;

		if (JobIndex.GetChunkJobCount(chunkIndex) == 0)
			return 0;

		int assigned = 0;

		for (int wi = 0; wi < workerCount; wi++)
		{
			// Cap на чанк: остальные рабочие остаются idle и их подберут
			// соседние чанки (spill) / global — вместо 48 claim-попыток в один чанк.
			if (assigned >= MaxAssignPerChunk)
				break;

			int agentIndex = _workerBuffer.Value[wi];
			if (agentIndex < 0 || agentIndex >= pool.Capacity)
				continue;
			if (pool.States[agentIndex] != AgentState.Idle)
				continue;

			// Atomically capture the idle worker: only one dispatch thread can
			// move CurrentJobId from -1 to the reserved marker.
			if (Interlocked.CompareExchange(ref pool.CurrentJobId[agentIndex], AgentReservedMarker, -1) != -1)
				continue;

			try
			{
				int workerTx = pool.CurrentCellX[agentIndex];
				int workerTy = pool.CurrentCellY[agentIndex];

				if (JobIndex.TryClaimForWorkerInChunk(
					chunkIndex, workerTx, workerTy,
					pool.EquippedTools[agentIndex],
					pool, agentIndex, ctx,
					out var activeJob))
				{
					IdleWorkers.RemoveIdleWorker(agentIndex, pool);
					pool.CurrentJobId[agentIndex] = activeJob.Id;
					pool.CurrentJobType[agentIndex] = activeJob.TypeId;

					if (JobRegistry.TryGetHandler(activeJob.TypeId, out var handler))
					{
						try
						{
							handler.OnStart(agentIndex, activeJob, pool, ctx);
							pool.LastJobCategory[agentIndex] = (int)JobPriorityManager.Instance.GetCategory(activeJob.TypeId);
							if (pool.States[agentIndex] == AgentState.Idle)
							{
								// OnStart тихо вернул агента в Idle (транзиентный фейл
								// резерва): claim уже удерживается, агент уже удалён
								// из idle-сетки. Без явного отката агент потерян:
								// State==Idle, но не в сетке, CurrentJobId != -1,
								// claim висит. Откатываем полностью.
								// (Часть хендлеров уже сами делают Release+AddIdle
								// в fail-ветке — тогда CurrentJobId==-1 и обе ветки
								// ниже no-op: ReleaseWorkerClaim(-1) early-out,
								// AddIdleWorker дедуплицируется по _inGrid.)
								int rollbackId = pool.CurrentJobId[agentIndex];
								if (rollbackId != -1)
								{
									JobIndex.ReleaseWorkerClaim(rollbackId);
									pool.CurrentJobId[agentIndex] = -1;
									pool.CurrentJobType[agentIndex] = JobTypeId.None;
								}
								IdleWorkers.AddIdleWorker(agentIndex, pool);
							}
							else
							{
								assigned++;
							}
						}
						catch (Exception ex)
						{
							GD.PrintErr($"[JobDispatcher] OnStart error {activeJob.TypeId} (agent #{agentIndex}): {ex.Message}\n{ex.StackTrace}");
							JobIndex.ReleaseWorkerClaim(activeJob.Id);
							pool.CurrentJobId[agentIndex] = -1;
							pool.CurrentJobType[agentIndex] = JobTypeId.None;
							pool.States[agentIndex] = AgentState.Idle;
							IdleWorkers.AddIdleWorker(agentIndex, pool);
						}
					}
					else
					{
						JobIndex.ReleaseWorkerClaim(activeJob.Id);
						pool.CurrentJobId[agentIndex] = -1;
						pool.CurrentJobType[agentIndex] = JobTypeId.None;
						IdleWorkers.AddIdleWorker(agentIndex, pool);
					}
				}
				else
				{
					// No job claimed - release the reserved worker back to the idle pool.
					pool.CurrentJobId[agentIndex] = -1;
				}
			}
			catch
			{
				// Never leave an agent stuck in the reserved state.
				pool.CurrentJobId[agentIndex] = -1;
				throw;
			}
		}

		return assigned;
	}

	/// <summary>
	/// Spill-over pass: для оставшихся без работы агентов ищем задачи в соседних чанках.
	/// </summary>
	private void SpillOverPass(AgentDataPool pool, SimulationContext ctx)
	{
		int remaining = IdleWorkers.CollectIdleWorkers(WorkersPerChunkBudget, _workerBuffer.Value, pool);
		if (remaining == 0) return;

		// Cap spill-прохода: раньше перебирал всех собранных idle (до 48) с кольцом
		// 3x3 чанка и TryClaim в каждом — до 48*9 claim-сканов за вызов.
		// Ограничиваем рабочими, остальные дождутся global/chunk следующего тика.
		int spillBudget = Math.Min(remaining, 16);

		for (int wi = 0; wi < spillBudget; wi++)
		{
			int agentIndex = _workerBuffer.Value[wi];
			if (agentIndex < 0 || agentIndex >= pool.Capacity || pool.States[agentIndex] != AgentState.Idle)
				continue;

			// Та же CAS-защита, что в DispatchChunk: spill/global идут после
			// параллельного прохода и могут увидеть stale idle-снимок.
			if (Interlocked.CompareExchange(ref pool.CurrentJobId[agentIndex], AgentReservedMarker, -1) != -1)
				continue;

			try
			{
			int workerTx = pool.CurrentCellX[agentIndex];
			int workerTy = pool.CurrentCellY[agentIndex];
			int centerChunk = GenericJobSpatialIndex.GetChunkIndexStatic(workerTx, workerTy);

			bool found = false;
			int centerCx = centerChunk % ChunkDim;
			int centerCy = centerChunk / ChunkDim;

			for (int r = 0; r <= 1 && !found; r++)
			{
				int minCx = Math.Max(0, centerCx - r);
				int maxCx = Math.Min(ChunkDim - 1, centerCx + r);
				int minCy = Math.Max(0, centerCy - r);
				int maxCy = Math.Min(ChunkDim - 1, centerCy + r);

				for (int cx = minCx; cx <= maxCx && !found; cx++)
				{
					for (int cy = minCy; cy <= maxCy && !found; cy++)
					{
						if (r > 0 && cx > minCx && cx < maxCx && cy > minCy && cy < maxCy)
							continue;

						int chunkIdx = cy * ChunkDim + cx;
						if (JobIndex.GetChunkJobCount(chunkIdx) == 0)
							continue;

						if (JobIndex.TryClaimForWorkerInChunk(
							chunkIdx, workerTx, workerTy,
							pool.EquippedTools[agentIndex],
							pool, agentIndex, ctx,
							out var spillJob))
						{
							IdleWorkers.RemoveIdleWorker(agentIndex, pool);
							pool.CurrentJobId[agentIndex] = spillJob.Id;
							pool.CurrentJobType[agentIndex] = spillJob.TypeId;

							if (JobRegistry.TryGetHandler(spillJob.TypeId, out var handler))
							{
								try
								{
									handler.OnStart(agentIndex, spillJob, pool, ctx);
									pool.LastJobCategory[agentIndex] = (int)JobPriorityManager.Instance.GetCategory(spillJob.TypeId);
									if (pool.States[agentIndex] == AgentState.Idle)
									{
										// Тот же откат что в DispatchChunk: OnStart вернул
										// в Idle — освобождаем claim, возвращаем в сетку.
										int rollbackId = pool.CurrentJobId[agentIndex];
										if (rollbackId != -1)
										{
											JobIndex.ReleaseWorkerClaim(rollbackId);
											pool.CurrentJobId[agentIndex] = -1;
											pool.CurrentJobType[agentIndex] = JobTypeId.None;
										}
										IdleWorkers.AddIdleWorker(agentIndex, pool);
									}
									found = true;
								}
								catch (Exception ex)
								{
									GD.PrintErr($"[JobDispatcher] Spill-over ошибка OnStart: {ex.Message}");
									JobIndex.ReleaseWorkerClaim(spillJob.Id);
									pool.CurrentJobId[agentIndex] = -1;
									pool.CurrentJobType[agentIndex] = JobTypeId.None;
									pool.States[agentIndex] = AgentState.Idle;
									IdleWorkers.AddIdleWorker(agentIndex, pool);
								}
							}
							else
							{
								JobIndex.ReleaseWorkerClaim(spillJob.Id);
								pool.CurrentJobId[agentIndex] = -1;
								pool.CurrentJobType[agentIndex] = JobTypeId.None;
								pool.States[agentIndex] = AgentState.Idle;
								IdleWorkers.AddIdleWorker(agentIndex, pool);
							}
							// Нет хендлера или OnStart упал — агент снова idle,
							// дальше по кольцу искать ему нечего.
							if (pool.States[agentIndex] == AgentState.Idle)
								found = true;
						}
					}
				}
			}
			if (!found)
			{
				// Работы рядом нет — снять резерв, вернуть в idle-сетку.
				pool.CurrentJobId[agentIndex] = -1;
			}
			}
			catch
			{
				pool.CurrentJobId[agentIndex] = -1;
				throw;
			}
		}
	}

	/// <summary>
	/// Глобальный fallback-распределитель работы.
	/// После локального/чанкового прохода собирает простаивающих работников по всей
	/// карте и назначает им ближайшие незахваченные задачи из глобального
	/// приоритизированного списка. Исправляет ситуацию, когда работа распределялась
	/// только в пределах нескольких чанков рядом с целью.
	/// </summary>
	private void GlobalRedistributePass(AgentDataPool pool, SimulationContext ctx)
	{
		var buf = _globalWorkerBuffer.Value;
		int remaining = IdleWorkers.CollectIdleWorkers(MaxGlobalWorkers, buf, pool);
		if (remaining == 0)
			return;

		// Переиспользуемый буфер вместо new List каждый тик (меньше GC-давления).
		var candidates = _globalCandidates;
		JobIndex.FillPrioritizedUnclaimed(candidates);
		if (candidates.Count == 0)
			return;

		for (int wi = 0; wi < remaining; wi++)
		{
			int agentIndex = buf[wi];
			if (agentIndex < 0 || agentIndex >= pool.Capacity || pool.States[agentIndex] != AgentState.Idle)
				continue;

			if (Interlocked.CompareExchange(ref pool.CurrentJobId[agentIndex], AgentReservedMarker, -1) != -1)
				continue;

			try
			{
			int workerTx = pool.CurrentCellX[agentIndex];
			int workerTy = pool.CurrentCellY[agentIndex];

			if (JobIndex.TryClaimFromCandidateList(
				candidates, workerTx, workerTy,
				pool.EquippedTools[agentIndex],
				pool, agentIndex, ctx,
				out var claimedJob))
			{
				IdleWorkers.RemoveIdleWorker(agentIndex, pool);
				pool.CurrentJobId[agentIndex] = claimedJob.Id;
				pool.CurrentJobType[agentIndex] = claimedJob.TypeId;

				if (JobRegistry.TryGetHandler(claimedJob.TypeId, out var handler))
				{
					try
					{
						handler.OnStart(agentIndex, claimedJob, pool, ctx);
						pool.LastJobCategory[agentIndex] = (int)JobPriorityManager.Instance.GetCategory(claimedJob.TypeId);
						if (pool.States[agentIndex] == AgentState.Idle)
						{
							// Тот же откат что в DispatchChunk: OnStart вернул
							// в Idle — освобождаем claim, возвращаем в сетку.
							int rollbackId = pool.CurrentJobId[agentIndex];
							if (rollbackId != -1)
							{
								JobIndex.ReleaseWorkerClaim(rollbackId);
								pool.CurrentJobId[agentIndex] = -1;
								pool.CurrentJobType[agentIndex] = JobTypeId.None;
							}
							IdleWorkers.AddIdleWorker(agentIndex, pool);
						}
					}
					catch (Exception ex)
					{
						GD.PrintErr($"[JobDispatcher] Global-распределитель ошибка OnStart: {ex.Message}");
						JobIndex.ReleaseWorkerClaim(claimedJob.Id);
						pool.CurrentJobId[agentIndex] = -1;
						pool.CurrentJobType[agentIndex] = JobTypeId.None;
						pool.States[agentIndex] = AgentState.Idle;
						IdleWorkers.AddIdleWorker(agentIndex, pool);
					}
				}
				else
				{
					JobIndex.ReleaseWorkerClaim(claimedJob.Id);
					pool.CurrentJobId[agentIndex] = -1;
					pool.CurrentJobType[agentIndex] = JobTypeId.None;
					pool.States[agentIndex] = AgentState.Idle;
					IdleWorkers.AddIdleWorker(agentIndex, pool);
				}
			}
			else
			{
				pool.CurrentJobId[agentIndex] = -1;
			}
			}
			catch
			{
				pool.CurrentJobId[agentIndex] = -1;
				throw;
			}
		}
	}

	public int RegisterJob(JobData job) => JobIndex.RegisterJob(job);
	public void RegisterBatch(List<JobData> jobs) => JobIndex.RegisterBatch(jobs);
	public void UnregisterJob(int jobId) => JobIndex.RemoveJob(jobId, out _);
	/// <summary>Unregister с результатом: true — работа реально удалена (счётчик
	/// _unclaimedCount уже поправлен внутри RemoveJob), false — её уже не было
	/// (stale(holder после stuck-релиза/переиспользования id): тогда вызывающий
	/// должен сам освободить claim через ReleaseWorkerClaim.</summary>
	public bool TryUnregisterJob(int jobId) => JobIndex.RemoveJob(jobId, out _);
	public void UnregisterJobByPos(int x, int y, JobTypeId type) => JobIndex.RemoveJobByPos(x, y, type, out _);
	public void UnregisterBatchByPositions(List<(int X, int Y)> positions, JobTypeId type) => JobIndex.RemoveBatchByPositions(positions, type);

	public void ReleaseJobWorker(int agentIndex, AgentDataPool pool, SimulationContext ctx)
	{
		using (GameProfiler.Scope())
		{
			int jobId = pool.CurrentJobId[agentIndex];
			// Агент зарезервирован диспетчером (CAS-маркер), но claim ещё не взят:
			// OnCancel/ReleaseWorkerClaim/MarkJobUnreachable для -2 недопустимы.
			// Просто снимаем резерв — агент остаётся в текущем состоянии,
			// диспетчер сам вернёт его в оборот (ветки отката) или он уже в сетке.
			if (jobId == AgentReservedMarker)
			{
				pool.CurrentJobId[agentIndex] = -1;
				pool.CurrentJobType[agentIndex] = JobTypeId.None;
				return;
			}
			if (jobId != -1)
			{
				if (JobRegistry.TryGetHandler(pool.CurrentJobType[agentIndex], out var handler))
				{
					try
					{
						handler.OnCancel(agentIndex, pool, ctx);
					}
					catch (Exception ex)
					{
						GD.PrintErr($"[JobDispatcher] Ошибка OnCancel для агента #{agentIndex}: {ex.Message}");
					}
				}

				JobIndex.ReleaseWorkerClaim(jobId);
				pool.CurrentJobId[agentIndex] = -1;
				pool.CurrentJobType[agentIndex] = JobTypeId.None;
			}

			// AddIdleWorker дедуплицируется по _inGrid (повторный вызов no-op),
			// поэтому явной проверки «уже idle» здесь не нужно.
			// ParallelRng вместо Random.Shared — ReleaseJobWorker вызывается из
			// Commit-путей, которые теперь тоже параллельны (балансер Phase3b).
			pool.States[agentIndex] = AgentState.Idle;
			pool.JobSearchTimer[agentIndex] = 4.0f + (float)ParallelRng.NextDouble() * 4.0f;
			IdleWorkers.AddIdleWorker(agentIndex, pool);
		}
	}

	/// <summary>
	/// Освобождение работника, который НЕ ДОБРАЛСЯ до работы (StuckTimer истёк).
	/// Дополнительно помечает работу на короткий кулдаун «недостижимости»,
	/// чтобы ближний бездельник не висел на ней вечно, а дальний (способный
	/// обойти стену) успел её забрать через глобальный проход.
	/// </summary>
	public void ReleaseJobWorkerForStuck(int agentIndex, AgentDataPool pool, SimulationContext ctx, int cooldownMs = 5000)
	{
		int jobId = pool.CurrentJobId[agentIndex];
		// -2 (резерв диспетчера) и -1 (нет работы) — кулдаун ставить не на что.
		if (jobId != -1 && jobId != AgentReservedMarker)
		{
			JobIndex.MarkJobUnreachable(jobId, cooldownMs);
		}
		ReleaseJobWorker(agentIndex, pool, ctx);
	}
}
