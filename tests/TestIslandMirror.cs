using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.Core.WorldStreaming;
using Game.Core.WorldStreaming.Integration;
using Game.Core.WorldStreaming.Mutation;

namespace Game.Tests;

/// <summary>
/// §118 фаза 4: островной стор дельт (IslandOverrideStore) — единый канал мутаций
/// острова. Проверяет контракт аудита MapDataWriter:
///  * §6.6 идемпотентность: повторная запись → false, без исключений;
///  * §6.8 батч: 10 000 клеток за один захват, дубли внутри батча не применяются;
///  * §6.7 события стреляют только при реальном изменении;
///  * §5.4 персистентность: Save → Load round-trip (у острова её не было вообще);
///  * §6.11 смена сида инвалидирует стор целиком (чужие дельты не прилипают);
///  * перечисление по области/виду; RemoveBatch; Clear.
/// </summary>
public partial class TestIslandMirror : RefCounted
{
	// Стабильные теги WorldDeltaKind (номера не изобретаем).
	private const byte KindCutTree = WorldDeltaKind.CutTree;
	private const byte KindMinedStone = WorldDeltaKind.MinedStone;
	private const byte KindWall = WorldDeltaKind.Wall;

	public bool Run()
	{
		GD.Print("==================================================");
		GD.Print("[ISLAND-STORE] Starting island override store audit (§118 phase 4)");
		GD.Print("==================================================");
		string dir = Path.Combine(OS.GetUserDataDir(), $"island_store_{Guid.NewGuid():N}");
		try
		{
			TestIdempotency();
			TestBatch();
			TestEvents();
			TestPersistence(dir);
			TestSeedChange();
			TestEnumerateAndRemove();
			TestMirrorMapping();
			TestMirrorForwarding();
			TestMirrorLifecycle();
			TestApplicator(dir);

			GD.Print("==================================================");
			GD.Print("[ISLAND-STORE OK] ALL TESTS PASSED! (Code 0)");
			GD.Print("==================================================");
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[ISLAND-STORE FAILED] {ex.Message}\n{ex.StackTrace}");
			return false;
		}
		finally
		{
			try
			{
				if (Directory.Exists(dir))
					Directory.Delete(dir, recursive: true);
			}
			catch
			{
				// мягкая уборка: хвосты I/O не должны валить тест
			}
		}
	}

	/// <summary>§6.6: повторная запись/снятие — false, без исключений и ретраев.</summary>
	private void TestIdempotency()
	{
		var store = new IslandOverrideStore(seed: 7);
		if (!store.TryApply(KindCutTree, 10, 20))
			throw new Exception("первая запись метки вернула false");
		if (store.TryApply(KindCutTree, 10, 20))
			throw new Exception("повторная запись метки вернула true (нарушена идемпотентность §6.6)");
		if (store.Count != 1)
			throw new Exception($"после двойной записи Count={store.Count} (ожидался 1)");

		// Разные kind в одной клетке — разные метки (чертёж + дерево, аудит §6.3).
		if (!store.TryApply(KindMinedStone, 10, 20))
			throw new Exception("метка другого вида в той же клетке не применилась");
		if (store.Count != 2)
			throw new Exception($"разные виды в клетке не различились: Count={store.Count}");

		if (!store.TryRemove(KindCutTree, 10, 20))
			throw new Exception("снятие существующей метки вернуло false");
		if (store.TryRemove(KindCutTree, 10, 20))
			throw new Exception("повторное снятие вернуло true");
		if (store.Contains(KindCutTree, 10, 20))
			throw new Exception("метка осталась после снятия");
		GD.Print("[ISLAND-STORE] идемпотентность (§6.6): OK");
	}

	/// <summary>§6.8: батч 10 000 клеток за один захват; дубли внутри батча не применяются.</summary>
	private void TestBatch()
	{
		var store = new IslandOverrideStore();
		const int n = 10000;
		var batch = new WorldMutation[n];
		for (int i = 0; i < n; i++)
			batch[i] = new WorldMutation(KindMinedStone, 1000 + i % 200, 2000 + i / 200);

		int applied = store.ApplyBatch(batch);
		// 200 x 50 = 10 000 уникальных клеток (i/200 даёт 50 рядов).
		if (applied != n)
			throw new Exception($"батч применил {applied} из {n}");
		if (store.Count != n)
			throw new Exception($"после батча Count={store.Count} (ожидался {n})");

		// Повторный батч тех же клеток — ничего нового.
		if (store.ApplyBatch(batch) != 0)
			throw new Exception("повторный батч применил записи (нарушена идемпотентность батча)");
		if (store.Count != n)
			throw new Exception($"Count изменился после повторного батча: {store.Count}");

		// Батч с дублями ВНУТРИ себя: 100 записей одной клетки → 1 применение.
		var dup = new WorldMutation[100];
		for (int i = 0; i < dup.Length; i++)
			dup[i] = new WorldMutation(KindWall, 555, 666);
		if (store.ApplyBatch(dup) != 1)
			throw new Exception("дубли внутри батча применились повторно");
		GD.Print($"[ISLAND-STORE] батч {n} клеток за один захват (§6.8): OK, Count={store.Count}");
	}

