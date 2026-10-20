extends SceneTree

func _init():
	print("[GD] Running TestWorldGenSeamAudit...")
	var test = load("res://tests/TestWorldGenSeamAudit.cs").new()
	var ok = test.Run()
	if ok:
		print("[GD OK] TestWorldGenSeamAudit passed with Code 0.")
		quit(0)
	else:
		printerr("[GD ERR] TestWorldGenSeamAudit failed!")
		quit(1)
