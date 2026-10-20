using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Game.Core;
using Game.Simulation;

namespace Game.Tests;

// Имя класса == basename файла (иначе Godot-генератор не эмитит ScriptPath
// и CSharpScript.new() из GDScript-раннера падает).

// ============================================================================
// §119 P1: тест битмапы покрытия + ZoneJobRouter (task-15). Запуск:
//   godot_run_headless --script tests/run_zone_router_test.gd
// Проверки приёмки:
//   (а) 10k пушей < 5 мс (зоны не созданы — путь непокрытых);
//   (б) пуш попадает в правильную зону (TryGetOwner/DequeueNearest);
//   (в) клип детерминирован (фикс-сид, повтор 100× один результат);
//   (г) стресс 16 потоков Push/DequeueNearest без гонок и потерянных работ;
//   (д) непокрытая работа репушится после 30с (Sweep);
//   (е) Enabled=false / DesiredWorkers=0 → зона не владеет клетками;
//   (ж) Unassign возвращает работу в очередь зоны.
// ============================================================================

public partial class test_zone_router : RefCounted
{
	private uint _rng = 777001u;

	private int NextRand()
	{
		_rng = _rng * 1664525u + 1013904223u;
		return (int)(_rng >> 8);
	}

	public bool Run()
	{
		try
		{
			ZoneManager.Instance.ClearAll();
			ZoneJobRouter.Instance.Reset();
			ZoneManager.SetMapBounds(1024, 1024);

			TestOwnershipAndRouting();
			GD.Print("[ZONE-ROUTER] (б) пуш → правильная зона, DequeueNearest: OK");

			TestClipDeterminism();
			GD.Print("[ZONE-ROUTER] (в) клип детерминирован (100 повторов): OK");

			TestEnabledAndDesiredGates();
			GD.Print("[ZONE-ROUTER] (е) Enabled/DesiredWorkers гейтят владение: OK");

			TestUnassign();
			GD.Print("[ZONE-ROUTER] (ж) Unassign возвращает работу в очередь: OK");

			TestUncoveredRepush();
			GD.Print("[ZONE-ROUTER] (д) непокрытая репушится через 30с: OK");

			TestPushPerf();
			GD.Print("[ZONE-ROUTER] (а) 10k пушей < 5 мс: OK");

			TestConcurrencyStress();
			GD.Print("[ZONE-ROUTER] (г) 16 потоков Push/DequeueNearest без потерь: OK");

			ZoneManager.Instance.ClearAll();
			ZoneJobRouter.Instance.Reset();
			GD.Print("==================================================");
			GD.Print("[TEST] ZONE ROUTER P1 PASSED! (Code 0)");
			GD.Print("==================================================");
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[TEST FAILED] {ex}");
			return false;
		}
	}

	private static long Now(long baseMs, long advance = 0) => baseMs + advance;

