@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestDestroyerStreaming...")
	var test_script = load("res://tests/TestDestroyerStreaming.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestDestroyerStreaming.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestDestroyerStreaming")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestDestroyerStreaming has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestDestroyerStreaming reported failure!")
		quit(1)
		return

	print("[GD OK] TestDestroyerStreaming passed with Code 0.")
	quit(0)
