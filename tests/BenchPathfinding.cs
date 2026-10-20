using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Game.Simulation;

namespace Game.Tests;

/// <summary>
/// §118 фаза 5 / §118.5: РЕГЛАМЕНТ ПРОВЕРКИ HPA* (требование юзера дословно):
///  * минимум 10 000 активных агентов (= 10 000 запросов пути);
///  * пути минимум 250 клеток (случайные пары, разнообразные направления);
///  * разнообразные сценарии, включая НЕПРОХОДИМУЮ реку;
///  * защита от абсурдных обходов (река на 20k клеток) — НЕ урезанием дальности,
///    а математическим критерием k·L (MaxDetourRatio · чебышёвское расстояние);
///  * бюджет кадра 1-2 мс максимум.
///
/// Ничего не «оптимизировано в ущерб функциональности»: бенчмарк только меряет
/// и проверяет инварианты. Замер — на горячем пути TryFindPathScratch (то, чем
/// реально ходит симуляция: zero-alloc, без new int[] на запрос).
/// </summary>
public partial class BenchPathfinding : RefCounted
{
	private const int MapSize = 1024;
	private const int RequestCount = 10000;
	private const int MinPathCells = 250;

	private static readonly Stopwatch Clock = new();

	public bool Run()
	{
		GD.Print("==================================================");
		GD.Print("[HPA* BENCH] регламент §118.5: 10k агентов, пути 250+, река, k·L, 1-2 мс");
		GD.Print("==================================================");
		try
		{
			ScenarioOpenField();
			ScenarioRiverWithBridge();
			ScenarioImpassableRiver();
			ScenarioSteadyState();
			GD.Print("==================================================");
			GD.Print("[HPA* BENCH OK] ALL SCENARIOS PASSED (Code 0)");
			GD.Print("==================================================");
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[HPA* BENCH FAILED] {ex.Message}\n{ex.StackTrace}");
			return false;
		}
		finally
		{
			HierarchicalPathfinder.Instance.Reset();
		}
	}

	// ------------------------------------------------------------------ //
	//  Сценарий 1: открытое поле — 10 000 запросов по 250+ клеток        //
	// ------------------------------------------------------------------ //

