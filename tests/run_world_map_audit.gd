@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running TestWorldMapRealismAudit...")
	var test_script = load("res://tests/TestWorldMapRealismAudit.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestWorldMapRealismAudit.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestWorldMapRealismAudit")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestWorldMapRealismAudit has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestWorldMapRealismAudit reported failure!")
		quit(1)
		return
	if res is int and res != 0:
		printerr("[GD ERR] TestWorldMapRealismAudit returned exit code: %d" % res)
		quit(res)
		return

	print("[GD OK] TestWorldMapRealismAudit passed with Code 0.")
	quit(0)
