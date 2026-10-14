@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestExtreme100kPathStress...")
	var test_script = load("res://tests/TestExtreme100kPathStress.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestExtreme100kPathStress.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestExtreme100kPathStress")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestExtreme100kPathStress has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestExtreme100kPathStress reported failure!")
		quit(1)
		return

	print("[GD OK] TestExtreme100kPathStress passed with Code 0.")
	quit(0)
