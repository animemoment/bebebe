@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# Test10kBenchmark...")
	var test_script = load("res://tests/Test10kBenchmark.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load Test10kBenchmark.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate Test10kBenchmark")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] Test10kBenchmark has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] Test10kBenchmark reported failure!")
		quit(1)
		return
	if res is int and res != 0:
		printerr("[GD ERR] Test10kBenchmark returned exit code: %d" % res)
		quit(res)
		return

	print("[GD OK] Test10kBenchmark passed with Code 0.")
	quit(0)
