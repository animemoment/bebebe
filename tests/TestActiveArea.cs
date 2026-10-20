using System;
using Godot;
using Game.Core;
using Game.Core.WorldStreaming;

namespace Game.Tests;

/// <summary>
/// §118 фаза 3 (PLAN.md §118.2, гейт: «игра неотличима; --deps MapData
/// показывает shim»): ActiveArea 1024×1024 + MapDataShim + ActiveAreaRegistry.
/// Проверяет:
///  (а) Fill зоны == WorldGenV2.GenerateArea на подвыборке 1000 клеток
///      (Ground/Tree/Stone/TreeVariant/Biome/Variant), вне круга расчистки;
///  (б) стартовая расчистка: круг r=14 в центре зоны — трава без деревьев/
///      камней, кольцо ровно за кругом — parity с сырым GenerateArea;
///  (в) MapDataShim: ToMapData/FillMapData == ActiveArea (500 случайных клеток
///      по всем массивам + Humidity/Fertility/Climate/Seed), негативные
///      исключения (размер, незаполненная зона);
///  (г) реестр: пересечение мировых прямоугольников 1024² → false, касание
///      краями — ок, дубль id → false, Unregister/TryGet/ActiveZone;
///  (д) отрицательные origin: Fill(-512,-768) == GenerateArea (200 клеток),
///      bounds-safe кромка, реестр с отрицательными прямоугольниками.
///
/// WorldOrigin НЕ трогается: WorldGenV2/ActiveArea/реестр работают в
/// АБСОЛЮТНЫХ мировых координатах (§118.1) — скрытого глобального состояния
/// нет. Origin = регион FindHabitableSpawn (лесной, как TestWorldGenV2).
///
/// ЗАПУСК (Lead; раннер по образцу tests/run_worldgen_v2.gd — создать
/// tests/run_activearea_test.gd с содержимым):
///   extends SceneTree
///   func _init():
///       print("[GD] Running TestActiveArea...")
///       var test = load("res://tests/TestActiveArea.cs").new()
///       var ok = test.Run()
///       if ok:
///           print("[GD OK] TestActiveArea passed with Code 0.")
///           quit(0)
///       else:
///           printerr("[GD ERR] TestActiveArea failed!")
///           quit(1)
/// и выполнить: godot --headless --path &lt;project&gt; --script tests/run_activearea_test.gd
/// </summary>
public partial class TestActiveArea : RefCounted
{
    private const ulong Seed = 0x5EED00000BADC0DEul; // известный лесной спавн (как TestWorldGenV2)
    private const uint Ver = 3;

    public bool Run()
    {
        GD.Print("==================================================");
        GD.Print("[TEST] Starting ActiveArea (§118 phase 3: зона + shim + реестр)");
        GD.Print("==================================================");
        try
        {
            // Лесной регион для зоны (1024² = 2×2 региона): зона полностью в
            // абсолютных координатах, WorldOrigin не нужен.
            RegionKey spawn = UnifiedWorldGenerator.FindHabitableSpawn(Seed, Ver);
            long zoneX = spawn.X * WorldRegions.RegionSize;
            long zoneY = spawn.Y * WorldRegions.RegionSize;
            GD.Print($"[TEST] Зона: origin ({zoneX},{zoneY}) — регион спавна ({spawn.X},{spawn.Y})");

            // Зона и эталон генерируются ПО ОДНОМУ разу и переиспользуются
            // тестами (1024² ≈ 1 млн клеток — дорого, ~10 с на генерацию).
            var zone = new ActiveArea(zoneX, zoneY);
            zone.Fill(Seed, Ver);
            WorldGenV2.Area raw = WorldGenV2.GenerateArea(
                Seed, Ver, zoneX, zoneY, ActiveArea.Size, ActiveArea.Size);

            TestFillParity(zone, raw, zoneX, zoneY);
            TestStartClearance(zone, raw, zoneX, zoneY);
            TestMapDataShim(zone, zoneX, zoneY);
            TestRegistry();
            TestNegativeOrigin();

            GD.Print("==================================================");
            GD.Print("[TEST] ALL ACTIVEAREA TESTS PASSED! (Code 0)");
            GD.Print("==================================================");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[TEST FAILED] {ex}");
            return false;
        }
    }

