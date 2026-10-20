@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestWorldStoreExit...")
	var test_script = load("res://tests/TestWorldStoreExit.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestWorldStoreExit.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestWorldStoreExit")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestWorldStoreExit has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestWorldStoreExit reported failure!")
		quit(1)
		return

	print("[GD OK] TestWorldStoreExit passed with Code 0.")
	quit(0)
