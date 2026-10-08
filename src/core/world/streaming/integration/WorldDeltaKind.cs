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

    /// <summary>План ли это (чертёж, ещё не выполненный).</summary>
    public static bool IsPlan(byte kind)
        => kind == WallPlan || kind == BuildingPlan || kind == CropPlan || kind == StockpilePlan;

    /// <summary>Готовый вид для плана (WallPlan → Wall). Для не-планов возвращает kind.</summary>
    public static byte DoneFor(byte planKind) => planKind switch
    {
        WallPlan => Wall,
        BuildingPlan => Building,
        CropPlan => Crop,
        StockpilePlan => Stockpile,
        _ => planKind
    };

    /// <summary>План для готового вида (Wall → WallPlan). Для не-готовых возвращает kind.</summary>
    public static byte PlanFor(byte doneKind) => doneKind switch
    {
        Wall => WallPlan,
        Building => BuildingPlan,
        Crop => CropPlan,
        Stockpile => StockpilePlan,
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