    // (а) Fill зоны == WorldGenV2.GenerateArea на подвыборке 1000 клеток.
    private static void TestFillParity(ActiveArea zone, WorldGenV2.Area raw, long zoneX, long zoneY)
    {
        ReadOnlySpan<GeneratedCell> cells = raw.Cells.Span;
        ReadOnlySpan<bool> trees = raw.TreeOnGrass.Span;
        ReadOnlySpan<bool> stones = raw.StoneOnGrass.Span;
        ReadOnlySpan<byte> treeVariants = raw.TreeVariant.Span;

        int cx = ActiveArea.Size / 2, cy = ActiveArea.Size / 2;
        int r2 = ActiveArea.StartClearRadius * ActiveArea.StartClearRadius;
        int checkedCells = 0, skippedClear = 0;

        // Детерминированный LCG: Size — степень двойки, маска вместо %.
        uint rng = 0x1234ABCDu;
        for (int attempt = 0; attempt < 200000 && checkedCells < 1000; attempt++)
        {
            rng = rng * 1664525u + 1013904223u;
            int lx = (int)(rng & (uint)(ActiveArea.Size - 1));
            rng = rng * 1664525u + 1013904223u;
            int ly = (int)(rng & (uint)(ActiveArea.Size - 1));
            int dx = lx - cx, dy = ly - cy;
            if (dx * dx + dy * dy <= r2)
            {
                skippedClear++; // клетки круга расчистки — отдельный тест (б)
                continue;
            }

            int i = ly * ActiveArea.Size + lx;
            long wx = zoneX + lx, wy = zoneY + ly;

            TileType expectedGround = cells[i].Terrain switch
            {
                BaseTerrainKind.Water => TileType.Water,
                BaseTerrainKind.Mountain => TileType.Mountain,
                _ => TileType.Grass
            };
            if (zone.GetGround(wx, wy) != expectedGround)
                throw new Exception($"(а) Ground @({wx},{wy}): {zone.GetGround(wx, wy)} != {expectedGround}");
            if (zone.GetTree(wx, wy) != trees[i])
                throw new Exception($"(а) Tree @({wx},{wy}): {zone.GetTree(wx, wy)} != {trees[i]}");
            if (zone.HasStone(wx, wy) != stones[i])
                throw new Exception($"(а) Stone @({wx},{wy}): {zone.HasStone(wx, wy)} != {stones[i]}");
            if (zone.GetTreeVariant(wx, wy) != (treeVariants[i] & 3))
                throw new Exception($"(а) TreeVariant @({wx},{wy}): {zone.GetTreeVariant(wx, wy)} != {treeVariants[i] & 3}");
            if (zone.GetBiome(wx, wy) != (BiomeType)cells[i].Biome)
                throw new Exception($"(а) Biome @({wx},{wy}): {zone.GetBiome(wx, wy)} != {(BiomeType)cells[i].Biome}");
            if (zone.GetGroundVariant(wx, wy) != cells[i].Variant)
                throw new Exception($"(а) Variant @({wx},{wy}): {zone.GetGroundVariant(wx, wy)} != {cells[i].Variant}");
            checkedCells++;
        }
        if (checkedCells < 1000)
            throw new Exception($"(а) Набрано только {checkedCells} клеток из 1000 — ошибка сэмплера");

        GD.Print($"[TEST a] Fill parity: {checkedCells} клеток == GenerateArea (пропущено клеток круга: {skippedClear})");
    }

    // (б) Стартовая расчистка: круг r=14 — трава без декора; кольцо за кругом — parity.
    private static void TestStartClearance(ActiveArea zone, WorldGenV2.Area raw, long zoneX, long zoneY)
    {
        ReadOnlySpan<GeneratedCell> cells = raw.Cells.Span;
        ReadOnlySpan<bool> trees = raw.TreeOnGrass.Span;
        ReadOnlySpan<bool> stones = raw.StoneOnGrass.Span;

        int cx = ActiveArea.Size / 2, cy = ActiveArea.Size / 2;
        int r2 = ActiveArea.StartClearRadius * ActiveArea.StartClearRadius;
        int rOuter = ActiveArea.StartClearRadius + 2; // кольцо r+1..r+2 за границей
        int cleared = 0, ringChecked = 0;

        for (int y = cy - rOuter; y <= cy + rOuter; y++)
        {
            for (int x = cx - rOuter; x <= cx + rOuter; x++)
            {
                int dx = x - cx, dy = y - cy;
                long wx = zoneX + x, wy = zoneY + y;
                int i = y * ActiveArea.Size + x;
                if (dx * dx + dy * dy <= r2)
                {
                    cleared++;
                    if (zone.GetGround(wx, wy) != TileType.Grass)
                        throw new Exception($"(б) В круге расчистки @({wx},{wy}) не трава: {zone.GetGround(wx, wy)}");
                    if (zone.GetTree(wx, wy))
                        throw new Exception($"(б) В круге расчистки @({wx},{wy}) дерево");
                    if (zone.HasStone(wx, wy))
                        throw new Exception($"(б) В круге расчистки @({wx},{wy}) камень");
                }
                else
                {
                    // Ровно за границей круга — сырой мир (расчистка не «размазана»).
                    ringChecked++;
                    TileType expected = cells[i].Terrain switch
                    {
                        BaseTerrainKind.Water => TileType.Water,
                        BaseTerrainKind.Mountain => TileType.Mountain,
                        _ => TileType.Grass
                    };
                    if (zone.GetGround(wx, wy) != expected)
                        throw new Exception($"(б) За кругом @({wx},{wy}): {zone.GetGround(wx, wy)} != {expected}");
                    if (zone.GetTree(wx, wy) != trees[i])
                        throw new Exception($"(б) За кругом @({wx},{wy}) дерево != {trees[i]}");
                    if (zone.HasStone(wx, wy) != stones[i])
                        throw new Exception($"(б) За кругом @({wx},{wy}) камень != {stones[i]}");
                }
            }
        }
        // π·14² ≈ 616: самопроверка счётчика (границы «дырявого» круга).
        if (cleared < 590 || cleared > 645)
            throw new Exception($"(б) Подозрительный размер круга расчистки: {cleared} клеток (ожидалось ~616)");

        GD.Print($"[TEST b] Стартовая расчистка: {cleared} клеток круга r=14 — трава без декора; {ringChecked} клеток кольца за кругом — parity");
    }

