using System;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Тип зоны в унифицированном ZoneManager.
/// Farm — грядка (фермерство, AutoPlant); Work — универсальная рабочая зона-бригадир.
/// </summary>
public enum ZoneKind : byte
{
    Farm = 0,
    Work = 1
}

/// <summary>
/// Неизменяемая форма зоны (immutable-swap). Собирается ЦЕЛИКОМ и публикуется
/// одной volatile-заменой ссылки (Zone.Shape). Читатели (UI-рендер вне локов,
/// SetAutoPlant, бригадир-диспатч из sim-потока) берут ссылку один раз и
/// работают с согласованным снапшотом: никаких «Collection was modified» и
/// разорванных состояний (новая геометрия + старые чанки) при параллельной
/// пересборке тайлов. Плотные массивы вместо мутабельных коллекций — плюс к
/// кэш-дружелюбности горячего цикла диспатча.
/// </summary>
public sealed class ZoneShape
{
    public readonly int RadiusTiles;
    public readonly int TilesTarget;
    public readonly int MaxWorkers;
    public readonly ulong JobMask;
    public readonly int? PriorityOverride;
    /// <summary>Тайлы зоны как плотный массив (для итерации без аллокаторов).</summary>
    public readonly (int X, int Y)[] TileArray;
    /// <summary>О(1) проверка принадлежности тайла зоне в диспатче.</summary>
    public readonly HashSet<(int X, int Y)> TileSet;
    /// <summary>Индексы чанков диспатча (16×16 тайлов), упакованные cy*ChunkDim+cx.</summary>
    public readonly int[] ChunkIndices;

    public ZoneShape(int radiusTiles, int tilesTarget, int maxWorkers, ulong jobMask,
        int? priorityOverride, HashSet<(int X, int Y)> tiles, int[] chunkIndices)
    {
        RadiusTiles = radiusTiles;
        TilesTarget = tilesTarget;
        MaxWorkers = maxWorkers;
        JobMask = jobMask;
        PriorityOverride = priorityOverride;
        if (tiles != null && tiles.Count > 0)
        {
            TileSet = tiles;
            TileArray = new (int X, int Y)[tiles.Count];
            int i = 0;
            foreach (var t in tiles)
                TileArray[i++] = t;
        }
        else
        {
            TileSet = new HashSet<(int X, int Y)>();
            TileArray = Array.Empty<(int X, int Y)>();
        }
        ChunkIndices = chunkIndices ?? Array.Empty<int>();
    }

    public bool ContainsTile(int x, int y) => TileSet.Contains((x, y));

    /// <summary>
    /// Инвариант: JobTypeId строго &lt; 64 (см. JobTypeId). Без маскировки сдвига:
    /// 1UL &lt;&lt; t при t >= 64 в C# даёт (t &amp; 63) — молча неверный матч.
    /// </summary>
    public bool JobMaskBitSet(int typeId)
    {
        System.Diagnostics.Debug.Assert(typeId >= 0 && typeId < 64);
        return typeId >= 0 && typeId < 64 && (JobMask & (1UL << typeId)) != 0;
    }
}

/// <summary>
/// Единая зона: одно множество тайлов, один владелец.
/// Farm-специфика (AutoPlant/семена) и Work-специфика (маска/лимиты/диспатч)
/// живут в одной сущности, различаются по Kind.
/// </summary>
public sealed class Zone
{
    public int Id { get; }
    public string Name { get; set; }
    public ZoneKind Kind { get; }

    // Геометрия:_tiles — единственный источник, заменяется ссылкой под локом менеджера.
    public HashSet<(int X, int Y)> Tiles { get; internal set; }

    // --- Farm-поля (актуальны при Kind == Farm) ---
    public ItemId RequiredSeedItem { get; set; } = ItemId.Grain;
    public bool AutoPlantEnabled { get; set; } = false;
    public int PlantedCount { get; set; }

