namespace Game.Core;

public enum BuildingType : byte
{
    WoodWall = 0,
    WorkTable = 1,
    /// <summary>Кровать (bed.png 64x64).</summary>
    Bed = 2,
    /// <summary>Скамья (bench.png 64x64).</summary>
    Bench = 3,
    /// <summary>Тумбочка (nightstand.png 64x64).</summary>
    Nightstand = 4,
    /// <summary>Центральный блок (central.png 64x64) — якорь центра рабочей зоны (§119).</summary>
    CentralBlock = 5
}