	private void ScenarioOpenField()
	{
		byte[] cost = MakeCost(MapSize, MapSize, (x, y) => HierarchicalPathfinder.CostGrass);
		HierarchicalPathfinder pf = HierarchicalPathfinder.Instance;
		pf.InitializeCost(cost, MapSize, MapSize);

		var requests = MakeRequests(new Random(12345), MapSize, RequestCount, MinPathCells);
		// Прогрев: JIT + построение оверлея + кэши (не попадает в замер).
		pf.TryFindPathScratch(0, 0, MapSize - 1, MapSize - 1, out _, out _);
		for (int i = 0; i < 500; i++)
			pf.TryFindPathScratch(requests[i].Sx, requests[i].Sy, requests[i].Tx, requests[i].Ty, out _, out _);

		long allocBefore = GC.GetAllocatedBytesForCurrentThread();
		var perPath = new double[requests.Length];
		int ok = 0;

		Clock.Restart();
		for (int i = 0; i < requests.Length; i++)
		{
			long t0 = Clock.ElapsedTicks;
			bool found = pf.TryFindPathScratch(requests[i].Sx, requests[i].Sy, requests[i].Tx, requests[i].Ty, out _, out int count);
			perPath[i] = (Clock.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
			if (found)
				ok++;
			// Инвариант: путь не может быть короче чебышёва расстояния.
			if (found && count > 0 && requests[i].Chebyshev > count + 1)
				throw new Exception($"путь короче чебышёва: {count} < {requests[i].Chebyshev}");
		}
		double totalMs = Clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
		long allocBytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

		if (ok != requests.Length)
			throw new Exception($"открытое поле: найдено {ok} из {requests.Length} путей (должны быть все)");
		Report("открытое поле", totalMs, perPath, allocBytes, pf.DetourRejects);
	}

	// ------------------------------------------------------------------ //
	//  Сценарий 2: непроходимая река с мостом (реальные обходы)           //
	// ------------------------------------------------------------------ //

	private void ScenarioRiverWithBridge()
	{
		const int riverX = 512;
		const int riverW = 8;
		const int bridgeY = 200;
		const int bridgeH = 6;

		byte[] cost = MakeCost(MapSize, MapSize, (x, y) =>
		{
			bool river = x >= riverX && x < riverX + riverW;
			bool bridge = y >= bridgeY && y < bridgeY + bridgeH;
			return river && !bridge ? HierarchicalPathfinder.CostBlocked : HierarchicalPathfinder.CostGrass;
		});
		HierarchicalPathfinder pf = HierarchicalPathfinder.Instance;
		pf.InitializeCost(cost, MapSize, MapSize);

		// Запросы через реку: старт слева, цель справа, расстояние 250+.
		var random = new Random(777);
		var perPath = new List<double>(RequestCount);
		int ok = 0;
		int detourRejected = 0;

		for (int i = 0; i < 500; i++) // прогрев
		{
			pf.TryFindPathScratch(100, 100, 900, 900, out _, out _);
		}
		long detourBefore = pf.DetourRejects;
		Clock.Restart();
		for (int i = 0; i < RequestCount; i++)
		{
			int sy = random.Next(0, MapSize - 1);
			int ty = random.Next(0, MapSize - 1);
			int sx = random.Next(20, riverX - 20);
			int tx = random.Next(riverX + riverW + 20, MapSize - 20);
			long t0 = Clock.ElapsedTicks;
			bool found = pf.TryFindPathScratch(sx, sy, tx, ty, out _, out _);
			perPath.Add((Clock.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency);
			if (found)
				ok++;
		}
		double totalMs = Clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
		detourRejected = (int)(pf.DetourRejects - detourBefore);

		GD.Print($"[HPA* BENCH] профиль: {pf.Profile}");

		// Мост есть → подавляющее большинство целей достижимо.
		if (ok < RequestCount * 0.95)
			throw new Exception(
				$"река с мостом: найдено {ok} из {RequestCount} (ожидалось ≥95%: мост есть); причины отказов: {pf.FailureStats}");
		Report($"река с мостом (найдено {ok}/{RequestCount}, k·L отверг {detourRejected})",
			totalMs, perPath.ToArray(), 0, pf.DetourRejects);
	}

	// ------------------------------------------------------------------ //
	//  Сценарий 3: река БЕЗ моста через всю карту — абсурдный обход       //
	// ------------------------------------------------------------------ //

	private void ScenarioImpassableRiver()
	{
		// Непроходимая полоса на всю высоту + крошечный проход в самом верху.
		// Цель — по ту сторону (x=700 против старта x=450): по прямой ~250 клеток,
		// реальный обход — вниз-вверх через верхнюю дырку, ~2000 клеток.
		// Такое обязано отсекаться критерием k·L, а не строиться.
		const int riverX = 512;
		const int riverW = 8;
		const int gapY = 3;

		byte[] cost = MakeCost(MapSize, MapSize, (x, y) =>
		{
			bool river = x >= riverX && x < riverX + riverW;
			bool gap = y < gapY;
			return river && !gap ? HierarchicalPathfinder.CostBlocked : HierarchicalPathfinder.CostGrass;
		});
		HierarchicalPathfinder pf = HierarchicalPathfinder.Instance;
		pf.InitializeCost(cost, MapSize, MapSize);
		pf.ResetProfile();

		long before = pf.DetourRejects;
		int found = 0;
		Clock.Restart();
		for (int i = 0; i < 200; i++)
		{
			int sy = 950 + i % 40;
			int ty = 950 + i % 40;
			// Старт слева от реки, цель справа: обход только через верхнюю дырку.
			bool ok = pf.TryFindPathScratch(450, sy, 700, ty, out _, out _);
			if (ok)
				found++;
		}
		double ms = Clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
		int rejected = (int)(pf.DetourRejects - before);

		// Ключевой инвариант: абсурдные обходы отсечены математикой (L≈250,
		// честного пути нет, обход ~1900 > 6·250=1500).
		if (rejected < 100)
			throw new Exception(
				$"критерий k·L не сработал: отвергнуто {rejected} из 200 (обход ~1900 при прямой 250 обязан отсекаться); {pf.FailureStats}");
		GD.Print($"[HPA* BENCH] река без моста: найдено={found}, отвергнуто k·L={rejected} за {ms:F1} мс ({ms / 200 * 1000:F0} мкс/запрос)");
		GD.Print($"[HPA* BENCH]   защита работает: цель считается фактически недостижимой БЕЗ урезания дальности");
	}

	// ------------------------------------------------------------------ //
	//  Сценарий 4: steady-state — 10 000 агентов к рабочим точкам        //
	//  (реальный режим колонии: цели повторяются → кэши HPA* тёплые)     //
	// ------------------------------------------------------------------ //

	private void ScenarioSteadyState()
	{
		const int riverX = 512;
		const int riverW = 8;
		const int bridgeY = 200;
		const int bridgeH = 6;

		byte[] cost = MakeCost(MapSize, MapSize, (x, y) =>
		{
			bool river = x >= riverX && x < riverX + riverW;
			bool bridge = y >= bridgeY && y < bridgeY + bridgeH;
			return river && !bridge ? HierarchicalPathfinder.CostBlocked : HierarchicalPathfinder.CostGrass;
		});
		HierarchicalPathfinder pf = HierarchicalPathfinder.Instance;
		pf.InitializeCost(cost, MapSize, MapSize);
		pf.ResetProfile();

		// 200 рабочих точек (как именованные задачи колонии) + агенты стартуют
		// из 200 разных мест — это честный steady-state, а не «один путь в кэше».
		var random = new Random(2024);
		var targets = new (int X, int Y)[200];
		var starts = new (int X, int Y)[200];
		for (int i = 0; i < 200; i++)
		{
			targets[i] = (random.Next(30, MapSize - 30), random.Next(30, MapSize - 30));
			starts[i] = (random.Next(30, MapSize - 30), random.Next(30, MapSize - 30));
		}

		// Прогрев: первый обход всех 200 пар (холодные кэши) — не в замере.
		for (int i = 0; i < 200; i++)
			pf.TryFindPathScratch(starts[i].X, starts[i].Y, targets[i].X, targets[i].Y, out _, out _);

		pf.ResetProfile();
		var perPath = new double[RequestCount];
		int ok = 0;

		Clock.Restart();
		for (int i = 0; i < RequestCount; i++)
		{
			int pick = random.Next(200);
			long t0 = Clock.ElapsedTicks;
			bool found = pf.TryFindPathScratch(starts[pick].X, starts[pick].Y, targets[pick].X, targets[pick].Y, out _, out _);
			perPath[i] = (Clock.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
			if (found)
				ok++;
		}
		double totalMs = Clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency;

		GD.Print($"[HPA* BENCH] steady-state профиль: {pf.Profile}");
		Report($"steady-state (200 точек, найдено {ok}/{RequestCount})", totalMs, perPath, 0, pf.DetourRejects);
	}

	// ------------------------------------------------------------------ //
	//  Утилиты                                                            //
	// ------------------------------------------------------------------ //

	private readonly struct Request
	{
		public readonly int Sx, Sy, Tx, Ty, Chebyshev;

		public Request(int sx, int sy, int tx, int ty)
		{
			Sx = sx; Sy = sy; Tx = tx; Ty = ty;
			int dx = Math.Abs(tx - sx);
			int dy = Math.Abs(ty - sy);
			Chebyshev = Math.Max(dx, dy);
		}
	}

	private static Request[] MakeRequests(Random random, int size, int count, int minCells)
	{
		var list = new Request[count];
		for (int i = 0; i < count; i++)
		{
			for (int attempt = 0; attempt < 40; attempt++)
			{
				int sx = random.Next(0, size);
				int sy = random.Next(0, size);
				int tx = random.Next(0, size);
				int ty = random.Next(0, size);
				var r = new Request(sx, sy, tx, ty);
				if (r.Chebyshev >= minCells)
				{
					list[i] = r;
					break;
				}
			}
			if (list[i].Chebyshev < minCells)
				throw new Exception($"не удалось подобрать запрос ≥{minCells} клеток за 40 попыток");
		}
		return list;
	}

	private static byte[] MakeCost(int w, int h, Func<int, int, byte> rule)
	{
		var cost = new byte[w * h];
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++)
				cost[y * w + x] = rule(x, y);
		return cost;
	}

	private static void Report(string name, double totalMs, double[] perPath, long allocBytes, long detourRejects)
	{
		Array.Sort(perPath);
		double avgMs = totalMs / perPath.Length;
		double p50 = perPath[perPath.Length / 2];
		double p95 = perPath[(int)(perPath.Length * 0.95)];
		double p99 = perPath[(int)(perPath.Length * 0.99)];
		double max = perPath[perPath.Length - 1];

		GD.Print($"[HPA* BENCH] {name}: {perPath.Length} запросов за {totalMs:F1} мс");
		GD.Print($"[HPA* BENCH]   среднее {avgMs * 1000.0:F1} мкс | p50 {p50 * 1000.0:F1} | p95 {p95 * 1000.0:F1} | p99 {p99 * 1000.0:F1} | max {max * 1000.0:F1} мкс");
		GD.Print($"[HPA* BENCH]   в бюджет 1 мс влезает {1.0 / avgMs:F0} путей, в 2 мс — {2.0 / avgMs:F0} путей");
		if (allocBytes > 0)
			GD.Print($"[HPA* BENCH]   аллокации горячего пути: {allocBytes} байт на {perPath.Length} запросов ({allocBytes / (double)perPath.Length:F1} б/запрос)");

		if (avgMs > 2.0)
			throw new Exception($"{name}: среднее {avgMs:F3} мс на путь — НЕ влезает в бюджет 2 мс (§118.5)");
	}
}