    // (в) MapDataShim: массивы MapData после моста == ActiveArea (500 клеток).
    private static void TestMapDataShim(ActiveArea zone, long zoneX, long zoneY)
    {
        MapData map = MapDataShim.ToMapData(zone);
        if (map.Width != ActiveArea.Size || map.Height != ActiveArea.Size)
            throw new Exception($"(в) Размер MapData после моста: {map.Width}×{map.Height}");
        CompareMapDataWithZone(map, zone, zoneX, zoneY);

        var target = new MapData(ActiveArea.Size, ActiveArea.Size);
        MapDataShim.FillMapData(target, zone);
        CompareMapDataWithZone(target, zone, zoneX, zoneY);

        if (map.Climate != zone.Climate)
            throw new Exception("(в) Climate после моста не равен Climate зоны");
        if (map.Seed != zone.Seed)
            throw new Exception($"(в) Seed после моста: {map.Seed} != {zone.Seed}");

        // Негативы: несовпадающий размер / незаполненная зона — исключения.
        ExpectException<ArgumentException>(
            () => MapDataShim.FillMapData(new MapData(512, 512), zone), "несовпадающий размер");
        var empty = new ActiveArea(0, 0);
        ExpectException<InvalidOperationException>(
            () => MapDataShim.ToMapData(empty), "незаполненная зона");

        GD.Print("[TEST c] MapDataShim: ToMapData + FillMapData == зона (500 клеток × все поля), Climate/Seed, негативы кидают исключения");
    }

    private static void CompareMapDataWithZone(MapData map, ActiveArea zone, long zoneX, long zoneY)
    {
        uint rng = 0x0BADF00Du;
        for (int n = 0; n < 500; n++)
        {
            rng = rng * 1664525u + 1013904223u;
            int lx = (int)(rng & (uint)(ActiveArea.Size - 1));
            rng = rng * 1664525u + 1013904223u;
            int ly = (int)(rng & (uint)(ActiveArea.Size - 1));
            int i = ly * ActiveArea.Size + lx;
            long wx = zoneX + lx, wy = zoneY + ly;

            if (map.Ground[lx, ly] != zone.Ground[i])
                throw new Exception($"(в) Ground @({lx},{ly}): {map.Ground[lx, ly]} != {zone.Ground[i]}");
            if (map.TreeOnGrass[lx, ly] != zone.TreeOnGrass[i])
                throw new Exception($"(в) TreeOnGrass @({lx},{ly})");
            if (map.TreeVariant[lx, ly] != zone.TreeVariant[i])
                throw new Exception($"(в) TreeVariant @({lx},{ly})");
            if (map.StoneOnGrass[lx, ly] != zone.StoneOnGrass[i])
                throw new Exception($"(в) StoneOnGrass @({lx},{ly})");
            if (map.Biome[lx, ly] != zone.Biome[i])
                throw new Exception($"(в) Biome @({lx},{ly})");
            if (map.Variant[lx, ly] != zone.Variant[i])
                throw new Exception($"(в) Variant @({lx},{ly})");
            // Геттеры-контракты MapData (bounds-safe) — против мировых геттеров зоны.
            if (map.GetBiome(lx, ly) != zone.GetBiome(wx, wy))
                throw new Exception($"(в) GetBiome @({lx},{ly})");
            if (map.GetGroundVariant(lx, ly) != zone.GetGroundVariant(wx, wy))
                throw new Exception($"(в) GetGroundVariant @({lx},{ly})");
            if (map.GetTreeVariant(lx, ly) != zone.GetTreeVariant(wx, wy))
                throw new Exception($"(в) GetTreeVariant @({lx},{ly})");
            if (map.HasStone(lx, ly) != zone.HasStone(wx, wy))
                throw new Exception($"(в) HasStone @({lx},{ly})");
            // Динамические слои: бит-в-бит текущие значения.
            if (map.Humidity.Get(lx, ly) != zone.Humidity.Get(lx, ly))
                throw new Exception($"(в) Humidity @({lx},{ly}): {map.Humidity.Get(lx, ly)} != {zone.Humidity.Get(lx, ly)}");
            if (map.Fertility.Get(lx, ly) != zone.Fertility.Get(lx, ly))
                throw new Exception($"(в) Fertility @({lx},{ly}): {map.Fertility.Get(lx, ly)} != {zone.Fertility.Get(lx, ly)}");
        }
    }

