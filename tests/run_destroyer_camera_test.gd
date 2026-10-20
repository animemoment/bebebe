@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestDestroyerCamera...")
	var test_script = load("res://tests/TestDestroyerCamera.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestDestroyerCamera.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestDestroyerCamera")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestDestroyerCamera has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestDestroyerCamera reported failure!")
		quit(1)
		return

	print("[GD OK] TestDestroyerCamera passed with Code 0.")
	quit(0)
