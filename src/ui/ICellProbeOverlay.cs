namespace Game.UI;

/// <summary>
/// Показ значений клетки под курсором (влажность / плодородие).
/// Узкий контракт для SelectTool, чтобы инструмент не зависел от HUDController.
/// Реализуется HUDController. Разрывает цикл
/// SelectTool → HUDController → PlayerInteractionManager → SelectTool.
/// </summary>
public interface ICellProbeOverlay
{
	/// <summary>Показать % влажности клетки.</summary>
	void ShowHumidityAt(int x, int y);

	/// <summary>Показать плодородие клетки.</summary>
	void ShowFertilityAt(int x, int y);
}