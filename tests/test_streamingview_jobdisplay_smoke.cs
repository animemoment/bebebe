using System;
using Godot;

namespace Game.Tests;

// Имя класса == basename файла (иначе Godot-генератор не эмитит ScriptPath
// и CSharpScript.new() из GDScript-раннера падает).

/// <summary>
/// §119/P6 (@developer dev-streaming-view, task-12): headless-смоук JobDisplay.
/// Приёмка: сцена инстанцируется; слайдер Tiles = радиус 64..512 step 1;
/// слайдер Worker = 0..999 step 1; Prioritet на месте; тумблер EnabledToggle
/// (toggle_mode) и кнопка RemoveZone находятся FindChild-ом (как их ищет
/// WorkZoneController); esc/ButtonForFarm на месте (регресс ферма→JobDisplay).
/// </summary>
public partial class test_streamingview_jobdisplay_smoke : RefCounted
{
	public bool Run()
	{
		GD.Print("==================================================");
		GD.Print("[TEST] JobDisplay P6 smoke (§119)");
		GD.Print("==================================================");
		try
		{
			const string path = "res://scenes/ui/JobDisplay.tscn";
			if (!ResourceLoader.Exists(path))
				throw new Exception($"Scene not found: {path}");
			var packed = ResourceLoader.Load<PackedScene>(path)
				?? throw new Exception($"Failed to load PackedScene: {path}");
			var root = packed.Instantiate<Control>()
				?? throw new Exception("Instantiate<Control>() returned null");
			try
			{
				CheckSlider(root, "Tiles", 64, 512, 1);
				CheckSlider(root, "Worker", 0, 999, 1);
				var priorityRow = root.FindChild("Prioritet", true, false) as Control
					?? throw new Exception("Prioritet row missing");
				var prioritySlider = priorityRow.FindChild("HSlider", true, false) as HSlider
					?? throw new Exception("Prioritet HSlider missing");
				if (Math.Abs(prioritySlider.MinValue - 1) > 0.001
					|| Math.Abs(prioritySlider.MaxValue - 10) > 0.001
					|| Math.Abs(prioritySlider.Step - 1) > 0.001)
					throw new Exception(
						$"Prioritet slider range mismatch: [{prioritySlider.MinValue}..{prioritySlider.MaxValue}] step {prioritySlider.Step}, expected [1..10] step 1");

				var toggle = root.FindChild("EnabledToggle", true, false) as BaseButton
					?? throw new Exception("EnabledToggle not findable by FindChild");
				if (!toggle.ToggleMode)
					throw new Exception("EnabledToggle must be a toggle (toggle_mode = true)");
				if (!toggle.ButtonPressed)
					throw new Exception("EnabledToggle must start pressed (вкл)");

				var remove = root.FindChild("RemoveZone", true, false) as BaseButton
					?? throw new Exception("RemoveZone button not findable by FindChild");

				var esc = root.FindChild("esc", true, false) as BaseButton
					?? throw new Exception("esc close button missing (регресс закрытия окна)");
				var farmBack = root.FindChild("ButtonForFarm", true, false) as BaseButton
					?? throw new Exception("ButtonForFarm missing (регресс ферма→JobDisplay)");
				if (farmBack.Visible)
					throw new Exception("ButtonForFarm must start hidden");

				var info = root.FindChild("Information", true, false) as Control
					?? throw new Exception("Information node missing");
				var infoText = info.FindChild("TextForInformation", true, false) as Control
					?? throw new Exception("TextForInformation missing");

				GD.Print($"[SMOKE] OK: prio [{prioritySlider.MinValue}..{prioritySlider.MaxValue}]; " +
					$"toggle pressed={toggle.ButtonPressed}; remove='{(remove as Button)?.Text}'; esc/farm-back/info ok; " +
					$"infoText='{infoText.GetType().Name}'.");
				GD.Print("==================================================");
				GD.Print("[TEST] JOBDISPLAY P6 SMOKE PASSED! (Code 0)");
				GD.Print("==================================================");
				return true;
			}
			finally
			{
				root.Free();
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[TEST FAILED] {ex}");
			return false;
		}
	}

	private static void CheckSlider(Control root, string rowName, double min, double max, double step)
	{
		var row = root.FindChild(rowName, true, false) as Control
			?? throw new Exception($"{rowName} row missing");
		var slider = row.FindChild("HSlider", true, false) as HSlider
			?? throw new Exception($"{rowName} HSlider missing");
		if (Math.Abs(slider.MinValue - min) > 0.001
			|| Math.Abs(slider.MaxValue - max) > 0.001
			|| Math.Abs(slider.Step - step) > 0.001)
			throw new Exception(
				$"{rowName} slider range mismatch: got [{slider.MinValue}..{slider.MaxValue}] step {slider.Step}, expected [{min}..{max}] step {step}");
		GD.Print($"[SMOKE] {rowName}: [{slider.MinValue}..{slider.MaxValue}] step={slider.Step} — OK");
	}
}
