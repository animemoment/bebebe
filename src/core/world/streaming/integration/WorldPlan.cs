namespace Game.Core.WorldStreaming.Integration;

/// <summary>
/// Чертёж (§29): план-метка в клетке мира, по которой ещё нет готового. Из чертежей
/// строится очередь задач для людей мира; готовое пишется отдельным видом (<see cref="DoneKind"/>).
/// </summary>
public readonly record struct WorldPlan(byte PlanKind, long CellX, long CellY)
{
    /// <summary>Готовый вид этого чертежа (WallPlan → Wall, CropPlan → Crop и т.д.).</summary>
    public byte DoneKind => WorldDeltaKind.DoneFor(PlanKind);

    /// <summary>Текстовый вид для логов («чертёж стены»).</summary>
    public string Title => PlanKind switch
    {
        WorldDeltaKind.WallPlan => "стена",
        WorldDeltaKind.BuildingPlan => "стройка",
        WorldDeltaKind.CropPlan => "посев",
        WorldDeltaKind.StockpilePlan => "склад",
        _ => "задача"
    };
}
