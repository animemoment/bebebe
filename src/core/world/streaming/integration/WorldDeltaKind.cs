namespace Game.Core.WorldStreaming.Integration;

/// <summary>Стабильные kind tags общих chunk-delta записей.</summary>
public static class WorldDeltaKind
{
    /// <summary>
    /// Постоянная wall marker: Data содержит ровно один байт со значением 1.
    /// Пустые Data у Wall не используются.
    /// </summary>
    public const byte Wall = 0x77;

    /// <summary>Building record tag; Data остаётся opaque до появления building DTO/schema.</summary>
    public const byte Building = 0x01;

    /// <summary>Mined-stone record tag; Data остаётся opaque до появления mining DTO/schema.</summary>
    public const byte MinedStone = 0x02;

    /// <summary>Cut-tree record tag; Data остаётся opaque до появления forestry DTO/schema.</summary>
    public const byte CutTree = 0x03;

    /// <summary>Stockpile record tag; Data остаётся opaque до появления stockpile DTO/schema.</summary>
    public const byte Stockpile = 0x04;

    /// <summary>Planted-crop record tag (ферма); Data остаётся opaque до появления crop DTO/schema.</summary>
    public const byte Crop = 0x05;

    /// <summary>
    /// §119 P7б (task-17 п.5): куча предметов на земле. НЕ зеркалится из событий
    /// (у GroundItemManager их нет) — метки создаёт save-time refresh
    /// (IslandStateSnapshot.RefreshPayloads) непосредственно перед сейвом, чтобы
    /// рассыпанное зерно/брёвна переживали перезапуск. Не блокирует клетку
    /// (в BlockingKinds не входит).
    /// </summary>
    public const byte GroundItems = 0x06;

    // ---------- §119 P7б: СХЕМА PAYLOAD'ОВ (task-17 п.5) ----------
    // Все payload'ы — little-endian BinaryWriter-поток, версия схемы первым байтом.
    // Читает/пишет IslandPayload (src/core/world/mutation/IslandPayload.cs).
    //
    //  Stockpile (0x04):      [1] ver=1, [2] item, [4] count
    //                         содержимое слота склада; null payload = пустой тайл зоны.
    //  Crop (0x05):           [1] ver=1, [1] stage(1..4), [4] growthTimer, [4] zoneId
    //                         растущая культура; null payload = готовая грядка без культуры.
    //  WallPlan (0x11) /      [1] ver=1, [1] stage(ConstructionStage), [4] deliveredCount,
    //  BuildingPlan (0x12):       { [1] item, [4] count }×delivered, [4] workProgress
    //                         недостроенная стройка (доставка + монтаж).
    //  CutTreePlan (0x15) /   [1] ver=1, [4] fraction (0..1)
    //  MinedStonePlan (0x16): незавершённый прогресс рубки/добычи (CellProgressStore).
    //  GroundItems (0x06):    [1] ver=1, [1] item, [4] count
    // ------------------------------------------------------------------

    // ---------- §29: чертежи (планы) за кромкой острова ----------
    // План = задача для людей мира; готовое = одноимённый готовый вид выше. Персистентность
    // бесплатна: «план без готового» = незакрытая задача, «готовое» = выполнено.

    /// <summary>План стены (0x11).</summary>
    public const byte WallPlan = 0x11;

    /// <summary>План здания/мебели (0x12).</summary>
    public const byte BuildingPlan = 0x12;

    /// <summary>План грядки/зоны фермы (0x13).</summary>
    public const byte CropPlan = 0x13;

    /// <summary>План склада (0x14).</summary>
    public const byte StockpilePlan = 0x14;

    /// <summary>План рубки дерева (0x15).</summary>
    public const byte CutTreePlan = 0x15;

    /// <summary>План добычи камня (0x16).</summary>
    public const byte MinedStonePlan = 0x16;

    /// <summary>План ли это (чертёж, ещё не выполненный).</summary>
    public static bool IsPlan(byte kind)
        => kind == WallPlan || kind == BuildingPlan || kind == CropPlan || kind == StockpilePlan
            || kind == CutTreePlan || kind == MinedStonePlan;

    /// <summary>Готовый вид для плана (WallPlan → Wall). Для не-планов возвращает kind.</summary>
    public static byte DoneFor(byte planKind) => planKind switch
    {
        WallPlan => Wall,
        BuildingPlan => Building,
        CropPlan => Crop,
        StockpilePlan => Stockpile,
        CutTreePlan => CutTree,
        MinedStonePlan => MinedStone,
        _ => planKind
    };

    /// <summary>План для готового вида (Wall → WallPlan). Для не-готовых возвращает kind.</summary>
    public static byte PlanFor(byte doneKind) => doneKind switch
    {
        Wall => WallPlan,
        Building => BuildingPlan,
        Crop => CropPlan,
        Stockpile => StockpilePlan,
        CutTree => CutTreePlan,
        MinedStone => MinedStonePlan,
        _ => doneKind
    };

    /// <summary>«Размещаемые» виды (требуют пригодной поверхности — травы): объекты и их планы.</summary>
    public static bool IsPlacementKind(byte kind)
        => kind == Wall || kind == Building || kind == Stockpile || kind == Crop || IsPlan(kind);

    /// <summary>
    /// Виды, блокирующие клетку (готовые объекты и их чертежи) — ЕДИНЫЙ список для всех гейтов.
    /// Раньше список был продублирован в StreamingWorldManager и WorldGameRules, и в обоих
    /// забыли Crop/CropPlan: на клетку с чертежом фермы вставала стена.
    /// </summary>
    public static readonly byte[] BlockingKinds =
    {
        Wall, Building, Stockpile, Crop,
        WallPlan, BuildingPlan, StockpilePlan, CropPlan
    };

    /// <summary>Постоянные объекты, которые блокируют клетку (готовые и их планы).</summary>
    public static bool IsPermanent(byte kind)
    {
        for (int i = 0; i < BlockingKinds.Length; i++)
        {
            if (BlockingKinds[i] == kind)
                return true;
        }
        return false;
    }
}
