@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestGlobal100kBreakdown...")
	var test_script = load("res://tests/TestGlobal100kBreakdown.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestGlobal100kBreakdown.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestGlobal100kBreakdown")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestGlobal100kBreakdown has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestGlobal100kBreakdown reported failure!")
		quit(1)
		return

	print("[GD OK] TestGlobal100kBreakdown passed with Code 0.")
	quit(0)
