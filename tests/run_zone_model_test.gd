@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestZoneModel (§119 P0: модель зон — радиус)...")
	var test_script = load("res://tests/test_zone_model.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load test_zone_model.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null or not test.has_method("Run"):
		printerr("[GD ERR] TestZoneModel has no Run()")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestZoneModel reported failure!")
		quit(1)
		return

	print("[GD OK] TestZoneModel passed.")
	quit(0)
