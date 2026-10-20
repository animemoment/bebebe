@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# test_central_block_zone (§119 P5: центральный блок)...")
	var test_script = load("res://tests/test_central_block_zone.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load test_central_block_zone.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null or not test.has_method("Run"):
		printerr("[GD ERR] test_central_block_zone has no Run()")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] test_central_block_zone reported failure!")
		quit(1)
		return

	print("[GD OK] test_central_block_zone passed.")
	quit(0)
