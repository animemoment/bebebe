@tool
extends SceneTree

## P7б п.3-5: .zones.json + payload'ы + state-replay (по образцу run_test.gd).

func _init() -> void:
	print("[GD] Running C# TestIslandZonePersistence...")
	var test_script = load("res://tests/TestIslandZonePersistence.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestIslandZonePersistence.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestIslandZonePersistence")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestIslandZonePersistence has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestIslandZonePersistence reported failure!")
		quit(1)
		return
	if res is int and res != 0:
		printerr("[GD ERR] TestIslandZonePersistence returned exit code: %d" % res)
		quit(res)
		return

	print("[GD OK] TestIslandZonePersistence passed with Code 0.")
	quit(0)
