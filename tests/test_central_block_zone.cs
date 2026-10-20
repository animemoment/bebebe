using System;
using Godot;
using Game.Core;
using Game.Simulation;
using Game.UI;

namespace Game.Tests;

// ============================================================================
// §119 P5: тест центрального блока (task-11). Запуск:
//   godot_run_headless --script tests/run_central_block_test.gd
// Проверки:
//   (а) StructureRegistry.CentralBlock: id/тип/категория Furniture/10 брёвен/
//       blocksMovement=false;
//   (б) BuildConfig.LogsFor(CentralBlock) == 10;
//   (в) MapRenderer: Source/Ghost маппинг CentralBlock (18/117);
//   (г) ИНВАРИАНТ «блок умер → зона умерла»: снос CentralBlock в центре зоны
//       через StructureManager.RemoveStructure гасит зону (RemoveZoneAtCenter),
//       работа из очереди зоны падает в непокрытые роутера, клетка теряет
//       владельца покрытия;
//   (д) снос ОБЫЧНОЙ структуры (WorkTable) зону НЕ гасит.
// ============================================================================

// Имя класса == basename файла (иначе Godot-генератор не эмитит ScriptPath
// и CSharpScript.new() из GDScript-раннера падает).

public partial class test_central_block_zone : RefCounted
{
	public bool Run()
	{
		try
		{
			TestRegistryAndConfig();
			GD.Print("[CENTRAL-BLOCK] (а) регистрация CentralBlock (10 дерева, Furniture, проходим): OK");

			TestMapRendererSources();
			GD.Print("[CENTRAL-BLOCK] (в) MapRenderer Source/Ghost маппинг: OK");

			TestDemolishKillsZoneAndDropsJobs();
			GD.Print("[CENTRAL-BLOCK] (г) снос блока гасит зону, работа → непокрытые: OK");

			TestOrdinaryRemovalKeepsZone();
			GD.Print("[CENTRAL-BLOCK] (д) снос обычной структуры зону не трогает: OK");

			GD.Print("==================================================");
			GD.Print("[TEST] CENTRAL BLOCK P5 PASSED! (Code 0)");
			GD.Print("==================================================");
			return true;
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[TEST FAILED] {ex}");
			return false;
		}
		finally
		{
			ZoneManager.Instance.ClearAll();
			ZoneJobRouter.Instance.Reset();
			StructureManager.Instance.ClearAll();
		}
	}

	// (а)+(б) определение структуры и цена.
	private void TestRegistryAndConfig()
	{
		var def = StructureRegistry.CentralBlock;
		if (def == null)
			throw new Exception("StructureRegistry.CentralBlock не зарегистрирован");
		if (def.Id != "central_block")
			throw new Exception($"id: ожидался central_block, получен {def.Id}");
		if (def.Type != BuildingType.CentralBlock)
			throw new Exception($"тип: ожидался CentralBlock, получен {def.Type}");
		if (def.Category != StructureCategory.Furniture)
			throw new Exception($"категория: ожидалась Furniture, получена {def.Category}");
		if (def.BlocksMovement)
			throw new Exception("CentralBlock НЕ должен блокировать навигацию");
		if (BuildConfig.LogsFor(BuildingType.CentralBlock) != 10)
			throw new Exception($"цена: ожидалось 10 брёвен, получено {BuildConfig.LogsFor(BuildingType.CentralBlock)}");
		if (def.GetRequiredCount(ItemId.Log) != 10)
			throw new Exception($"RequiredMaterials: ожидалось 10 Log, получено {def.GetRequiredCount(ItemId.Log)}");
		// Гет по строке тоже должен находить (меню/сейв идут по id).
		if (!StructureRegistry.TryGet("central_block", out var byId) || byId != def)
			throw new Exception("TryGet(central_block) не находит определение");
	}

	// (в) статические маппинги рендера (без инстанцирования Node — headless-safe).
	private void TestMapRendererSources()
	{
		if (MapRenderer.SourceForBuilding(BuildingType.CentralBlock) != MapRenderer.SourceCentralBlock)
			throw new Exception("SourceForBuilding(CentralBlock) != SourceCentralBlock");
		if (MapRenderer.GhostSourceForBuilding(BuildingType.CentralBlock) != MapRenderer.GhostCentralBlockSource)
			throw new Exception("GhostSourceForBuilding(CentralBlock) != GhostCentralBlockSource");
	}

