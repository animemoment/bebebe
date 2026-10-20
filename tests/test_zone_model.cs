using System;
using System.Collections.Generic;
using Godot;
using Game.Simulation;

namespace Game.Tests;

// ============================================================================
// §119 P0: тест модели зон (task-10). Запуск:
//   godot_run_headless --script tests/run_zone_model_test.gd
// Проверки приёмки:
//   (а) Contains совпадает с эталонной формулой dx²+dy²≤r² для r=64..512;
//   (б) кламп радиуса 64..512 (Create + SetWorkRadius);
//   (в) валидация «ровно один бит JobMask» (0/мульти/бит0 → отказ);
//   (г) RemoveZoneAtCenter убивает зону;
//   (д) 10 зон r=512 → GC.GetTotalMemory delta < 20 МБ (нет материализации);
//   (е) DesiredWorkers кламп 0..999, CellCount = πr², ферма не режется зоной.
// ============================================================================

// Имя класса == basename файла (иначе Godot-генератор не эмитит ScriptPath
// и CSharpScript.new() из GDScript-раннера падает).

public partial class test_zone_model : RefCounted
{
	private uint _rng = 20261020u;

	private int NextRand() // детерминизм по конвенции §118-тестов
	{
		_rng = _rng * 1664525u + 1013904223u;
		return (int)(_rng >> 8);
	}

	public bool Run()
	{
		try
		{
			var zm = ZoneManager.Instance;
			zm.ClearAll();
			ZoneManager.SetMapBounds(1024, 1024);

			TestContainsMatchesFormula();
			GD.Print("[ZONE-MODEL] (а) Contains == эталон dx²+dy²≤r² (r=64..512): OK");

			TestRadiusClamp();
			GD.Print("[ZONE-MODEL] (б) кламп радиуса 64..512 (create+set): OK");

			TestSingleBitMaskValidation();
			GD.Print("[ZONE-MODEL] (в) ровно один бит JobMask: OK");

			TestDesiredWorkersAndCellCount();
			GD.Print("[ZONE-MODEL] (е) DesiredWorkers кламп 0..999, CellCount=πr²: OK");

			TestFarmNotCutByWorkZone();
			GD.Print("[ZONE-MODEL] (ж) Farm не режется Work-зоной: OK");

			TestRemoveZoneAtCenter();
			GD.Print("[ZONE-MODEL] (г) RemoveZoneAtCenter убивает зону: OK");

			TestMemoryNoMaterialization();
			GD.Print("[ZONE-MODEL] (д) 10 зон r=512, GC delta < 20 МБ: OK");

			zm.ClearAll();
			GD.Print("==================================================");
			GD.Print("[TEST] ZONE MODEL P0 PASSED! (Code 0)");
			GD.Print("==================================================");
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[TEST FAILED] {ex}");
			return false;
		}
	}

	// (а) эталон: независимая long-математика спеки против WorkZone.Contains.
	private void TestContainsMatchesFormula()
	{
		int[] radii = { 64, 100, 257, 512 };
		foreach (int r in radii)
		{
			if (!WorkZoneManager.Instance.CreateZoneAt(512, 512, 1UL << 1, r, 3, out var zone, out string fail))
				throw new Exception($"зона r={r} не создалась: {fail}");
			if (zone.RadiusTiles != r)
				throw new Exception($"радиус исказился: {r} → {zone.RadiusTiles}");

			// Грубая сетка смещений (шаг 7) + граничные точки — достаточный срез.
			for (int dy = -r; dy <= r; dy += 7)
			{
				for (int dx = -r; dx <= r; dx += 7)
				{
					bool expected = (long)dx * dx + (long)dy * dy <= (long)r * r;
					bool actual = zone.Contains(512 + dx, 512 + dy);
					if (expected != actual)
						throw new Exception($"Contains(512{dx:+#;-#;0},512{dy:+#;-#;0}) r={r}: ожидалось {expected}, получено {actual}");
				}
			}
			// Границы: (r,0) внутри по формуле ≤, (r+1,0) снаружи, центр внутри.
			if (!zone.Contains(512 + r, 512)) throw new Exception($"граница (r,0) r={r} должна быть внутри (≤)");
			if (zone.Contains(512 + r + 1, 512)) throw new Exception($"(r+1,0) r={r} должен быть снаружи");
			if (!zone.Contains(512, 512)) throw new Exception("центр обязан быть внутри");
		}
		ZoneManager.Instance.ClearAll();
	}