	// (б) зона TreeChopping на (400,400) r=128; Mining — (700,400) r=128.
	// Пуш дерева в (400,400) → очередь зоны 1; камня в (700,400) → зоны 2;
	// вне обеих → непокрытая.
	private void TestOwnershipAndRouting()
	{
		var wm = WorkZoneManager.Instance;
		if (!wm.CreateZoneAt(400, 400, 1UL << 1, 128, 3, out var zTree, out string f1))
			throw new Exception($"зона деревьев не создалась: {f1}");
		if (!wm.CreateZoneAt(700, 400, 1UL << 8, 128, 3, out var zMine, out string f2))
			throw new Exception($"зона камня не создалась: {f2}");

		var router = ZoneJobRouter.Instance;
		router.ForceRebuild(10_000);

		if (!router.Coverage.TryGetOwner(400, 400, JobTypeId.TreeChopping, out int ownTree) || ownTree != zTree.Id)
			throw new Exception($"владелец дерева: ожидалась зона {zTree.Id}, получено {ownTree}");
		if (router.Coverage.TryGetOwner(400, 400, JobTypeId.Mining, out _))
			throw new Exception("деревянная клетка ошибочно принадлежит Mining-зоне (типы независимы)");

		router.Push(JobTypeId.TreeChopping, 400, 400, 5001, 10_000);
		router.Push(JobTypeId.Mining, 700, 400, 5002, 10_000);
		router.Push(JobTypeId.TreeChopping, 900, 900, 5003, 10_000); // вне зон

		if (router.QueueDepth(zTree.Id) != 1 || router.QueueDepth(zMine.Id) != 1)
			throw new Exception($"очереди зон: дерево={router.QueueDepth(zTree.Id)}, камень={router.QueueDepth(zMine.Id)} (ожидалось 1/1)");
		if (router.UncoveredCount != 1)
			throw new Exception($"непокрытых: {router.UncoveredCount} (ожидалась 1)");

		if (!router.DequeueNearest(zTree.Id, 400, 400, out var jobTree) || jobTree.JobId != 5001)
			throw new Exception("работа 5001 не выдалась из зоны деревьев");
		if (!router.DequeueNearest(zMine.Id, 700, 400, out var jobMine) || jobMine.JobId != 5002)
			throw new Exception("работа 5002 не выдалась из зоны камня");
		if (router.DequeueNearest(zTree.Id, 400, 400, out _))
			throw new Exception("пустая очередь выдала работу (двойная выдача!)");

		// TryGetOwner O(1)-семантика на пустой карте: клетка ничья → false.
		if (router.Coverage.TryGetOwner(900, 900, JobTypeId.TreeChopping, out _))
			throw new Exception("клетка вне зон ошибочно покрыта");
		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
	}

	// (в) клип: две пересекающиеся зоны одного типа; AssignedCount задаёт
	// владельца спорной клетки; равенство → меньший id; 100 повторов — один результат.
	private void TestClipDeterminism()
	{
		var router = ZoneJobRouter.Instance;
		// Спорная клетка — центр (512,512): в обеих зонах (r=128, центры в 64 клетках).
		int firstOwner = -1;
		for (int iter = 0; iter < 100; iter++)
		{
			ZoneManager.Instance.ClearAll();
			ZoneJobRouter.Instance.Reset();
			// Фикс-сценарий без рандома: одни и те же зоны каждый повтор.
			if (!WorkZoneManager.Instance.CreateZoneAt(448, 512, 1UL << 1, 128, 5, out var a, out _))
				throw new Exception("клип: зона A не создалась");
			if (!WorkZoneManager.Instance.CreateZoneAt(576, 512, 1UL << 1, 128, 5, out var b, out _))
				throw new Exception("клип: зона B не создалась");

			// Равный AssignedCount (0) → владелец спора — МЕНЬШИЙ id.
			router.ForceRebuild(20_000 + iter);
			router.Coverage.TryGetOwner(512, 512, JobTypeId.TreeChopping, out int owner);
			int smaller = Math.Min(a.Id, b.Id);
			if (owner != smaller)
				throw new Exception($"клип tie: ожидался меньший id {smaller}, получено {owner}");

			if (iter == 0)
				firstOwner = owner;
			else if (owner != firstOwner)
				throw new Exception($"клип недетерминирован: итерация {iter} дала {owner} против {firstOwner}");
		}

		// Проверка приоритета AssignedCount: у зоны с БОЛЬШИМ счётчиком владение.
		// ВАЖНО: RegisterAssigned зовём на ЖИВОЙ Zone (менеджер), а не на фасадном
		// снапшоте WorkZone — покрытие читает живые зоны.
		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
		if (!WorkZoneManager.Instance.CreateZoneAt(448, 512, 1UL << 1, 128, 5, out var za, out _))
			throw new Exception("клип: зона A2 не создалась");
		if (!WorkZoneManager.Instance.CreateZoneAt(576, 512, 1UL << 1, 128, 5, out var zb, out _))
			throw new Exception("клип: зона B2 не создалась");
		int higherId = Math.Max(za.Id, zb.Id);
		int lowerId = Math.Min(za.Id, zb.Id);
		if (!ZoneManager.Instance.TryGetZoneById(higherId, out var liveHigher) || liveHigher == null)
			throw new Exception("клип: живая зона не найдена по id");
		for (int i = 0; i < 3; i++)
			liveHigher.RegisterAssigned();
		router.ForceRebuild(30_000);
		router.Coverage.TryGetOwner(512, 512, JobTypeId.TreeChopping, out int winner);
		if (winner != higherId)
			throw new Exception($"клип AssignedCount: ожидалась зона {higherId}, получено {winner}");
		if (winner == lowerId)
			throw new Exception("клип отдал спор меньшей зоне вопреки AssignedCount");
		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
	}

