@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestWorldGenV2...")
	var test_script = load("res://tests/TestWorldGenV2.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestWorldGenV2.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestWorldGenV2")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestWorldGenV2 has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestWorldGenV2 reported failure!")
		quit(1)
		return

	print("[GD OK] TestWorldGenV2 passed with Code 0.")
	quit(0)
