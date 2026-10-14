@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestPerformanceComparison...")
	var test_script = load("res://tests/TestPerformanceComparison.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestPerformanceComparison.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestPerformanceComparison")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestPerformanceComparison has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestPerformanceComparison reported failure!")
		quit(1)
		return

	print("[GD OK] TestPerformanceComparison passed with Code 0.")
	quit(0)
