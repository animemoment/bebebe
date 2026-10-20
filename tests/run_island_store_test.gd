@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestIslandMirror...")
	var test_script = load("res://tests/TestIslandMirror.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestIslandMirror.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestIslandMirror")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestIslandMirror has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestIslandMirror reported failure!")
		quit(1)
		return

	print("[GD OK] TestIslandMirror passed with Code 0.")
	quit(0)