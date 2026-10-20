using Godot;

namespace Game.UI.Controllers;

public partial class MainMenuController : Control
{
	private Button _playButton;
	private Button _settingsButton;
	private Button _exitButton;

	public override void _Ready()
	{
		// Прогрев пула потоков (.NET ThreadPool warm up) — гарантирует 100% готовность всех ядер без задержки
		int cores = System.Environment.ProcessorCount;
		System.Threading.ThreadPool.SetMinThreads(cores, cores);

		_playButton = GetNodeOrNull<Button>("VBoxContainer/Play") 
		              ?? FindChild("Play", true, false) as Button;
		_settingsButton = GetNodeOrNull<Button>("VBoxContainer/Settings") 
		                  ?? FindChild("Settings", true, false) as Button;
		_exitButton = GetNodeOrNull<Button>("VBoxContainer/Exit") 
		              ?? FindChild("Exit", true, false) as Button;

		if (_playButton != null)
		{
			_playButton.Pressed += OnPlayPressed;
		}

		if (_exitButton != null)
		{
			_exitButton.Pressed += OnExitPressed;
		}

		if (_settingsButton != null)
		{
			_settingsButton.Pressed += OnSettingsPressed;
		}

		var panel = GetNodeOrNull<Control>("Panel") ?? FindChild("Panel", true, false) as Control;
		if (panel != null)
		{
			panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		}

		var label = GetNodeOrNull<Label>("Label") ?? FindChild("Label", true, false) as Label;
		if (label != null)
		{
			label.AnchorLeft = 0.5f;
			label.AnchorRight = 0.5f;
			label.OffsetLeft = -567.5f;
			label.OffsetRight = 567.5f;
		}
	}

	private void OnPlayPressed()
	{
		GD.Print("[MENU] Нажата кнопка 'Играть' -> Переход к выбору спавна на карте мира");
		Game.Simulation.BlueprintManager.Instance.ClearAll();
		Game.Simulation.BuildingManager.Instance.ClearAll();
		Game.Simulation.ConstructionManager.Instance.ClearAll();
		Game.Simulation.StructureManager.Instance.ClearAll();
		Game.Simulation.StockpileManager.Instance.ClearAll();
		Game.Simulation.FarmJobManager.Instance.ClearAll();
		Game.Simulation.TreeJobManager.Instance.ClearAll();
		Game.Simulation.StoneJobManager.Instance.ClearAll();
		Game.Simulation.GroundItemManager.Instance.ClearAll();
		Game.Simulation.CropGrowthManager.Instance.ClearAll();
		Game.Simulation.ZoneManager.Instance.ClearAll();
		Game.Simulation.JobDispatcher.Instance.JobIndex.Clear();
		Game.Simulation.JobDispatcher.Instance.IdleWorkers.Clear();
		Game.Simulation.WorkProgressTracker.Instance.ClearAll();
		Game.Simulation.HierarchicalPathfinder.Instance.Reset();
		Game.Simulation.FlowFieldManager.Instance.ClearCache();
		Game.UI.MapRenderer.ClearTileSetCaches();
		Game.Core.WorldClimate.WorldClimateProvider.ClearCache();
		Game.Simulation.JobPriorityManager.Instance.Reset();
		Game.Core.GameProfiler.Reset();
		Game.Core.SimEvents.Reset();
		Game.Core.ProfilerLogService.Instance.Reset();
		GetTree().ChangeSceneToFile("res://scenes/main/Main.tscn");
	}

	private void OnExitPressed()
	{
		GD.Print("[MENU] Нажата кнопка 'Выход'");
		GetTree().Quit();
	}

	private void OnSettingsPressed()
	{
		GD.Print("[MENU] Нажата кнопка 'Настройки'");
	}
}