	// (г) СЕРДЦЕ ПРАВИЛА «блок умер → зона умерла».
	private void TestDemolishKillsZoneAndDropsJobs()
	{
		var zm = ZoneManager.Instance;
		zm.ClearAll();
		ZoneManager.SetMapBounds(2048, 2048);
		var router = ZoneJobRouter.Instance;
		router.Reset();

		// Первый доступ к фасаду → static ctor подключает CentralBlockRemovedHook.
		_ = WorkZoneManager.Instance;
		if (StructureManager.CentralBlockRemovedHook == null)
			throw new Exception("CentralBlockRemovedHook не подключён (WorkZoneManager static ctor)");

		if (!WorkZoneManager.Instance.CreateZoneAt(512, 512, 1UL << (int)JobTypeId.TreeChopping, 128, 3,
				out var zone, out string fail))
			throw new Exception($"зона-якорь не создалась: {fail}");
		int zoneId = zone.Id;

		// Принудительный пересчёт покрытия (throttle 2с — синтетическое «позднее» время).
		const long nowBase = 10_000_000;
		router.Coverage.RebuildNow(nowBase);
		if (!router.Coverage.TryGetOwner(512, 512, JobTypeId.TreeChopping, out int owner) || owner != zoneId)
			throw new Exception($"покрытие центра не за зоной {zoneId} (owner={owner})");

		// Работа в зоне: пуш в покрытую клетку → очередь зоны, не непокрытые.
		router.Push(JobTypeId.TreeChopping, 512, 512, 777, nowBase);
		if (router.QueueDepth(zoneId) != 1)
			throw new Exception($"работа не легла в очередь зоны (depth={router.QueueDepth(zoneId)})");
		if (router.UncoveredCount != 0)
			throw new Exception($"покрытая работа попала в непокрытые (uncovered={router.UncoveredCount})");

		// Сам блок: поставить и снести тем же путём, что игра (StructureManager).
		var sm = StructureManager.Instance;
		sm.ClearAll();
		if (!sm.AddStructure(512, 512, StructureRegistry.CentralBlock))
			throw new Exception("AddStructure(CentralBlock) вернул false");
		if (!sm.RemoveStructure(512, 512))
			throw new Exception("RemoveStructure(CentralBlock) вернул false");

		// Зона с центром в этой клетке обязана исчезнуть.
		if (WorkZoneManager.Instance.TryGetZoneById(zoneId, out _))
			throw new Exception($"зона {zoneId} пережила снос центрального блока");
		if (router.UncoveredCount == 0)
			throw new Exception("работа из очереди убитой зоны не упала в непокрытые роутера");
		// Покрытие после пересчёта: клетка ничего не владеет.
		router.Coverage.RebuildNow(nowBase + 60_000);
		if (router.Coverage.TryGetOwner(512, 512, JobTypeId.TreeChopping, out int after))
			throw new Exception($"клетка центра всё ещё покрыта зоной {after} после сноса блока");
	}

	// (д) негатив: снос не-центральной структуры зону не убивает.
	private void TestOrdinaryRemovalKeepsZone()
	{
		var zm = ZoneManager.Instance;
		zm.ClearAll();
		ZoneJobRouter.Instance.Reset();

		if (!WorkZoneManager.Instance.CreateZoneAt(768, 512, 1UL << (int)JobTypeId.TreeChopping, 128, 2,
				out var zone, out string fail))
			throw new Exception($"контрольная зона не создалась: {fail}");
		int zoneId = zone.Id;

		var sm = StructureManager.Instance;
		sm.ClearAll();
		sm.AddStructure(768, 512, StructureRegistry.WorkTable);
		sm.RemoveStructure(768, 512); // WorkTable — не CentralBlock: хук не зовётся

		if (!WorkZoneManager.Instance.TryGetZoneById(zoneId, out _))
			throw new Exception("снос обычной структуры (WorkTable) погасил зону — хук сработал на чужом типе");
	}
}