	// (е) Enabled=false и DesiredWorkers=0 снимают владение (после пересчёта).
	private void TestEnabledAndDesiredGates()
	{
		var wm = WorkZoneManager.Instance;
		var router = ZoneJobRouter.Instance;
		if (!wm.CreateZoneAt(500, 500, 1UL << 1, 64, 5, out var z, out string f))
			throw new Exception($"зона: {f}");
		router.ForceRebuild(40_000);
		if (!router.Coverage.TryGetOwner(500, 500, JobTypeId.TreeChopping, out int own) || own != z.Id)
			throw new Exception("включённая зона не владеет своим центром");

		// DesiredWorkers = 0 → владение снято.
		if (!wm.SetDesiredWorkers(z.Id, 0))
			throw new Exception("SetDesiredWorkers(0) не принялось");
		router.ForceRebuild(41_000);
		if (router.Coverage.TryGetOwner(500, 500, JobTypeId.TreeChopping, out _))
			throw new Exception("DesiredWorkers=0 — зона всё ещё владеет клеткой");

		// Вернуть заявку, выключить → владение снято.
		if (!wm.SetDesiredWorkers(z.Id, 7))
			throw new Exception("SetDesiredWorkers(7) не принялось");
		router.ForceRebuild(42_000);
		if (!router.Coverage.TryGetOwner(500, 500, JobTypeId.TreeChopping, out _))
			throw new Exception("зона с заявкой не владеет клеткой");
		if (!wm.SetEnabled(z.Id, false))
			throw new Exception("SetEnabled(false) не принялось");
		router.ForceRebuild(43_000);
		if (router.Coverage.TryGetOwner(500, 500, JobTypeId.TreeChopping, out _))
			throw new Exception("Enabled=false — зона всё ещё владеет клеткой");
		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
	}

	// (ж) Unassign: выдали → вернули → снова в очереди той же зоны.
	private void TestUnassign()
	{
		var wm = WorkZoneManager.Instance;
		var router = ZoneJobRouter.Instance;
		if (!wm.CreateZoneAt(400, 400, 1UL << 1, 64, 2, out var z, out string f))
			throw new Exception($"зона: {f}");
		router.ForceRebuild(50_000);
		router.Push(JobTypeId.TreeChopping, 400, 400, 7001, 50_000);
		if (!router.DequeueNearest(z.Id, 0, 0, out var job) || job.JobId != 7001)
			throw new Exception("работа не выдалась перед Unassign");
		if (router.QueueDepth(z.Id) != 0)
			throw new Exception("очередь не опустела после выдачи");
		if (!router.Unassign(7001, 51_000))
			throw new Exception("Unassign не вернул работу");
		if (router.QueueDepth(z.Id) != 1)
			throw new Exception("работа не вернулась в очередь зоны");
		if (!router.DequeueNearest(z.Id, 400, 400, out var again) || again.JobId != 7001)
			throw new Exception("повторная выдача после Unassign не сработала");
		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
	}

