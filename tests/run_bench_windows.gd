@tool
extends SceneTree

## Headless-драйвер бенчмарка: секция (а) GenerateColonyWindow 512² и 1024².
## Run: godot --headless --path . --script tests/run_bench_windows.gd

func _init() -> void:
	_run("windows")

func _run(mode: String) -> void:
	print("[GD] Running TestGeneratorBench mode=", mode, "...")
	var runner_script = load("res://tests/TestGeneratorBench.cs")
	if runner_script == null:
		printerr("[GD ERR] Failed to load tests/TestGeneratorBench.cs")
		quit(1)
		return

	var runner = runner_script.new()
	if runner == null:
		printerr("[GD ERR] Failed to instantiate TestGeneratorBench")
		quit(1)
		return

	var err: Variant = runner.RunBenchmarks(mode)
	runner.free()
	if String(err) == "":
		print("[GD OK] Generator bench (", mode, ") finished with Code 0.")
		quit(0)
		return

	printerr("[GD ERR] Generator bench (", mode, ") failed: ", err)
	quit(1)
