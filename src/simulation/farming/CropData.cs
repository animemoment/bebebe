namespace Game.Simulation;

public struct CropData
{
    public int X;
    public int Y;
    public int ZoneId;
    public int Stage;           // 0: пусто, 1..4: фазы роста
    public float GrowthTimer;   // Таймер текущей фазы (до 60 сек)
    public bool IsHarvestQueued;
    public bool IsPlantingQueued;
}