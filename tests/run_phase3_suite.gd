@tool
extends SceneTree

## Headless-драйвер для C#-фазы 3 (по образцу run_phase4_suite.gd / run_test.gd).
## Ключевой кейс: TestSimulationThreadStartIdempotency — двойной Start() на одном
## экземпляре AgentSimulationThread должен чисто останавливаться (проверка защиты
## от двух параллельных петель симуляции).
## Run: godot --headless --path . --script tests/run_phase3_suite.gd

func _init() -> void:
	print("[GD] Running Phase3StressTestRunner (RunAllTests)...")
	var runner_script = load("res://tests/Phase3StressTestRunner.cs")
	if runner_script == null:
		printerr("[GD ERR] Failed to load Phase3StressTestRunner.cs")
		quit(1)
		return

	var runner = runner_script.new()
	if runner == null:
		printerr("[GD ERR] Failed to instantiate Phase3StressTestRunner")
		quit(1)
		return

	var err: Variant = runner.RunAllTests()
	runner.free()
	if String(err) == "":
		print("[GD OK] Phase3 suite passed with Code 0.")
		quit(0)
		return

	printerr("[GD ERR] Phase3 suite failed: ", err)
	quit(1)