	/// <summary>§6.7: события стреляют только при реальном изменении.</summary>
	private void TestEvents()
	{
		var store = new IslandOverrideStore();
		int applied = 0, removed = 0;
		byte lastKind = 0;
		long lastX = -1, lastY = -1;
		store.OnMutationApplied += (kind, x, y) => { applied++; lastKind = kind; lastX = x; lastY = y; };
		store.OnMutationRemoved += (kind, x, y) => removed++;

		store.TryApply(KindCutTree, 3, 4);
		store.TryApply(KindCutTree, 3, 4); // дубль — событие не должно стрелять
		store.TryApply(KindWall, 5, 6);
		store.TryRemove(KindWall, 5, 6);
		store.TryRemove(KindWall, 5, 6); // повтор — не должно стрелять

		if (applied != 2 || removed != 1)
			throw new Exception($"события: applied={applied} (ожидалось 2), removed={removed} (ожидалось 1)");
		if (lastKind != KindWall || lastX != 5 || lastY != 6)
			throw new Exception($"последнее событие: kind={lastKind} ({lastX},{lastY}) — ожидалось Wall (5,6)");
		GD.Print("[ISLAND-STORE] события только на реальном изменении (§6.7): OK");
	}

	/// <summary>§5.4: персистентность острова — Save → Load побитово.</summary>
	private void TestPersistence(string dir)
	{
		Directory.CreateDirectory(dir);
		string path = Path.Combine(dir, "island.delta");

		var store = new IslandOverrideStore(seed: 0xABCDEF01u);
		var payloadA = new byte[] { 1, 2, 3, 4, 5 };
		store.TryApply(KindWall, 100, 200, payloadA);
		store.TryApply(KindCutTree, -50, 700); // отрицательная координата + null payload
		for (int i = 0; i < 500; i++)
			store.TryApply(KindMinedStone, i, i * 2);
		int before = store.Count;

		store.Save(path);
		if (!File.Exists(path))
			throw new Exception("файл дельт не создан");
		if (File.Exists(path + ".tmp"))
			throw new Exception("временный файл не убран после атомарной замены");

		var loaded = new IslandOverrideStore();
		if (!loaded.Load(path))
			throw new Exception("Load вернул false для существующего файла");
		if (loaded.Count != before)
			throw new Exception($"после Load Count={loaded.Count} (ожидался {before})");
		if (loaded.Seed != 0xABCDEF01u)
			throw new Exception($"после Load Seed=0x{loaded.Seed:X8} (ожидался 0xABCDEF01)");
		if (!loaded.Contains(KindWall, 100, 200))
			throw new Exception("метка стены не пережила round-trip");
		if (!loaded.Contains(KindCutTree, -50, 700))
			throw new Exception("метка с отрицательной координатой не пережила round-trip");
		if (!loaded.Contains(KindMinedStone, 499, 998))
			throw new Exception("метка из хвоста списка не пережила round-trip");
		if (!loaded.TryGet(KindWall, 100, 200, out byte[] payloadB) || payloadB == null
			|| payloadB.Length != payloadA.Length)
			throw new Exception("payload не пережил round-trip");
		for (int i = 0; i < payloadA.Length; i++)
			if (payloadA[i] != payloadB[i])
				throw new Exception($"payload повреждён на байте {i}");

		// Повторное сохранение поверх существующего файла (замена, не дубль).
		loaded.TryApply(KindWall, 999, 999);
		loaded.Save(path);
		var reloaded = new IslandOverrideStore();
		reloaded.Load(path);
		if (reloaded.Count != before + 1 || !reloaded.Contains(KindWall, 999, 999))
			throw new Exception("перезапись файла дельт потеряла данные");

		// Нет файла — не ошибка (пустой остров).
		if (new IslandOverrideStore().Load(Path.Combine(dir, "no_such.delta")))
			throw new Exception("Load вернул true для отсутствующего файла");

		// Чужой файл — исключение, а не тихая потеря дельт.
		string junkPath = Path.Combine(dir, "junk.delta");
		File.WriteAllBytes(junkPath, new byte[] { 9, 9, 9, 9, 1, 2, 3, 4 });
		bool threw = false;
		try
		{
			new IslandOverrideStore().Load(junkPath);
		}
		catch (InvalidDataException)
		{
			threw = true;
		}
		if (!threw)
			throw new Exception("битый файл дельт не был отвергнут");
		GD.Print($"[ISLAND-STORE] персистентность round-trip {before}+500 меток (§5.4): OK");
	}

