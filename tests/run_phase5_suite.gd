@tool
extends SceneTree

## Headless-драйвер для C#-фазы 5 (по образцу run_test.gd / run_world_map_audit.gd).
## Архитектурные тесты: TileClearanceService, JobSpatialPyramid,
## StreamingWorldManager (транзакционная замена), WorldGameRules fast-path,
## ZonePriority thread-safety, MapRenderer TileSet-cache reset.
## Run: godot --headless --path . --script tests/run_phase5_suite.gd

func _init() -> void:
	print("[GD] Running Phase5StressTestRunner (RunAllTests)...")
	var runner_script = load("res://tests/Phase5StressTestRunner.cs")
	if runner_script == null:
		printerr("[GD ERR] Failed to load Phase5StressTestRunner.cs")
		quit(1)
		return

	var runner = runner_script.new()
	if runner == null:
		printerr("[GD ERR] Failed to instantiate Phase5StressTestRunner")
		quit(1)
		return

	var err: Variant = runner.RunAllTests()
	runner.free()
	if String(err) == "":
		print("[GD OK] Phase5 suite passed with Code 0.")
		quit(0)
		return

	printerr("[GD ERR] Phase5 suite failed: ", err)
	quit(1)
