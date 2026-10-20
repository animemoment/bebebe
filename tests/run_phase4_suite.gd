@tool
extends SceneTree

## Headless-драйвер для C#-фазы 4 (по образцу run_test.gd / run_world_map_audit.gd).
## Покрывает поверхность моста A (PLAN.md §112.2): GroundItemManager,
## ConstructionManager, HUDController, цикл ResetColonyState.
## Run: godot --headless --path . --script tests/run_phase4_suite.gd

func _init() -> void:
	print("[GD] Running Phase4StressTestRunner (RunAllTests)...")
	var runner_script = load("res://tests/Phase4StressTestRunner.cs")
	if runner_script == null:
		printerr("[GD ERR] Failed to load Phase4StressTestRunner.cs")
		quit(1)
		return

	var runner = runner_script.new()
	if runner == null:
		printerr("[GD ERR] Failed to instantiate Phase4StressTestRunner")
		quit(1)
		return

	var err: Variant = runner.RunAllTests()
	runner.free()
	if String(err) == "":
		print("[GD OK] Phase4 suite passed with Code 0.")
		quit(0)
		return

	printerr("[GD ERR] Phase4 suite failed: ", err)
	quit(1)
