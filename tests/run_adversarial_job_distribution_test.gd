@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestAdversarialJobDistribution...")
	var test_script = load("res://tests/TestAdversarialJobDistribution.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestAdversarialJobDistribution.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestAdversarialJobDistribution")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestAdversarialJobDistribution has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestAdversarialJobDistribution reported failure!")
		quit(1)
		return

	print("[GD OK] TestAdversarialJobDistribution passed with Code 0.")
	quit(0)
