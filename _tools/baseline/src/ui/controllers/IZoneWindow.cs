namespace Game.UI;

/// <summary>
/// Узкий контракт окна зоны (ферма / склад): взаимное закрытие окон без прямой
/// зависимости FarmController ↔ WorkZoneController. Реализуется обоими контроллерами.
/// Разрывает цикл #3 (DIP вместо обращения к чужому статическому Instance).
/// </summary>
public interface IZoneWindow
{
	/// <summary>Окно сейчас видимо игроку.</summary>
	bool IsOpen { get; }

	/// <summary>Закрыть окно (идемпотентно).</summary>
	void Close();
}

/// <summary>
/// Хост окна JobDisplay для Farm-зоны (§20.6). Реализуется WorkZoneController,
/// потребляется FarmController. Отдельный интерфейс — это не «окно», а действие.
/// </summary>
public interface IFarmJobDisplayHost
{
	/// <summary>Открыть JobDisplay для Farm-зоны по её id.</summary>
	void ShowForFarmZone(int farmZoneId);
}