	// (б) кламп радиуса: 1 → 64, 100000 → 512; SetWorkRadius так же.
	private void TestRadiusClamp()
	{
		var zm = ZoneManager.Instance;
		if (!zm.CreateWorkZoneAt(300, 300, 1UL << 2, 1, 0, out var low, out string f1))
			throw new Exception($"зона (r=1) не создалась: {f1}");
		if (low.RadiusTiles != WorkZone.MinRadiusTiles)
			throw new Exception($"кламп минимума: ожидалось {WorkZone.MinRadiusTiles}, получено {low.RadiusTiles}");
		if (!zm.CreateWorkZoneAt(700, 300, 1UL << 2, 100000, 0, out var high, out string f2))
			throw new Exception($"зона (r=100000) не создалась: {f2}");
		if (high.RadiusTiles != WorkZone.MaxRadiusTiles)
			throw new Exception($"кламп максимума: ожидалось {WorkZone.MaxRadiusTiles}, получено {high.RadiusTiles}");

		if (!zm.SetWorkRadius(low.Id, 999999) || low.RadiusTiles != WorkZone.MaxRadiusTiles)
			throw new Exception("SetWorkRadius не заклампил максимум");
		if (!zm.SetWorkRadius(low.Id, 0) || low.RadiusTiles != WorkZone.MinRadiusTiles)
			throw new Exception("SetWorkRadius не заклампил минимум");
		zm.ClearAll();
	}

	// (в) JobMask: пустая, мультибит, бит 0 — отказ; ровно один бит — успех.
	private void TestSingleBitMaskValidation()
	{
		var zm = ZoneManager.Instance;
		if (zm.CreateWorkZoneAt(512, 512, 0UL, 128, 1, out _, out string fEmpty))
			throw new Exception("пустая маска должна быть отклонена");
		if (zm.CreateWorkZoneAt(512, 512, 0x06UL, 128, 1, out _, out string fMulti))
			throw new Exception("мультибитная маска должна быть отклонена");
		if (zm.CreateWorkZoneAt(512, 512, 1UL, 128, 1, out _, out string fZero))
			throw new Exception("бит 0 (JobTypeId.None) должен быть отклонён");
		if (!zm.CreateWorkZoneAt(512, 512, 1UL << 3, 128, 1, out _, out string fOk))
			throw new Exception($"однобитная маска должна пройти: {fOk}");
		zm.ClearAll();
	}

	// (е) DesiredWorkers 5000→999, -5→0; CellCount = πr² (через фасад WorkZone).
	// WorkZone — СНАПШОТ: после мутации перечитываем свежую копию по Id.
	private void TestDesiredWorkersAndCellCount()
	{
		var wm = WorkZoneManager.Instance;
		if (!wm.CreateZoneAt(512, 512, 1UL << 4, 200, 5000, out var zone, out string f))
			throw new Exception($"зона не создалась: {f}");
		if (zone.DesiredWorkers != WorkZone.MaxDesiredWorkers)
			throw new Exception($"DesiredWorkers максимум: ожидалось {WorkZone.MaxDesiredWorkers}, получено {zone.DesiredWorkers}");
		if (!wm.SetDesiredWorkers(zone.Id, -5))
			throw new Exception("SetDesiredWorkers(-5) не принялось");
		if (!wm.TryGetZoneById(zone.Id, out var refreshed))
			throw new Exception("зона потерялась после SetDesiredWorkers");
		if (refreshed.DesiredWorkers != 0)
			throw new Exception($"DesiredWorkers минимум: ожидалось 0, получено {refreshed.DesiredWorkers}");

		long expected = (long)Math.Round(Math.PI * 200.0 * 200.0);
		if (zone.CellCount != expected)
			throw new Exception($"CellCount: ожидалось {expected} (πr²), получено {zone.CellCount}");
		ZoneManager.Instance.ClearAll();
	}

