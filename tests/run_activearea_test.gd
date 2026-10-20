@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestActiveArea...")
	var test_script = load("res://tests/TestActiveArea.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestActiveArea.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestActiveArea")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestActiveArea has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestActiveArea reported failure!")
		quit(1)
		return

	print("[GD OK] TestActiveArea passed with Code 0.")
	quit(0)
