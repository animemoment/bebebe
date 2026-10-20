@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestAdversarialConstruction...")
	var test_script = load("res://tests/TestAdversarialConstruction.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestAdversarialConstruction.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestAdversarialConstruction")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestAdversarialConstruction has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestAdversarialConstruction reported failure!")
		quit(1)
		return

	print("[GD OK] TestAdversarialConstruction passed with Code 0.")
	quit(0)