	// (д) непокрытая работа: <30с не трогается, после ≥30с и появления зоны —
	// попадает в очередь зоны через Sweep. Работа РЕАЛЬНО регистрируется в
	// реестре (как в проде: Push всегда после RegisterJob; Sweep считает
	// работы без реестра мёртвыми — реестр источник истины).
	private void TestUncoveredRepush()
	{
		var wm = WorkZoneManager.Instance;
		var router = ZoneJobRouter.Instance;
		long t0 = 100_000;

		int realJobId = JobDispatcher.Instance.RegisterJob(new JobData
		{
			TypeId = JobTypeId.TreeChopping,
			ExecutionType = JobExecutionType.Stationary,
			PriorityTier = JobPriorityTier.TreeChopping,
			TargetX = 250,
			TargetY = 250,
			StandX = 250,
			StandY = 250,
			MaxWorkers = 1
		});
		router.Push(JobTypeId.TreeChopping, 250, 250, realJobId, t0); // зон нет → непокрытая
		if (router.UncoveredCount != 1)
			throw new Exception("работа не попала в непокрытые");
		router.Sweep(t0 + 29_999);
		if (router.UncoveredCount != 1)
			throw new Exception("репуш сработал раньше 30с (спека: каждые 30с)");

		// Зона появляется ПОСЛЕ работы (мгновенного прохода НЕТ — только Sweep).
		if (!wm.CreateZoneAt(250, 250, 1UL << 1, 64, 1, out var z, out string f))
			throw new Exception($"зона: {f}");
		router.Sweep(t0 + 30_000);
		if (router.QueueDepth(z.Id) != 1)
			throw new Exception($"репуш после 30с не переложил работу (глубина {router.QueueDepth(z.Id)})");
		if (router.UncoveredCount != 0)
			throw new Exception("работа осталась в непокрытых после репуша");

		JobDispatcher.Instance.UnregisterJobByPos(250, 250, JobTypeId.TreeChopping);
		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
	}

	// (а) 10k пушей быстрее 5 мс. Замер УСТОЯВШЕГОСЯ режима: прогрев 2000 пушей
	// (JIT + выделение бакетов), затем два прохода по 10k (идентификаторы не
	// пересекаются — оба прохода меряют чистое добавление). Спековский порог —
	// на каждый проход отдельно; покрытый путь замеряется отдельной зоной.
	private void TestPushPerf()
	{
		var router = ZoneJobRouter.Instance;
		long t0 = 200_000;
		// Прогрев: JIT дженериков + кодовые пути (JIT переживает Reset).
		for (int i = 0; i < 2000; i++)
			router.Push(JobTypeId.TreeChopping, NextRand() & 1023, NextRand() & 1023, 90_000 + i, t0);
		router.Reset();

		double pass1 = MeasureUncoveredPushes(300_000);
		router.Reset();
		double pass2 = MeasureUncoveredPushes(400_000);
		router.Reset();
		GD.Print($"[ZONE-ROUTER] 10k пушей (без зон): проход1={pass1:F2} мс, проход2={pass2:F2} мс");
		if (pass1 > 5.0 || pass2 > 5.0)
			throw new Exception($"10k пушей {pass1:F2}/{pass2:F2} мс > 5 мс (спека)");

		// Покрытый путь: одна зона покрывает всю карту (512² = вся 1024²).
		if (!WorkZoneManager.Instance.CreateZoneAt(512, 512, 1UL << 1, 512, 5, out var z, out string f))
			throw new Exception($"зона перфа: {f}");
		router.ForceRebuild(500_000);
		// Прогрев ПОКРЫТОЙ ветки: tier-1 JIT + аллокация/рост словаря зоны —
		// замеряем устоявшийся режим, как и для непокрытой ветки.
		for (int i = 0; i < 5000; i++)
			router.Push(JobTypeId.TreeChopping, NextRand() & 1023, NextRand() & 1023, 550_000 + i, 500_000);
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
		GC.WaitForPendingFinalizers();
		var sw = System.Diagnostics.Stopwatch.StartNew();
		for (int i = 0; i < 10_000; i++)
			router.Push(JobTypeId.TreeChopping, NextRand() & 1023, NextRand() & 1023, 600_000 + i, 500_000);
		sw.Stop();
		GD.Print($"[ZONE-ROUTER] 10k пушей (в зону): {sw.Elapsed.TotalMilliseconds:F2} мс");
		if (sw.Elapsed.TotalMilliseconds > 5.0)
			throw new Exception($"10k пушей в зону = {sw.Elapsed.TotalMilliseconds:F2} мс > 5 мс (спека)");
		// r=512 покрывает вписанный круг: πr²/1024² ≈ 78.4% квадрата — углы
		// уходят в непокрытые. Проверяем СОХРАННОСТЬ: очередь + непокрытые = все.
		int inQueue = router.QueueDepth(z.Id);
		int uncovered = router.UncoveredCount;
		GD.Print($"[ZONE-ROUTER] перф: в очереди {inQueue}, непокрытых {uncovered}");
		if (inQueue + uncovered != 15_000)
			throw new Exception($"перф: потеряны работы ({inQueue}+{uncovered} != 15000)");
		if (inQueue < 10_000)
			throw new Exception($"перф: слишком много непокрытых ({uncovered}) — зона r=512 должна крывать ~78%");
		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
	}

