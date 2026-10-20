@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# test_zone_agents (§119 P2: Worker'ы зон)...")
	var test_script = load("res://tests/test_zone_agents.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load test_zone_agents.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null or not test.has_method("Run"):
		printerr("[GD ERR] test_zone_agents has no Run()")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] test_zone_agents reported failure!")
		quit(1)
		return

	print("[GD OK] test_zone_agents passed.")
	quit(0)
