@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestAdversarialGpuAndSimd...")
	var test_script = load("res://tests/TestAdversarialGpuAndSimd.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestAdversarialGpuAndSimd.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestAdversarialGpuAndSimd")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestAdversarialGpuAndSimd has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestAdversarialGpuAndSimd reported failure!")
		quit(1)
		return

	print("[GD OK] TestAdversarialGpuAndSimd passed with Code 0.")
	quit(0)