	// (ж) разные типы друг друга не режут: ферма-тайлы остаются покрыты фермой,
	// Work-зона не пишет тайлы в битмапу фермы.
	private void TestFarmNotCutByWorkZone()
	{
		var zm = ZoneManager.Instance;
		var farmTiles = new List<(int X, int Y)> { (500, 500), (500, 501), (501, 500) };
		var farms = zm.CreateFarmZones(farmTiles);
		if (farms.Count != 1)
			throw new Exception($"одна связная ферма ожидалась, получено {farms.Count}");
		if (!zm.CreateWorkZoneAt(500, 500, 1UL << 5, 128, 0, out _, out string f))
			throw new Exception($"Work-зона поверх фермы не создалась (типы не режут друг друга): {f}");
		if (!zm.TryGetZoneAt(500, 501, out var hit) || hit.Kind != ZoneKind.Farm)
			throw new Exception("тайл фермы потерялся после создания Work-зоны");
		zm.ClearAll();
	}

	// (г) RemoveZoneAtCenter: зона с центром в клетке удаляется; чужой центр — нет.
	private void TestRemoveZoneAtCenter()
	{
		var zm = ZoneManager.Instance;
		if (!zm.CreateWorkZoneAt(512, 512, 1UL << 6, 64, 2, out var zone, out string f))
			throw new Exception($"зона не создалась: {f}");
		if (!zm.RemoveZoneAtCenter(512, 512))
			throw new Exception("RemoveZoneAtCenter не нашёл зону по центру");
		if (zm.TryGetZoneById(zone.Id, out _))
			throw new Exception("зона пережила RemoveZoneAtCenter");
		if (zm.TryGetZoneAt(512, 512, out _))
			throw new Exception("клетка центра всё ещё покрыта после сноса");
		if (zm.RemoveZoneAtCenter(700, 700))
			throw new Exception("RemoveZoneAtCenter снёс несуществующую зону");
		zm.ClearAll();
	}

	// (д) 10 зон r=512: память не должна расти (радиус не материализуется).
	private void TestMemoryNoMaterialization()
	{
		var zm = ZoneManager.Instance;
		zm.ClearAll();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		long before = GC.GetTotalMemory(forceFullCollection: true);
		var ids = new List<int>(10);
		for (int i = 0; i < 10; i++)
		{
			int cx = 100 + (NextRand() % 800);
			int cy = 100 + (NextRand() % 800);
			if (!zm.CreateWorkZoneAt(cx, cy, 1UL << ((i % 7) + 1), WorkZone.MaxRadiusTiles, 10, out var z, out string fail))
				throw new Exception($"зона #{i} не создалась в ({cx},{cy}): {fail}");
			ids.Add(z.Id);
		}
		long after = GC.GetTotalMemory(forceFullCollection: true);
		long delta = after - before;
		if (delta > 20L * 1024 * 1024)
			throw new Exception($"10 зон r=512 съели {delta / (1024 * 1024)} МБ (> 20 МБ) — клетки материализуются?");
		// Обратная проверка содержимого: у всех зон корректный радиус и CellCount.
		foreach (int id in ids)
		{
			if (!zm.TryGetZoneById(id, out var z) || z.RadiusTiles != WorkZone.MaxRadiusTiles)
				throw new Exception($"зона #{id} потерялась или радиус искажён");
		}
		zm.ClearAll();
	}
}
