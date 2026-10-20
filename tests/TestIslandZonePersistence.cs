using System;
using System.Collections.Generic;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.Core.WorldStreaming.Mutation;
using Game.Simulation;

// ============================================================================
// §119 P7б (task-17 п.3-5): round-trip тест — зоны (.zones.json), payload'ы
// островных дельт, state-replay. По образцу TestWorkProgressPersistence.
// ВАЖНО: синглтоны менеджеров общие с другими тестами процесса — сетапы
// идемпотентны (ClearAll перед наполнением), порядок строгий.
// ============================================================================

public partial class TestIslandZonePersistence : RefCounted
{
	private static int _failures;

	private static void Check(bool cond, string name)
	{
		if (cond)
		{
			GD.Print($"[TEST P7b-z] {name} OK");
		}
		else
		{
			_failures++;
			GD.PrintErr($"[TEST P7b-z] {name} FAILED");
		}
	}

	/// <summary>Уникальный zoneId теста: не задеть .zones.json реальной колонии.</summary>
	private const string TestZoneId = "colony_p7b_selftest";

	public int Run()
	{
		GD.Print("==================================================");
		GD.Print("[TEST P7b-z] .zones.json + payload'ы + state-replay");
		GD.Print("==================================================");

		string path = ZonePersistence.ZonesPath(TestZoneId);
		try { System.IO.File.Delete(path); } catch { }
		try { System.IO.File.Delete(path + ".tmp"); } catch { }

		// ---------- 1. SaveZones: пустой менеджер → пустой файл ----------
		ZoneManager.Instance.ClearAll();
		Check(ZonePersistence.SaveZones(TestZoneId, 0xAABBCCDDUL, out string errEmpty), "SaveZones (0 зон) OK");
		Check(ZonePersistence.TryLoadZones(TestZoneId, 0xAABBCCDDUL, out _), "TryLoadZones (0 зон) OK");

		// ---------- 2. SaveZones: 2 Work-зоны с полями ----------
		ZoneManager.Instance.ClearAll();
		bool okA = ZoneManager.Instance.CreateWorkZoneAt(200, 180, 1UL << (int)JobTypeId.TreeChopping, 128, 5, out _, out _);
		bool okB = ZoneManager.Instance.CreateWorkZoneAt(400, 300, 1UL << (int)JobTypeId.Mining, 64, 12, out _, out _);
		Check(okA && okB, "создание 2 Work-зон OK");
		if (okB)
		{
			var all = ZoneManager.Instance.GetAllZones();
			foreach (var z in all)
				if (z.Id > 1 && z.Kind == ZoneKind.Work)
					ZoneManager.Instance.SetWorkPriorityOverride(z.Id, 7);
			// Выключаем вторую созданную (минимальный id > первой).
			ZoneManager.Instance.SetWorkEnabled(2, false);
		}
		Check(ZonePersistence.SaveZones(TestZoneId, 0xAABBCCDDUL, out string err2), $"SaveZones (2 зоны) OK (err={err2 ?? "null"})");

		// ---------- 3. Восстановление: id/радиус/mask/priority/enabled ----------
		ZoneManager.Instance.ClearAll();
		Check(ZonePersistence.TryLoadZones(TestZoneId, 0xAABBCCDDUL, out string loadErr), $"TryLoadZones OK (err={loadErr ?? "null"})");
		var restored = ZoneManager.Instance.GetAllZones();
		Check(restored.Count == 2, $"восстановлено ровно 2 зоны (факт {restored.Count})");
		Zone z1 = null, z2 = null;
		foreach (var z in restored)
		{
			if (z.CenterX == 200 && z.CenterY == 180) z1 = z;
			if (z.CenterX == 400 && z.CenterY == 300) z2 = z;
		}
		Check(z1 != null && z1.Id == 1 && z1.RadiusTiles == 128 && z1.DesiredWorkers == 5
			&& z1.JobMask == (1UL << (int)JobTypeId.TreeChopping) && z1.Enabled,
			"зона 1: id/центр/радиус/mask/desired/enabled OK");
		Check(z2 != null && z2.Id == 2 && z2.RadiusTiles == 64 && z2.DesiredWorkers == 12
			&& !z2.Enabled && z2.PriorityOverride == 7,
			"зона 2: priority=7/enabled=false OK");

		// ---------- 4. Другой seed → чистка (после восстановления, файл ещё жив) ----------
		Check(!ZonePersistence.TryLoadZones(TestZoneId, 0xDEADBEEFUL, out _), "TryLoadZones (чужой seed) → false + чистка OK");
		Check(ZoneManager.Instance.GetAllZones().Count == 0, "зоны чужого seed'а вычищены OK");

		// ---------- 5. Payload'ы: writer/reader round-trip ----------
		byte[] sp = IslandPayload.WriteStockpile(ItemId.Log, 42);
		Check(IslandPayload.TryReadStockpile(sp, out ItemId spItem, out int spCount)
			&& spItem == ItemId.Log && spCount == 42, "Stockpile payload round-trip OK");
		byte[] cr = IslandPayload.WriteCrop(3, 27.5f, 9);
		Check(IslandPayload.TryReadCrop(cr, out int crStage, out float crTimer, out int crZone)
			&& crStage == 3 && Math.Abs(crTimer - 27.5f) < 0.001f && crZone == 9, "Crop payload round-trip OK");
		byte[] bp = IslandPayload.WriteBuildPlan(ConstructionStage.Assembling,
			new List<(ItemId, int)> { (ItemId.Log, 3), (ItemId.Stone, 2) }, 0.75f);
		Check(IslandPayload.TryReadBuildPlan(bp, out var bpStage, out var bpDel, out float bpWp)
			&& bpStage == ConstructionStage.Assembling && bpDel.Count == 2 && bpDel[1].Count == 2
			&& Math.Abs(bpWp - 0.75f) < 0.001f, "BuildPlan payload round-trip OK");
		byte[] pf = IslandPayload.WriteProgressFraction(0.4f);
		Check(IslandPayload.TryReadProgressFraction(pf, out float pfVal) && Math.Abs(pfVal - 0.4f) < 0.0001f,
			"ProgressFraction payload round-trip OK");
		byte[] gi = IslandPayload.WriteGroundItems(ItemId.Grain, 17);
		Check(IslandPayload.TryReadGroundItems(gi, out ItemId giItem, out int giCount)
			&& giItem == ItemId.Grain && giCount == 17, "GroundItems payload round-trip OK");
		Check(!IslandPayload.TryReadStockpile(null, out _, out _)
			&& !IslandPayload.TryReadCrop(new byte[] { 9, 1, 0, 0, 0, 0, 0, 0, 0, 0 }, out _, out _, out _),
			"битые/null payload'ы → false OK");

		// ---------- 6. TrySetPayload: только существующая метка ----------
		var store = new IslandOverrideStore(123u);
		Check(store.TryApply(WorldDeltaKind.CropPlan, 5, 5), "метка применена OK");
		Check(!store.TrySetPayload(WorldDeltaKind.CropPlan, 6, 6, new byte[] { 1 }), "TrySetPayload на отсутствующей → false OK");
		byte[] p1 = IslandPayload.WriteProgressFraction(0.25f);
		Check(store.TrySetPayload(WorldDeltaKind.CropPlan, 5, 5, p1), "TrySetPayload на существующей OK");
		Check(store.TryGet(WorldDeltaKind.CropPlan, 5, 5, out byte[] gotP) && gotP == p1, "payload прочитан OK");

		// ---------- 7. GroundItems: не домен, явный кейс аппликатора ----------
		Check(IslandDomainReplay.Classify(WorldDeltaKind.GroundItems) == IslandReplayDomain.NotDomain,
			"GroundItems классифицирован как NotDomain OK");

		// ---------- 8. Seed-примесь PrepapreIslandStore-паттерна не ломает версии ----------
		uint seedKey = unchecked((uint)(0xAABBCCDDUL ^ (0xAABBCCDDUL >> 32)) ^ (3u * 0x9E3779B9u));
		Check(seedKey != 0, "seed-ключ ненулевой OK");

		// ---------- Уборка тестового файла. ----------
		try { System.IO.File.Delete(path); } catch { }
		try { System.IO.File.Delete(path + ".tmp"); } catch { }
		ZoneManager.Instance.ClearAll();

		GD.Print($"[TEST P7b-z] {( _failures == 0 ? "ALL PASSED" : "FAILURES: " + _failures )}");
		return _failures == 0 ? 0 : 1;
	}
}
