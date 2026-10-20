@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# BenchPathfinding (HPA* regime §118.5)...")
	var test_script = load("res://tests/BenchPathfinding.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load BenchPathfinding.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null or not test.has_method("Run"):
		printerr("[GD ERR] BenchPathfinding has no Run()")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] BenchPathfinding reported failure!")
		quit(1)
		return

	print("[GD OK] BenchPathfinding passed.")
	quit(0)