	/// <summary>§6.11: смена сида чистит стор целиком.</summary>
	private void TestSeedChange()
	{
		var store = new IslandOverrideStore(seed: 111);
		store.TryApply(KindWall, 1, 1);
		store.TryApply(KindCutTree, 2, 2);

		if (store.RebindSeed(111))
			throw new Exception("RebindSeed сообщил смену при том же сиде");
		if (store.Count != 2)
			throw new Exception($"RebindSeed с тем же сидом потерял метки: Count={store.Count}");

		int removedEvents = 0;
		store.OnMutationRemoved += (k, x, y) => removedEvents++;
		if (!store.RebindSeed(222))
			throw new Exception("RebindSeed не сообщил смену сида");
		if (store.Count != 0)
			throw new Exception($"смена сида не очистила стор: Count={store.Count}");
		if (removedEvents != 2)
			throw new Exception($"смена сида дала {removedEvents} событий снятия (ожидалось 2)");
		GD.Print("[ISLAND-STORE] смена сида инвалидирует стор (§6.11): OK");
	}

	/// <summary>Перечисление по области и виду, RemoveBatch, Clear.</summary>
	private void TestEnumerateAndRemove()
	{
		var store = new IslandOverrideStore();
		store.TryApply(KindWall, 10, 10);
		store.TryApply(KindWall, 20, 20);
		store.TryApply(KindWall, 300, 300);
		store.TryApply(KindCutTree, 10, 10);

		var list = new List<(byte Kind, long CellX, long CellY)>();
		int walls = store.Enumerate(0, 0, 100, 100, KindWall, list);
		if (walls != 2)
			throw new Exception($"Enumerate(Wall, 0..100) вернул {walls} (ожидалось 2)");
		if (list.Exists(r => r.CellX == 300))
			throw new Exception("Enumerate вернул метку вне прямоугольника");

		if (store.Enumerate(0, 0, 400, 400, 0, list) != 4)
			throw new Exception($"Enumerate(kind=0, вся область) вернул {list.Count} (ожидалось 4)");
		if (store.Enumerate(0, 0, 400, 400, KindCutTree, list) != 1)
			throw new Exception("фильтр по виду не сработал");

		// RemoveBatch — снятие пачки за один захват.
		var batch = new WorldMutation[]
		{
			new WorldMutation(KindWall, 10, 10),
			new WorldMutation(KindWall, 20, 20),
			new WorldMutation(KindWall, 777, 777), // нет такой — не считается
		};
		if (store.RemoveBatch(batch) != 2)
			throw new Exception("RemoveBatch снял не то число меток");
		if (store.Count != 2)
			throw new Exception($"после RemoveBatch Count={store.Count} (ожидалось 2)");

		if (store.Clear() != 2 || store.Count != 0)
			throw new Exception("Clear не очистил стор");
		GD.Print("[ISLAND-STORE] перечисление/RemoveBatch/Clear: OK");
	}

	// ================================================================== //
	//  Зеркало (IslandMutationMirror, аудит §5.3): отображение событий     //
	//  менеджеров в виды дельты и запись в единый канал.                  //
	//  Обработчики вызываются напрямую: события менеджеров идут через      //
	//  CallDeferred и в headless-тесте на _init() не доставляются.        //
	// ================================================================== //

	private static StructureDefinition WallDef()
		=> new("wall_test", "Тестовая стена", default(BuildingType), StructureCategory.Wall,
			Array.Empty<(ItemId Item, int Count)>(), 1f, true, true);

	private static StructureDefinition FurnitureDef()
		=> new("bench_test", "Тестовый верстак", default(BuildingType), StructureCategory.Furniture,
			Array.Empty<(ItemId Item, int Count)>(), 1f, true, true);