	private double MeasureUncoveredPushes(long baseMs)
	{
		// Гигиена замера: мусор предыдущих тестов (2МБ карты покрытий и т.п.)
		// не должен попадать в проход в виде GC-пауз.
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
		GC.WaitForPendingFinalizers();
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
		var sw = System.Diagnostics.Stopwatch.StartNew();
		for (int i = 0; i < 10_000; i++)
			ZoneJobRouter.Instance.Push(JobTypeId.TreeChopping, NextRand() & 1023, NextRand() & 1023, 100_000 + i, baseMs);
		sw.Stop();
		return sw.Elapsed.TotalMilliseconds;
	}

	// (г) 16 потоков: пушат в 2 зоны + dequeue; каждая работа выдаётся максимум
	// один раз (сумма выданных+в_очереди+непокрытых == запушено).
	private void TestConcurrencyStress()
	{
		var wm = WorkZoneManager.Instance;
		var router = ZoneJobRouter.Instance;
		if (!wm.CreateZoneAt(300, 300, 1UL << 1, 128, 50, out var zA, out _))
			throw new Exception("стресс: зона A не создалась");
		if (!wm.CreateZoneAt(700, 700, 1UL << 1, 128, 50, out var zB, out _))
			throw new Exception("стресс: зона B не создалась");
		router.ForceRebuild(400_000);

		const int perThread = 500;
		const int threads = 16;
		long t0 = 400_000;
		var start = new ManualResetEventSlim(false);
		var tasks = new List<Task>();
		var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
		var dequeued = new System.Collections.Concurrent.ConcurrentBag<int>();

		for (int t = 0; t < threads; t++)
		{
			int tid = t;
			tasks.Add(Task.Run(() =>
			{
				try
				{
					start.Wait();
					for (int i = 0; i < perThread; i++)
					{
						int jobId = 200_000 + tid * perThread + i;
						int x = (tid % 2 == 0 ? 300 : 700) + (i % 64);
						int y = (tid % 2 == 0 ? 300 : 700) + (i / 64 % 64);
						router.Push(JobTypeId.TreeChopping, x, y, jobId, t0);
						if ((i & 15) == 0)
						{
							int zoneId = tid % 2 == 0 ? zA.Id : zB.Id;
							if (router.DequeueNearest(zoneId, 0, 0, out var j))
								dequeued.Add(j.JobId);
						}
					}
				}
				catch (Exception ex)
				{
					errors.Add($"t{tid}: {ex.Message}");
				}
			}));
		}
		start.Set();
		Task.WaitAll(tasks.ToArray());
		if (!errors.IsEmpty)
			throw new Exception($"гонки в стрессе: {string.Join("; ", errors)}");

		// Каждая работа максимум один раз выдана:
		var set = new HashSet<int>(dequeued);
		if (set.Count != dequeued.Count)
			throw new Exception($"двойная выдача: {dequeued.Count - set.Count} работ выдано дважды");

		// Все запушенные учтены: очереди + непокрытые + выданные = 8000.
		int accounted = router.QueueDepth(zA.Id) + router.QueueDepth(zB.Id) + router.UncoveredCount + dequeued.Count;
		if (accounted != threads * perThread)
			throw new Exception($"потеряно работ: {threads * perThread - accounted} (учтено {accounted})");
		ZoneManager.Instance.ClearAll();
		ZoneJobRouter.Instance.Reset();
	}
}
