namespace Game.UI;

/// <summary>
/// Реестр окон зон. Листовой файл без зависимостей: контроллеры видят только интерфейсы
/// и не ссылаются друг на друга напрямую. Регистрация — в _Ready, снятие — в _ExitTree
/// (иначе статика удержит освобождённый Godot-объект).
/// </summary>
public static class ZoneWindowRegistry
{
	/// <summary>Окно Farm-зоны (регистрирует FarmController).</summary>
	public static IZoneWindow FarmWindow { get; set; }

	/// <summary>Окно Work-зоны (регистрирует WorkZoneController).</summary>
	public static IZoneWindow WorkWindow { get; set; }

	/// <summary>Хост JobDisplay для Farm-зоны (регистрирует WorkZoneController).</summary>
	public static IFarmJobDisplayHost FarmJobDisplay { get; set; }
}