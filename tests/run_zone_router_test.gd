@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestZoneRouter (§119 P1: покрытие + роутер)...")
	var test_script = load("res://tests/test_zone_router.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load test_zone_router.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null or not test.has_method("Run"):
		printerr("[GD ERR] test_zone_router has no Run()")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] test_zone_router reported failure!")
		quit(1)
		return

	print("[GD OK] test_zone_router passed.")
	quit(0)
