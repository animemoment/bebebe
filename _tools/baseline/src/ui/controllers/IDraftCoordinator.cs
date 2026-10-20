namespace Game.UI;

/// <summary>
/// Координатор черновика постройки: то, что окно стройки (BuildMenuController) вправе
/// требовать от HUD. Реализуется HUDController, потребляется BuildMenuController.
/// Разрывает цикл HUDController ↔ BuildMenuController (ветка (b) цикла #4).
/// </summary>
public interface IDraftCoordinator
{
	/// <summary>Активен ли черновик зоны/стен.</summary>
	bool IsDrafting { get; }

	/// <summary>Начать черчение зоны по её id.</summary>
	void StartZoneDraft(string zoneId);

	/// <summary>Включить/выключить режим дверного проёма.</summary>
	void SetDraftDoorMode(bool enabled);

	/// <summary>Остановить рисование, сохранив черновик.</summary>
	void StopDraftDrawing();

	/// <summary>Зафиксировать черновик (коммит).</summary>
	void ConfirmDraft();

	/// <summary>Отменить черновик без удаления поставленного.</summary>
	void CancelDraft();

	/// <summary>Отменить черновик и снести поставленные чертежи/зону.</summary>
	void DiscardDraft();

	/// <summary>Выбрать материал стен для черновика.</summary>
	void SetDraftMaterial(string materialId);
}