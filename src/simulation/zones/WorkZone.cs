using System;
using System.Collections.Generic;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

// ============================================================================
// §119 P0: модель Work-зоны — ЦЕНТР + РАДИУС, без материализации клеток.
// Раньше зона хранила HashSet всех клеток диска (при r=512 это 823 548 клеток
// = гигабайты на батч зон) — заменено формулой Contains и вычисляемым
// CellCount (πr²). Очередь работ зоны, партиция пересечений и репуш — P1/P2.
// ============================================================================

/// <summary>Публичное (UI/фасад) представление Work-зоны — снапшот полей Zone.</summary>
public sealed class WorkZone
{
    /// <summary>§119: радиус зоны, клеток (кламп обязателен).</summary>
    public const int MinRadiusTiles = 64;
    public const int MaxRadiusTiles = 512;
    /// <summary>§119: заявка на число рабочих без потолка (слайдер 0..999).</summary>
    public const int MaxDesiredWorkers = 999;

    public int Id { get; }
    public string Name { get; set; }
    public int CenterX { get; }
    public int CenterY { get; }

    private int _radiusTiles;
    public int RadiusTiles
    {
        get => Volatile.Read(ref _radiusTiles);
        set => Volatile.Write(ref _radiusTiles, Math.Clamp(value, MinRadiusTiles, MaxRadiusTiles));
    }

    /// <summary>
    /// ЛЕГАСИ (UI WorkZoneController читает до P2): раньше — целевая площадь,
    /// теперь дублирует радиус; слайдер Tiles в P2 станет радиусом 64..512.
    /// </summary>
    public int TilesTarget { get; set; }

    private int _desiredWorkers;
    /// <summary>§119: ЗАЯВКА на число рабочих, без потолка (0..999; 0 = зона не просит).</summary>
    public int DesiredWorkers
    {
        get => Volatile.Read(ref _desiredWorkers);
        set => Volatile.Write(ref _desiredWorkers, Math.Clamp(value, 0, MaxDesiredWorkers));
    }

    /// <summary>ЛЕГАСИ-алиас DesiredWorkers (UI читает до P2). Семантика: заявка, потолка нет.</summary>
    public int MaxWorkers
    {
        get => DesiredWorkers;
        set => DesiredWorkers = value;
    }

    public ulong JobMask { get; set; }
    public int? PriorityOverride { get; set; }

    private int _enabled;
    /// <summary>§119: зона включена (выключенная зона не диспатчит — логика в P1).</summary>
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    private int _anchorBlockId = -1;
    /// <summary>§119 P5: id центрального блока-якоря (-1 = якоря нет). Снос якоря = снос зоны.</summary>
    public int AnchorBlockId
    {
        get => Volatile.Read(ref _anchorBlockId);
        set => Volatile.Write(ref _anchorBlockId, value);
    }

    public int AssignedCount;

    /// <summary>
    /// Индексы чанков (16×16), пересекающих диск — вычисляются аналитически,
    /// БЕЗ материализации клеток. Маленький список (≤ ChunkDim² int'ов);
    /// оставлен, т.к. UI (WorkZoneController) читает его до P2.
    /// </summary>
    public List<int> ChunkIndices { get; set; }

    public WorkZone(int id, string name, int centerX, int centerY, int radiusTiles,
        int desiredWorkers, ulong jobMask, List<int> chunkIndices = null)
    {
        Id = id;
        Name = name;
        CenterX = centerX;
        CenterY = centerY;
        RadiusTiles = radiusTiles;          // сеттер клампит 64..512
        TilesTarget = RadiusTiles;          // легаси-поле: до P2 хранит радиус
        DesiredWorkers = desiredWorkers;    // сеттер клампит 0..999
        JobMask = jobMask;
        PriorityOverride = null;
        AssignedCount = 0;
        Enabled = true;
        AnchorBlockId = -1;
        ChunkIndices = chunkIndices;
    }

    /// <summary>§119: площадь зоны по формуле πr² (вычисляемая, БЕЗ материализации клеток).</summary>
    public long CellCount
    {
        get
        {
            long r = RadiusTiles;
            return (long)Math.Round(Math.PI * r * r);
        }
    }

    /// <summary>§119: попадание клетки в диск (O(1), без хэшсета): dx²+dy² ≤ r².</summary>
    public bool Contains(int x, int y)
    {
        long dx = x - CenterX;
        long dy = y - CenterY;
        long r = RadiusTiles;
        return dx * dx + dy * dy <= r * r;
    }

    public bool Matches(JobTypeId t) => (JobMask & (1UL << (int)t)) != 0;

    public void RegisterAssigned() => Interlocked.Increment(ref AssignedCount);

    public void ResetAssigned() => Volatile.Write(ref AssignedCount, 0);
}
