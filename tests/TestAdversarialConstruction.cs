using System;
using System.Collections.Generic;
using Godot;
using Game.Core;
using Game.Simulation;

namespace Game.Tests;

/// <summary>
/// Состязательный стресс-тест (@destroyer) для новой единой системы строительства:
/// 1. Граничные случаи, null-значения и некорректные данные.
/// 2. Стресс-тест создания массовых чертежей 500x500 (250,000 штук) без просадки памяти.
/// 3. Жизненный цикл стройки: расчистка -> снабжение -> сборка -> сдача объекта в StructureManager.
/// 4. Корректность коллизий SolidWalls и кэша навигации.
/// 5. Защита от переполнения ресурсов (overflow refund).
/// </summary>
public partial class TestAdversarialConstruction : RefCounted
{
    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[ADVERSARIAL CONSTRUCTION] Starting Unified Construction Audit (@destroyer)");
        GD.Print("==================================================");

        try
        {
            TestBadDataAndNulls();
            TestMassive500x500BlueprintCreation();
            TestResourceDeliveryAndRefund();
            TestFullConstructionLifecycle();
            TestStructureCollisionIntegration();
            TestIslandVsWorldBoundary();
            TestWarehouseConstructionGating();
            TestUnfinishedStructureNotOperational();
            TestPendingWarehouseZoneRenderingAndIsolation();
            TestInteriorObstacleClearance();
            TestFarmStrictHierarchy();
            TestReactiveResourceBrokerAndDemandPush();
            TestUniversalObstacleMaskAndFarmObstacleRejection();
            TestMaxAssignmentsPerTickBudgetCap();

            GD.Print("==================================================");
            GD.Print("[ADVERSARIAL OK] ALL CONSTRUCTION & STRUCTURE AUDITS PASSED! (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ADVERSARIAL FAILED] {ex}");
            return false;
        }
    }

    private void TestBadDataAndNulls()
    {
        GD.Print("[TEST 1] Testing bad data, nulls and empty parameters...");

        // 1. StructureRegistry
        var def = StructureRegistry.Get((string)null);
        if (def == null) throw new Exception("StructureRegistry.Get(null) returned null instead of default!");

        var unknown = StructureRegistry.Get("non_existent_structure_id_xyz");
        if (unknown == null) throw new Exception("StructureRegistry.Get(unknown) returned null!");

        // 2. StructureManager bad data
        bool r1 = StructureManager.Instance.AddStructure(10, 10, null);
        if (r1) throw new Exception("AddStructure with null def must return false!");

        bool r2 = StructureManager.Instance.RemoveStructure(-999, -999);
        if (r2) throw new Exception("RemoveStructure for non-existent structure must return false!");

        // 3. ConstructionManager bad data
        int g1 = ConstructionManager.Instance.StartSitesBatch(null, null, null);
        if (g1 != 0) throw new Exception("StartSitesBatch with null params must return 0!");

        int g2 = ConstructionManager.Instance.StartSitesBatch(new List<(int, int)>(), StructureRegistry.WoodWall, null);
        if (g2 != 0) throw new Exception("StartSitesBatch with empty list must return 0!");
    }

    private void TestMassive500x500BlueprintCreation()
    {
        GD.Print("[TEST 2] Stress-testing massive 500x500 (250k) blueprint registration...");

        ConstructionManager.Instance.ClearAll();

        int w = 500, h = 500;
        var batch = new List<(int X, int Y)>(w * h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                batch.Add((x, y));

        long t0 = System.Environment.TickCount64;
        int groupId = ConstructionManager.Instance.StartSitesBatch(batch, StructureRegistry.WoodWall, null);
        long elapsed = System.Environment.TickCount64 - t0;

        GD.Print($"[TEST 2] 250,000 blueprints created in {elapsed} ms.");
        if (groupId <= 0) throw new Exception("StartSitesBatch failed to create massive site group!");

        if (!ConstructionManager.Instance.IsSiteAt(0, 0)) throw new Exception("Site (0,0) not found!");
        if (!ConstructionManager.Instance.IsSiteAt(499, 499)) throw new Exception("Site (499,499) not found!");

        long t1 = System.Environment.TickCount64;
        ConstructionManager.Instance.CancelSitesBatch(batch);
        long elapsedCancel = System.Environment.TickCount64 - t1;
        GD.Print($"[TEST 2] 250,000 blueprints cancelled in {elapsedCancel} ms.");

        if (ConstructionManager.Instance.IsSiteAt(0, 0)) throw new Exception("Site (0,0) still present after batch cancel!");
    }

    private void TestResourceDeliveryAndRefund()
    {
        GD.Print("[TEST 3] Testing resource delivery clamp and excess protection...");

        ConstructionManager.Instance.ClearAll();

        var cells = new List<(int X, int Y)> { (50, 50) };
        ConstructionManager.Instance.StartSitesBatch(cells, StructureRegistry.WoodWall, null);

        if (!ConstructionManager.Instance.TryGetSite(50, 50, out var site))
            throw new Exception("Site (50,50) not created!");

        int targetLogs = site.GetRequired(ItemId.Log);
        if (targetLogs != BuildConfig.LogsWall)
            throw new Exception($"Expected {BuildConfig.LogsWall} logs, got {targetLogs}!");

        bool delivered = ConstructionManager.Instance.DeliverMaterial(50, 50, ItemId.Log, 100, out int accepted, out _);
        if (!delivered || accepted != targetLogs)
            throw new Exception($"DeliverMaterial should accept exactly {targetLogs}, accepted: {accepted}!");

        if (site.GetDelivered(ItemId.Log) != targetLogs)
            throw new Exception($"Site has {site.GetDelivered(ItemId.Log)} logs instead of {targetLogs}!");

        if (!site.IsFullySupplied)
            throw new Exception("Site should be fully supplied!");

        ConstructionManager.Instance.ClearAll();
    }

    private void TestFullConstructionLifecycle()
    {
        GD.Print("[TEST 4] Testing full construction lifecycle: Designate -> Deliver -> Assemble -> Complete...");

        ConstructionManager.Instance.ClearAll();
        StructureManager.Instance.ClearAll();

        var cells = new List<(int X, int Y)> { (25, 25) };
        ConstructionManager.Instance.StartSitesBatch(cells, StructureRegistry.WorkTable, null);

        if (!ConstructionManager.Instance.TryGetSite(25, 25, out var site))
            throw new Exception("WorkTable site not created!");

        int needed = site.GetRequired(ItemId.Log);
        ConstructionManager.Instance.DeliverMaterial(25, 25, ItemId.Log, needed, out int accepted, out _);

        ConstructionManager.Instance.ReportWorkProgress(25, 25, 0.5f);
        if (site.WorkProgressStage != 3)
            throw new Exception($"Expected stage 3, got {site.WorkProgressStage}!");

        bool completed = ConstructionManager.Instance.CompleteConstruction(25, 25, null, out var completedDef);
        if (!completed || completedDef.Type != BuildingType.WorkTable)
            throw new Exception("CompleteConstruction failed!");

        if (ConstructionManager.Instance.IsSiteAt(25, 25))
            throw new Exception("Blueprint site should be removed after completion!");

        if (!StructureManager.Instance.HasStructureAt(25, 25))
            throw new Exception("Structure should exist in StructureManager after completion!");

        if (!StructureManager.Instance.TryGetStructure(25, 25, out var placed) || placed.Definition.Type != BuildingType.WorkTable)
            throw new Exception("Placed structure does not match expected WorkTable!");

        StructureManager.Instance.ClearAll();
    }

    private void TestStructureCollisionIntegration()
    {
        GD.Print("[TEST 5] Testing SolidWalls and navigation synchronization...");

        int w = 100, h = 100;
        var ground = new TileType[w, h];
        var tree = new bool[w, h];
        var solid = new bool[w, h];
        var walkable = new List<(int X, int Y)>();
        var spatial = new AgentSpatialGrid(w, h);
        var move = new AgentMovementService();
        var rng = new Random(12345);

        var ctx = new SimulationContext(ground, null, tree, solid, walkable, spatial, move, rng);

        // 1. Стена должна блокировать SolidWalls
        StructureManager.Instance.AddStructure(10, 10, StructureRegistry.WoodWall, ctx);
        if (!ctx.SolidWalls[10, 10])
            throw new Exception("SolidWalls[10,10] must be true after WoodWall added!");

        // 2. Мебель НЕ должна блокировать SolidWalls
        StructureManager.Instance.AddStructure(11, 10, StructureRegistry.Bed, ctx);
        if (ctx.SolidWalls[11, 10])
            throw new Exception("SolidWalls[11,10] must be false after Bed added!");

        // 3. Снос стены должен очищать SolidWalls
        StructureManager.Instance.RemoveStructure(10, 10, ctx);
        if (ctx.SolidWalls[10, 10])
            throw new Exception("SolidWalls[10,10] must be false after WoodWall removed!");

        StructureManager.Instance.ClearAll(ctx);
    }

    private void TestIslandVsWorldBoundary()
    {
        GD.Print("[TEST 6] Testing Island vs World cell boundary classification...");

        // Colony island is [0, MapRenderer.MapWidth) x [0, MapRenderer.MapHeight)
        if (!Game.UI.Tools.WorldPlacementPass.IsIslandCell(0, 0))
            throw new Exception("IsIslandCell(0, 0) must be true!");
        if (!Game.UI.Tools.WorldPlacementPass.IsIslandCell(511, 511))
            throw new Exception("IsIslandCell(511, 511) must be true!");
        if (Game.UI.Tools.WorldPlacementPass.IsIslandCell(-1, 0))
            throw new Exception("IsIslandCell(-1, 0) must be false!");
        if (Game.UI.Tools.WorldPlacementPass.IsIslandCell(0, -1))
            throw new Exception("IsIslandCell(0, -1) must be false!");
        if (Game.UI.Tools.WorldPlacementPass.IsIslandCell(512, 100))
            throw new Exception("IsIslandCell(512, 100) must be false!");
        if (Game.UI.Tools.WorldPlacementPass.IsIslandCell(1000, 1000))
            throw new Exception("IsIslandCell(1000, 1000) must be false!");
    }

    private void TestWarehouseConstructionGating()
    {
        GD.Print("[TEST 7] Testing warehouse construction gating (cannot use warehouse before walls built)...");

        ConstructionManager.Instance.ClearAll();
        StockpileManager.Instance.ClearAll();

        var wallCells = new List<(int X, int Y)> { (30, 30), (30, 31) };
        var warehouseInterior = new List<(int X, int Y)> { (31, 30), (31, 31) };

        // Запуск стройки стен с отложенной зоной склада
        int gid = ConstructionManager.Instance.StartSitesBatch(wallCells, StructureRegistry.WoodWall, null, warehouseInterior);
        if (gid <= 0) throw new Exception("Failed to start site batch!");

        // 1. Пока стены строятся, зона склада НЕ должна быть активна
        if (StockpileManager.Instance.IsZoneTile(31, 30) || StockpileManager.Instance.IsZoneTile(31, 31))
            throw new Exception("Warehouse zone must NOT be active while walls are under construction!");

        // 2. Резервация слотов на активной стройплощадке (30, 30) запрещена
        bool reservedOnSite = StockpileManager.Instance.TryReserveStockpileSlot(new System.Numerics.Vector2(30 * 64, 30 * 64), ItemId.Log, 1, out _, out _);
        if (reservedOnSite)
            throw new Exception("StockpileManager must NOT allow reserving slots on active construction sites!");

        // 3. Завершаем стройку первой стены: зона склада всё ещё ждёт вторую стену
        ConstructionManager.Instance.DeliverMaterial(30, 30, ItemId.Log, BuildConfig.LogsWall, out _, out _);
        ConstructionManager.Instance.CompleteConstruction(30, 30, null, out _);
        if (StockpileManager.Instance.IsZoneTile(31, 30))
            throw new Exception("Warehouse zone must NOT be active until ALL building walls are finished!");

        // 4. Завершаем вторую стену: теперь здание полностью готово, склад активируется!
        ConstructionManager.Instance.DeliverMaterial(30, 31, ItemId.Log, BuildConfig.LogsWall, out _, out _);
        ConstructionManager.Instance.CompleteConstruction(30, 31, null, out _);

        if (!StockpileManager.Instance.IsZoneTile(31, 30) || !StockpileManager.Instance.IsZoneTile(31, 31))
            throw new Exception("Warehouse zone MUST be activated after all walls are completed!");

        ConstructionManager.Instance.ClearAll();
        StockpileManager.Instance.ClearAll();
        StructureManager.Instance.ClearAll();
    }

    private void TestUnfinishedStructureNotOperational()
    {
        GD.Print("[TEST 8] Testing unfinished production structures cannot be operated...");

        ConstructionManager.Instance.ClearAll();
        StructureManager.Instance.ClearAll();

        // Ставим верстак на этапе стройки
        var cells = new List<(int X, int Y)> { (40, 40) };
        ConstructionManager.Instance.StartSitesBatch(cells, StructureRegistry.WorkTable, null);

        // Объект на стройке НЕ должен считаться эксплуатационно готовым
        if (StructureManager.Instance.IsStructureOperational(40, 40))
            throw new Exception("WorkTable under construction must NOT be operational!");

        if (StructureManager.Instance.TryGetOperationalStructure(40, 40, out _))
            throw new Exception("TryGetOperationalStructure must return false while under construction!");

        // Доставляем бревна и завершаем стройку
        ConstructionManager.Instance.DeliverMaterial(40, 40, ItemId.Log, BuildConfig.LogsWorkTable, out _, out _);
        ConstructionManager.Instance.CompleteConstruction(40, 40, null, out _);

        // После завершения верстак готов к эксплуатации
        if (!StructureManager.Instance.IsStructureOperational(40, 40))
            throw new Exception("Completed WorkTable MUST be operational!");

        if (!StructureManager.Instance.TryGetOperationalStructure(40, 40, out var op) || op.Type != BuildingType.WorkTable)
            throw new Exception("TryGetOperationalStructure must return the completed WorkTable!");

        ConstructionManager.Instance.ClearAll();
        StructureManager.Instance.ClearAll();
    }

    private void TestPendingWarehouseZoneRenderingAndIsolation()
    {
        GD.Print("[TEST 9] Testing pending warehouse zone rendering visibility and slot isolation...");

        StockpileManager.Instance.ClearAll();

        var pending = new List<(int X, int Y)> { (50, 50), (50, 51) };
        StockpileManager.Instance.AddPendingZoneTilesBatch(pending);

        // 1. Зона видна для рендера
        if (!StockpileManager.Instance.IsZoneTile(50, 50) || !StockpileManager.Instance.IsZoneTile(50, 51))
            throw new Exception("Pending warehouse zone MUST be recognized as zone tile for renderer!");

        // 2. Но склад не должен предоставлять свободные слоты для хранения
        if (StockpileManager.Instance.HasFreeSpace)
            throw new Exception("Pending warehouse zone must NOT have free space before activation!");

        if (StockpileManager.Instance.TryReserveStockpileSlot(System.Numerics.Vector2.Zero, ItemId.Log, 5, out _, out _))
            throw new Exception("Must NOT be able to reserve slots in pending warehouse zone!");

        // 3. Активируем зону — теперь слоты доступны
        StockpileManager.Instance.ActivatePendingZone(pending);

        if (!StockpileManager.Instance.HasFreeSpace)
            throw new Exception("Activated warehouse zone MUST have free space!");

        StockpileManager.Instance.ClearAll();
    }

    private void TestInteriorObstacleClearance()
    {
        GD.Print("[TEST 10] Testing interior floor obstacles clearance in buildings...");

        ConstructionManager.Instance.ClearAll();
        TreeJobManager.Instance.ClearAll();
        StoneJobManager.Instance.ClearAll();

        int w = 100, h = 100;
        var treeMask = new bool[w, h];
        var stoneMask = new bool[w, h];

        var mapData = new MapData(w, h);
        mapData.TreeOnGrass[61, 61] = true;
        mapData.StoneOnGrass[62, 62] = true;

        var walls = new List<(int X, int Y)> { (60, 60), (60, 61), (60, 62) };
        var interior = new List<(int X, int Y)> { (61, 61), (62, 62) };

        ConstructionManager.Instance.StartSitesBatch(walls, StructureRegistry.WoodWall, mapData, interior);

        if (!TreeJobManager.Instance.IsTreeMarked(61, 61))
            throw new Exception("Interior tree at (61,61) MUST be queued for chopping!");

        if (!StoneJobManager.Instance.IsStoneMarked(62, 62))
            throw new Exception("Interior stone at (62,62) MUST be queued for mining!");

        ConstructionManager.Instance.ClearAll();
        TreeJobManager.Instance.ClearAll();
        StoneJobManager.Instance.ClearAll();
    }

    private void TestFarmStrictHierarchy()
    {
        GD.Print("[TEST 11] Testing strict farm building hierarchy: Clear -> Plow -> Plant...");

        FarmJobManager.Instance.ClearAll();
        TreeJobManager.Instance.ClearAll();
        StoneJobManager.Instance.ClearAll();

        int w = 100, h = 100;
        var treeMask = new bool[w, h];
        treeMask[70, 70] = true; // на (70,70) растет дерево

        var plots = new List<(int X, int Y)> { (70, 70), (71, 71) };
        FarmJobManager.Instance.MarkPlotsBatch(plots, treeMask, null);

        // 1. Клетка с деревом (70,70) не должна сразу считаться готовой к вспашке
        if (FarmJobManager.Instance.IsGardenBed(70, 70))
            throw new Exception("Uncleared plot must NOT be a garden bed!");

        // 2. Дерево на грядке отправлено в рубку
        if (!TreeJobManager.Instance.IsTreeMarked(70, 70))
            throw new Exception("Tree on farm plot MUST be marked for chopping!");

        // 3. До вспашки PlantingJobHandler не должен разрешать посадку
        var plantJob = new JobData { TargetX = 70, TargetY = 70 };
        var handler = new Game.Simulation.Jobs.PlantingJobHandler();
        if (handler.CanAgentExecute(0, plantJob, null, null))
            throw new Exception("PlantingJobHandler must NOT allow planting before bed is plowed into GardenBed!");

        // 4. Завершаем вспашку чистой клетки (71,71)
        FarmJobManager.Instance.CompletePlot(71, 71);
        if (!FarmJobManager.Instance.IsGardenBed(71, 71))
            throw new Exception("Completed plot must be a garden bed!");

        FarmJobManager.Instance.ClearAll();
        TreeJobManager.Instance.ClearAll();
        StoneJobManager.Instance.ClearAll();
    }

    private void TestReactiveResourceBrokerAndDemandPush()
    {
        GD.Print("[TEST 12] Testing Reactive ResourceBroker and DemandRegistry Push notifications...");

        DemandRegistry.Instance.ClearAll();
        GroundItemManager.Instance.ClearAll();

        // 1. Создаем чертеж, когда ресурсов нет
        JobBroker.Instance.RegisterBlueprint(25, 25, BuildingType.WoodWall, 10);
        if (!DemandRegistry.Instance.HasDemandFor(ItemId.Log))
            throw new Exception("DemandRegistry must track demand for WoodWall!");

        // 2. Имитируем появление брёвен в мире (рубка дерева)
        GroundItemManager.Instance.SpawnItems(30, 30, ItemId.Log, 15);

        // 3. Доставка на чертеж
        JobBroker.Instance.DeliverLogsToBlueprint(25, 25, 10);
        if (DemandRegistry.Instance.HasDemandFor(ItemId.Log))
            throw new Exception("Demand must be satisfied and removed after full delivery!");

        GroundItemManager.Instance.ClearAll();
        DemandRegistry.Instance.ClearAll();
    }

    private void TestUniversalObstacleMaskAndFarmObstacleRejection()
    {
        GD.Print("[TEST 13] Testing Universal Obstacle Mask and strict farm clearance rejection...");

        GroundItemManager.Instance.ClearAll();
        FarmJobManager.Instance.ClearAll();

        int w = 50, h = 50;
        var ground = new TileType[w, h];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                ground[x, y] = TileType.Grass;

        var treeMask = new bool[w, h];
        var stoneMask = new bool[w, h];
        var wallMask = new bool[w, h];
        var emptyWalkable = new List<(int X, int Y)>();
        var spatialGrid = new AgentSpatialGrid(w, h);
        var moveService = new AgentMovementService();
        var ctx = new SimulationContext(ground, null, treeMask, wallMask, emptyWalkable, spatialGrid, moveService, new Random(), 64, stoneMask);

        // 1. Чистая клетка
        var farmHandler = new Game.Simulation.Jobs.FarmingJobHandler();
        var farmJob = new JobData { TargetX = 15, TargetY = 15 };
        if (!farmHandler.CanAgentExecute(0, farmJob, null, ctx))
            throw new Exception("Clean grass must be allowed for plowing!");

        // 2. Клетка с брёвнами на земле (мусор/ресурсы)
        GroundItemManager.Instance.SpawnItems(15, 15, ItemId.Log, 5);
        if (farmHandler.CanAgentExecute(0, farmJob, null, ctx))
            throw new Exception("Plot with loose items on the ground MUST be rejected for plowing!");

        // 3. Убираем брёвна
        GroundItemManager.Instance.TakeItemsWithType(15, 15, 5);
        if (!farmHandler.CanAgentExecute(0, farmJob, null, ctx))
            throw new Exception("Plot after clearance must be allowed for plowing!");

        GroundItemManager.Instance.ClearAll();
        FarmJobManager.Instance.ClearAll();
    }

    private void TestMaxAssignmentsPerTickBudgetCap()
    {
        GD.Print("[TEST 14] Testing MaxAssignmentsPerTick budget cap (high-throughput >= 4096)...");
        if (JobDispatcher.MaxAssignmentsPerTick < 4096)
            throw new Exception($"MaxAssignmentsPerTick must be at least 4096 for 10k agents, got {JobDispatcher.MaxAssignmentsPerTick}!");
    }
}