    // --- Work-поля (актуальны при Kind == Work) ---
    public int CenterX { get; }
    public int CenterY { get; }
    public int RadiusTiles { get; internal set; }
    public int TilesTarget { get; internal set; }
    public int MaxWorkers { get; internal set; }
    public ulong JobMask { get; internal set; }
    public int? PriorityOverride { get; internal set; }
    public int AssignedCount;
    public List<int> ChunkIndices { get; internal set; }

    /// <summary>
    /// Immutable-swap снапшот геометрии Work-зоны (см. ZoneShape). Пишется ТОЛЬКО
    /// через Volatile.Write в ZoneManager (CreateWorkZoneAt/SetWork*), читается
    /// диспатчем одним Volatile.Read — согласованный снимок без локов.
    /// Для Farm-зон может быть null (геометрия не меняется на лету).
    /// </summary>
    public ZoneShape Shape;

    public int TotalTiles => Tiles.Count;

    internal Zone(int id, string name, ZoneKind kind, HashSet<(int X, int Y)> tiles)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Tiles = tiles;
    }

    internal Zone(int id, string name, int centerX, int centerY, int radiusTiles,
        int tilesTarget, int maxWorkers, ulong jobMask,
        HashSet<(int, int)> tiles, List<int> chunkIndices)
        : this(id, name, ZoneKind.Work, new HashSet<(int X, int Y)>())
    {
        CenterX = centerX;
        CenterY = centerY;
        RadiusTiles = radiusTiles;
        TilesTarget = tilesTarget;
        MaxWorkers = maxWorkers;
        JobMask = jobMask;
        PriorityOverride = null;
        AssignedCount = 0;
        // (int,int) и (int X, int Y) — один и тот же ValueTuple, прямое присваивание.
        Tiles = tiles;
        ChunkIndices = chunkIndices;
    }

    public bool Matches(JobTypeId t) => JobMaskBitSet((int)t);

    /// <summary>
    /// Инвариант: JobTypeId строго &lt; 64. Без маскировки сдвига: 1UL &lt;&lt; t при
    /// t >= 64 в C# даёт (t &amp; 63) — молча неверный матч (скрытая порча JobMask).
    /// </summary>
    public bool JobMaskBitSet(int typeId)
    {
        System.Diagnostics.Debug.Assert(typeId >= 0 && typeId < 64);
        return typeId >= 0 && typeId < 64 && (JobMask & (1UL << typeId)) != 0;
    }

    /// <summary>
    /// Протокол AssignedCount: RegisterAssigned вызывается воркерами/диспатчем во
    /// время параллельной фазы; ResetAssigned — СТРОГО в фазе до параллельной работы
    /// (между проходами DispatchZones, после join прошлой фазы). Нарушение порядка =
    /// тихая потеря назначений под нагрузкой.
    /// </summary>
    public void RegisterAssigned() => System.Threading.Interlocked.Increment(ref AssignedCount);

    /// <summary>См. комментарий к RegisterAssigned: сброс только вне параллельной фазы.</summary>
    public void ResetAssigned() => System.Threading.Volatile.Write(ref AssignedCount, 0);

    /// <summary>Валидация: отрицательный target → NaN радиус → маскируемая ошибка вызывающего кода.</summary>
    public static int CalcRadius(int tilesTarget)
    {
        if (tilesTarget <= 0)
            throw new ArgumentOutOfRangeException(nameof(tilesTarget), tilesTarget, "tilesTarget должен быть > 0");
        return (int)Math.Round(Math.Sqrt(tilesTarget / Math.PI));
    }

    public static HashSet<(int, int)> BuildDisc(int cx, int cy, int r)
    {
        if (r < 0)
            throw new ArgumentOutOfRangeException(nameof(r), r, "Радиус диска не может быть отрицательным");
        var set = new HashSet<(int, int)>();
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (dx * dx + dy * dy <= r * r)
                    set.Add((cx + dx, cy + dy));
        return set;
    }
}