    // (г) Реестр: пересечения/касания/дубли/активная зона.
    private static void TestRegistry()
    {
        var reg = new ActiveAreaRegistry();

        if (!reg.RegisterZone("a", 0, 0))
            throw new Exception("(г) RegisterZone(a, 0, 0) должен пройти");
        if (reg.RegisterZone("b", 1000, 0))
            throw new Exception("(г) Пересекающаяся зона (нахлёст 24 тайла) принята!");
        if (reg.RegisterZone("b2", 500, 500))
            throw new Exception("(г) Пересекающаяся по обеим осям зона принята!");
        if (!reg.RegisterZone("c", 1024, 0))
            throw new Exception("(г) Касание по X (края, не перекрытие) должно проходить");
        if (!reg.RegisterZone("d", 0, 1024))
            throw new Exception("(г) Касание по Y должно проходить");
        if (reg.RegisterZone("a", 50000, 50000))
            throw new Exception("(г) Дубль id принят!");
        if (reg.RegisterZone("", 90000, 0))
            throw new Exception("(г) Пустой id принят!");
        if (reg.Count != 3)
            throw new Exception($"(г) Count после регистраций: {reg.Count} (ожидалось 3: a, c, d)");

        if (!reg.TryGetZone("a", out ActiveArea za) || za == null)
            throw new Exception("(г) TryGetZone(a) должен вернуть зону");
        if (za.ZoneOriginX != 0 || za.ZoneOriginY != 0)
            throw new Exception($"(г) Origin зоны из реестра: ({za.ZoneOriginX},{za.ZoneOriginY})");
        if (za.IsFilled)
            throw new Exception("(г) Зона из реестра должна быть ПУСТОЙ до Fill");
        if (!reg.TryGetZone("a", out ActiveArea za2) || !ReferenceEquals(za, za2))
            throw new Exception("(г) TryGetZone должен кэшировать одну инстанцию на id");
        if (reg.TryGetZone("nope", out _))
            throw new Exception("(г) TryGetZone(незарегистрированный) должен дать false");

        if (!reg.SetActiveZone("a") || !ReferenceEquals(reg.ActiveZone, za))
            throw new Exception("(г) SetActiveZone(a) не назначил зону");
        if (reg.SetActiveZone("nope"))
            throw new Exception("(г) SetActiveZone(незарегистрированный) должен дать false");
        if (!ReferenceEquals(reg.ActiveZone, za))
            throw new Exception("(г) Неудачный SetActiveZone сбросил активную зону");

        if (!reg.UnregisterZone("c"))
            throw new Exception("(г) UnregisterZone(c) должен пройти");
        if (reg.UnregisterZone("c"))
            throw new Exception("(г) Повторный UnregisterZone(c) должен дать false");
        if (reg.TryGetZone("c", out _))
            throw new Exception("(г) TryGetZone(c) после снятия должен дать false");
        if (!reg.UnregisterZone("a"))
            throw new Exception("(г) UnregisterZone(a) должен пройти");
        if (reg.ActiveZone != null)
            throw new Exception("(г) Снятие АКТИВНОЙ зоны не сбросило ActiveZone");
        if (reg.Count != 1)
            throw new Exception($"(г) Count в конце: {reg.Count} (ожидалось 1: d)");

        GD.Print("[TEST d] Реестр: пересечение → false, касание краями → true, дубль id → false, ActiveZone/Unregister — ок");
    }

