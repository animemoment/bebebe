@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestAdversarialFlowField...")
	var test_script = load("res://tests/TestAdversarialFlowField.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestAdversarialFlowField.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestAdversarialFlowField")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestAdversarialFlowField has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestAdversarialFlowField reported failure!")
		quit(1)
		return

	print("[GD OK] TestAdversarialFlowField passed with Code 0.")
	quit(0)
