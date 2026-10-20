@tool
extends SceneTree

## P7б: прогресс работ переживает прерывание (per-cell store).
## По образцу tests/run_test.gd.

func _init() -> void:
	print("[GD] Running C# TestWorkProgressPersistence...")
	var test_script = load("res://tests/TestWorkProgressPersistence.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestWorkProgressPersistence.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestWorkProgressPersistence")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestWorkProgressPersistence has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestWorkProgressPersistence reported failure!")
		quit(1)
		return
	if res is int and res != 0:
		printerr("[GD ERR] TestWorkProgressPersistence returned exit code: %d" % res)
		quit(res)
		return

	print("[GD OK] TestWorkProgressPersistence passed with Code 0.")
	quit(0)