    // (д) Отрицательные origin: parity + кромка зоны + реестр.
    private static void TestNegativeOrigin()
    {
        long ox = -512, oy = -768;
        var zone = new ActiveArea(ox, oy);
        zone.Fill(Seed, Ver);
        WorldGenV2.Area raw = WorldGenV2.GenerateArea(Seed, Ver, ox, oy, ActiveArea.Size, ActiveArea.Size);
        ReadOnlySpan<GeneratedCell> cells = raw.Cells.Span;
        ReadOnlySpan<bool> trees = raw.TreeOnGrass.Span;
        ReadOnlySpan<bool> stones = raw.StoneOnGrass.Span;

        int cx = ActiveArea.Size / 2, cy = ActiveArea.Size / 2;
        int r2 = ActiveArea.StartClearRadius * ActiveArea.StartClearRadius;
        int checkedCells = 0;
        uint rng = 0xFEEDC0DEu;
        for (int attempt = 0; attempt < 200000 && checkedCells < 200; attempt++)
        {
            rng = rng * 1664525u + 1013904223u;
            int lx = (int)(rng & (uint)(ActiveArea.Size - 1));
            rng = rng * 1664525u + 1013904223u;
            int ly = (int)(rng & (uint)(ActiveArea.Size - 1));
            int dx = lx - cx, dy = ly - cy;
            if (dx * dx + dy * dy <= r2)
                continue;

            int i = ly * ActiveArea.Size + lx;
            long wx = ox + lx, wy = oy + ly;
            TileType expectedGround = cells[i].Terrain switch
            {
                BaseTerrainKind.Water => TileType.Water,
                BaseTerrainKind.Mountain => TileType.Mountain,
                _ => TileType.Grass
            };
            if (zone.GetGround(wx, wy) != expectedGround)
                throw new Exception($"(д) Ground @({wx},{wy}): {zone.GetGround(wx, wy)} != {expectedGround}");
            if (zone.GetTree(wx, wy) != trees[i])
                throw new Exception($"(д) Tree @({wx},{wy})");
            if (zone.HasStone(wx, wy) != stones[i])
                throw new Exception($"(д) Stone @({wx},{wy})");
            checkedCells++;
        }
        if (checkedCells < 200)
            throw new Exception($"(д) Набрано только {checkedCells} клеток из 200");

        // Кромка зоны: углы — внутри, за кромкой — bounds-safe дефолты.
        if (!zone.ContainsWorldCell(ox, oy) || !zone.ContainsWorldCell(ox + 1023, oy + 1023)
            || !zone.ContainsWorldCell(ox, oy + 1023) || !zone.ContainsWorldCell(ox + 1023, oy))
            throw new Exception("(д) Углы зоны не распознаны как ContainsWorldCell");
        if (zone.ContainsWorldCell(ox - 1, oy) || zone.ContainsWorldCell(ox, oy - 1)
            || zone.ContainsWorldCell(ox + 1024, oy) || zone.ContainsWorldCell(ox, oy + 1024))
            throw new Exception("(д) Клетка за кромкой распознана как внутри зоны");
        if (zone.GetGround(ox - 1, oy) != TileType.Water)
            throw new Exception("(д) За кромкой GetGround должен дать Water");
        if (zone.GetTree(ox - 1, oy) || zone.HasStone(ox - 1, oy))
            throw new Exception("(д) За кромкой GetTree/HasStone должны дать false");
        if (zone.GetBiome(ox - 1, oy) != BiomeType.Plains)
            throw new Exception("(д) За кромкой GetBiome должен дать Plains");
        if (zone.GetGroundVariant(ox - 1, oy) != 0 || zone.GetTreeVariant(ox - 1, oy) != 0)
            throw new Exception("(д) За кромкой варианты должны дать 0");

        // Реестр с отрицательными прямоугольниками.
        var reg = new ActiveAreaRegistry();
        if (!reg.RegisterZone("neg", -1024, -1024))
            throw new Exception("(д) Отрицательный origin должен регистрироваться");
        if (reg.RegisterZone("neg2", -1000, -1000))
            throw new Exception("(д) Пересечение отрицательных зон принято!");
        if (!reg.RegisterZone("neg3", -2048, 0))
            throw new Exception("(д) Непересекающаяся отрицательная зона должна проходить");

        GD.Print($"[TEST d] Отрицательный origin ({ox},{oy}): {checkedCells} клеток parity, кромка bounds-safe, реестр — ок");
    }

    private static void ExpectException<T>(Action action, string what) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception($"Ожидалось исключение {typeof(T).Name} ({what}) — не брошено.");
    }
}