	/// <summary>Чистое отображение «постройка → вид дельты» (стена vs здание).</summary>
	private void TestMirrorMapping()
	{
		if (IslandMutationMirror.DoneKindFor(WallDef()) != WorldDeltaKind.Wall)
			throw new Exception("стена не отобразилась в WorldDeltaKind.Wall");
		if (IslandMutationMirror.DoneKindFor(FurnitureDef()) != WorldDeltaKind.Building)
			throw new Exception("мебель не отобразилась в WorldDeltaKind.Building");
		if (IslandMutationMirror.DoneKindFor(null) != WorldDeltaKind.Building)
			throw new Exception("null-определение должно давать Building (безопасный дефолт)");

		if (IslandMutationMirror.PlanKindFor(WallDef()) != WorldDeltaKind.WallPlan)
			throw new Exception("чертёж стены не отобразился в WallPlan");
		if (IslandMutationMirror.PlanKindFor(FurnitureDef()) != WorldDeltaKind.BuildingPlan)
			throw new Exception("чертёж мебели не отобразился в BuildingPlan");

		// Согласованность с WorldDeltaKind: завершение плана даёт готовый вид.
		if (WorldDeltaKind.DoneFor(IslandMutationMirror.PlanKindFor(WallDef())) != WorldDeltaKind.Wall)
			throw new Exception("PlanKindFor/DoneFor рассинхронизированы для стены");
		GD.Print("[ISLAND-MIRROR] отображение постройка→вид дельты: OK");
	}

	/// <summary>Сквозной путь «событие домена → вид дельты → островной стор».</summary>
	private void TestMirrorForwarding()
	{
		var store = new IslandOverrideStore(seed: 1);
		using var mirror = new IslandMutationMirror(store);
		// Менеджеры дают локальные координаты, стор — мировые: проверяем перевод.
		const long originX = 1000;
		const long originY = 2000;
		mirror.SetZoneOrigin(originX, originY);
		long Wx(int lx) => originX + lx;
		long Wy(int ly) => originY + ly;

		// Лес и камень: разметка = план, выполнение = снять план + готовое.
		mirror.OnTreeMarked((11, 12));
		mirror.OnTreeChopped((11, 12));
		mirror.OnStoneMarked((13, 14));
		mirror.OnStoneMined((13, 14));
		// Батч-разметка леса и камня.
		mirror.OnTreesBatchMarked(new List<(int X, int Y)> { (15, 16) });
		mirror.OnStonesBatchMarked(new List<(int X, int Y)> { (17, 18) });

		// Ферма: разметка = план; завершение = снять план + поставить готовое.
		mirror.OnPlotMarked((21, 22));
		mirror.OnPlotCompleted((21, 22));

		// Склад: одиночная клетка и батч.
		mirror.OnZoneTileAdded((31, 32));
		mirror.OnZoneTilesBatchAdded(new (int X, int Y)[] { (33, 34), (35, 36) });

		// Постройка: готовая стена и снятие мебели.
		mirror.OnStructureAdded(new StructureInstance(41, 42, WallDef()));
		mirror.OnStructureRemoved((43, 44), FurnitureDef());

		// Стройка: чертёж стены → завершение (план снимается, ставится готовое).
		mirror.OnSiteAdded(new ConstructionSite(51, 52, WallDef()));
		mirror.OnSiteCompleted(new ConstructionSite(51, 52, WallDef()));

		if (!store.Contains(WorldDeltaKind.CutTree, Wx(11), Wy(12)))
			throw new Exception("рубка дерева не дошла до стора");
		if (store.Contains(WorldDeltaKind.CutTreePlan, Wx(11), Wy(12)))
			throw new Exception("план рубки не снят при выполнении");
		if (!store.Contains(WorldDeltaKind.MinedStone, Wx(13), Wy(14)))
			throw new Exception("добыча камня не дошла до стора");
		if (store.Contains(WorldDeltaKind.MinedStonePlan, Wx(13), Wy(14)))
			throw new Exception("план добычи не снят при выполнении");
		if (!store.Contains(WorldDeltaKind.CutTreePlan, Wx(15), Wy(16)))
			throw new Exception("батч-разметка леса не дала план");
		if (!store.Contains(WorldDeltaKind.MinedStonePlan, Wx(17), Wy(18)))
			throw new Exception("батч-разметка камня не дала план");
		if (!store.Contains(WorldDeltaKind.Crop, Wx(21), Wy(22)))
			throw new Exception("завершённая грядка не дошла до стора");
		if (store.Contains(WorldDeltaKind.CropPlan, Wx(21), Wy(22)))
			throw new Exception("план грядки не снят при завершении");
		if (!store.Contains(WorldDeltaKind.Stockpile, Wx(31), Wy(32))
			|| !store.Contains(WorldDeltaKind.Stockpile, Wx(33), Wy(34))
			|| !store.Contains(WorldDeltaKind.Stockpile, Wx(35), Wy(36)))
			throw new Exception("клетки склада (в т.ч. батч) не дошли до стора");
		if (!store.Contains(WorldDeltaKind.Wall, Wx(41), Wy(42)))
			throw new Exception("построенная стена не дошла до стора как Wall");
		if (store.Contains(WorldDeltaKind.Building, Wx(43), Wy(44)))
			throw new Exception("снятая мебель осталась в сторе");
		if (!store.Contains(WorldDeltaKind.Wall, Wx(51), Wy(52)))
			throw new Exception("завершённая стройка не дала готовую стену");
		if (store.Contains(WorldDeltaKind.WallPlan, Wx(51), Wy(52)))
			throw new Exception("чертёж стены не снят после завершения стройки");

		// Локальные координаты НЕ должны попадать в стор как есть (перевод обязателен).
		if (store.Contains(WorldDeltaKind.CutTree, 11, 12))
			throw new Exception("локальная координата записалась в стор без перевода в мировую");

		// Снятие разметки фермы и зоны склада батчами.
		mirror.OnPlotsBatchUnmarked(new List<(int X, int Y)> { (21, 22) });
		mirror.OnZoneTilesBatchRemoved(new (int X, int Y)[] { (31, 32) });
		if (store.Contains(WorldDeltaKind.Crop, Wx(21), Wy(22)))
			throw new Exception("батч-снятие разметки не убрало готовую грядку");
		if (store.Contains(WorldDeltaKind.Stockpile, Wx(31), Wy(32)))
			throw new Exception("батч-снятие не убрало клетку склада");

		// Стройка, снятая без завершения: событие не несёт определение → должны уйти оба плана.
		mirror.OnSiteAdded(new ConstructionSite(61, 62, FurnitureDef()));
		mirror.OnSiteRemoved((61, 62));
		if (store.Contains(WorldDeltaKind.BuildingPlan, Wx(61), Wy(62)))
			throw new Exception("чертёж здания не снят при отмене стройки");

		// Мир-стриминг не тронут: зеркало пишет ТОЛЬКО в островной стор (§6.1).
		if (mirror.Mirrored <= 0)
			throw new Exception("счётчик зеркала не увеличился");
		GD.Print($"[ISLAND-MIRROR] события 6 доменов → стор: OK (Mirrored={mirror.Mirrored}, Count={store.Count})");
	}

