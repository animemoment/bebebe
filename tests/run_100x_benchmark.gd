@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# Test100xPeakBenchmark...")
	var test_script = load("res://tests/Test100xPeakBenchmark.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load Test100xPeakBenchmark.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate Test100xPeakBenchmark")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] Test100xPeakBenchmark has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] Test100xPeakBenchmark reported failure!")
		quit(1)
		return
	if res is int and res != 0:
		printerr("[GD ERR] Test100xPeakBenchmark returned exit code: %d" % res)
		quit(res)
		return

	print("[GD OK] Test100xPeakBenchmark passed with Code 0.")
	quit(0)
