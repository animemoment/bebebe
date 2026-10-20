@tool
extends SceneTree

## Headless-драйвер полного бенчмарка генератора (dev-generator, PLAN §117).
## Run: godot --headless --path . --script tests/run_generator_bench.gd
## Цифры печатает сам C#-раннер через GD.Print("[BENCH] ...").
## Секции также запускаются поодиночке: run_bench_windows.gd / run_bench_chunks.gd /
## run_bench_cellat.gd (таймаут одного tool-вызова ~130с — полный прогон может не влезть).

func _init() -> void:
	print("[GD] Running TestGeneratorBench (all)...")
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

	var err: Variant = runner.RunBenchmarks("all")
	runner.free()
	if String(err) == "":
		print("[GD OK] Generator bench finished with Code 0.")
		quit(0)
		return

	printerr("[GD ERR] Generator bench failed: ", err)
	quit(1)