	/// <summary>Attach/Detach идемпотентны и не бросают (в т.ч. повторные вызовы).</summary>
	private void TestMirrorLifecycle()
	{
		var store = new IslandOverrideStore();
		var mirror = new IslandMutationMirror(store);
		mirror.Attach();
		mirror.Attach(); // двойная подписка недопустима
		mirror.Detach();
		mirror.Detach();
		mirror.Dispose(); // Dispose после Detach — без исключения
		if (store.Count != 0)
			throw new Exception("Attach/Detach сами по себе не должны писать в стор");

		// После Detach зеркало продолжает работать как пересыльщик (обработчики живы),
		// но повторная подписка не накапливается: повторный цикл не должен падать.
		mirror.Attach();
		mirror.SetZoneOrigin(0, 0);
		mirror.OnTreeChopped((7, 8));
		if (!store.Contains(WorldDeltaKind.CutTree, 7, 8))
			throw new Exception("после повторного Attach обработчики не работают");
		mirror.Detach();
		GD.Print("[ISLAND-MIRROR] жизненный цикл подписки (идемпотентность): OK");
	}

	/// <summary>
	/// Применитель дельт к зоне (IslandDeltaApplicator): база + дельта = состояние,
	/// и полный круг «мутация → стор → файл → новый стор → свежая зона → флаг снят».
	/// </summary>
	private void TestApplicator(string dir)
	{
		var store = new IslandOverrideStore(seed: 42);
		ActiveArea FindArea(out long treeWx, out long treeWy, out ulong usedSeed)
		{
			const uint version = 3;
			for (int attempt = 0; attempt < 8; attempt++)
			{
				ulong seed = 0x51EED4C0FFEEUL + (ulong)attempt * 7919UL;
				var probe = new ActiveArea(4096, 8192);
				probe.Fill(seed, version);
				for (int i = 0; i < probe.TreeOnGrass.Length; i++)
				{
					if (!probe.TreeOnGrass[i])
						continue;
					treeWx = probe.ZoneOriginX + i % ActiveArea.Size;
					treeWy = probe.ZoneOriginY + i / ActiveArea.Size;
					usedSeed = seed;
					return probe;
				}
			}
			throw new Exception("не найдено дерево в восьми пробных зонах (ожидался лес)");
		}

		ActiveArea area = FindArea(out long treeX, out long treeY, out ulong baseSeed);
		int treeIdx = (int)((treeY - area.ZoneOriginY) * ActiveArea.Size + (treeX - area.ZoneOriginX));
		if (!area.TreeOnGrass[treeIdx])
			throw new Exception("клетка с деревом не нашлась в базе");
		if (!store.TryApply(WorldDeltaKind.CutTree, treeX, treeY))
			throw new Exception("дельта рубки не записалась в стор");

		IslandDeltaApplyResult result = IslandDeltaApplicator.Apply(area, store);
		if (area.TreeOnGrass[treeIdx])
			throw new Exception("дельта рубки не сняла дерево с базовой зоны");
		if (result.Applied != 1)
			throw new Exception($"применено {result.Applied} дельт (ожидалась 1)");

		// Повторное применение к уже поправленной зоне — идемпотентно: 0 новых изменений.
		if (IslandDeltaApplicator.Apply(area, store).Applied != 0)
			throw new Exception("повторное применение дельт не идемпотентно");

		// Вне зоны: мировая клетка далеко за кромкой не должна трогать зону вовсе.
		var distantStore = new IslandOverrideStore(seed: 42);
		distantStore.TryApply(WorldDeltaKind.CutTree, area.ZoneOriginX + 5000, area.ZoneOriginY);
		IslandDeltaApplyResult outside = IslandDeltaApplicator.Apply(area, distantStore);
		if (outside.Applied != 0)
			throw new Exception($"дельта вне зоны что-то изменила: applied={outside.Applied}");

		// §118 ф.5: доменные виды больше не «сохраняются и не применяются» — они
		// восстанавливаются через IslandDomainReplay (StructureManager и т.д.).
		// Проверяем: стена не осталась в отложенных и реально появилась в домене.
		store.TryApply(WorldDeltaKind.Wall, treeX, treeY);
		IslandDeltaApplyResult domain = IslandDeltaApplicator.Apply(area, store);
		if (domain.SkippedDomainKinds != 0)
			throw new Exception($"доменная метка не восстановлена, отложено: {domain.SkippedDomainKinds}");
		if (!StructureManager.Instance.HasStructureAt((int)treeX, (int)treeY))
			throw new Exception("реплей стены не восстановил структуру в StructureManager");
		// Идемпотентность реплея: повтор не должен считать применённое заново.
		if (IslandDeltaApplicator.Apply(area, store).Applied != 0)
			throw new Exception("повторный реплей доменных дельт не идемпотентен");

		// ПОЛНЫЙ КРУГ: файл → новый стор → свежая зона с тем же сидом → дерево снято.
		string path = Path.Combine(dir, "roundtrip.island.delta");
		store.Save(path);
		var reloaded = new IslandOverrideStore();
		if (!reloaded.Load(path))
			throw new Exception("round-trip: файл дельт не загрузился");

		var fresh = new ActiveArea(area.ZoneOriginX, area.ZoneOriginY);
		// База пересоздаётся ТЕМ ЖЕ сидом, который нашёл дерево (не хардкод).
		fresh.Fill(baseSeed, 3);
		if (!fresh.TreeOnGrass[treeIdx])
			throw new Exception("свежая база не содержит дерево на исходной клетке (сид не совпал)");
		IslandDeltaApplyResult roundTrip = IslandDeltaApplicator.Apply(fresh, reloaded);
		if (fresh.TreeOnGrass[treeIdx])
			throw new Exception("после round-trip дельта не восстановила срубленное дерево");
		if (roundTrip.Applied < 1)
			throw new Exception("round-trip не применил ни одной дельты");
		GD.Print($"[ISLAND-APPLY] база+дельта, вне зоны, доменные виды, round-trip: OK ({roundTrip})");
	}
}