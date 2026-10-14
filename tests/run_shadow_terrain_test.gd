@tool
extends SceneTree

func _init() -> void:
	print("[GD] Running TestShadowAndTerrainAudit...")
	var test_script = load("res://tests/TestShadowAndTerrainAudit.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestShadowAndTerrainAudit.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null:
		printerr("[GD ERR] Failed to instantiate TestShadowAndTerrainAudit")
		quit(1)
		return

	if not test.has_method("Run"):
		printerr("[GD ERR] TestShadowAndTerrainAudit has no Run() method")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestShadowAndTerrainAudit reported failure!")
		quit(1)
		return
	if res is int and res != 0:
		printerr("[GD ERR] TestShadowAndTerrainAudit returned exit code: %d" % res)
		quit(res)
		return

	print("[GD OK] TestShadowAndTerrainAudit passed with Code 0.")
	quit(0)
