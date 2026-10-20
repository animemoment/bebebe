@tool
extends SceneTree

## Headless-драйвер бенчмарка: профилирование фаз (micro) — где уходит время окна.
## Run: godot --headless --path . --script tests/run_bench_micro.gd

func _init() -> void:
	print("[GD] Running TestGeneratorBench mode=micro...")
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

	var err: Variant = runner.RunBenchmarks("micro")
	runner.free()
	if String(err) == "":
		print("[GD OK] Generator bench (micro) finished with Code 0.")
		quit(0)
		return

	printerr("[GD ERR] Generator bench (micro) failed: ", err)
	quit(1)
