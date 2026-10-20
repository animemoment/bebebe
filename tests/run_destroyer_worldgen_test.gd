@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestDestroyerWorldGen...")
	var test_script = load("res://tests/TestDestroyerWorldGen.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestDestroyerWorldGen.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestDestroyerWorldGen")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestDestroyerWorldGen has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestDestroyerWorldGen reported failure!")
		quit(1)
		return

	print("[GD OK] TestDestroyerWorldGen passed with Code 0.")
	quit(0)
