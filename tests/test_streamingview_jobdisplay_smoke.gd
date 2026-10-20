@tool
extends SceneTree

# Раннер headless-смоука P6 JobDisplay (§119, task-12).
# Запуск: godot --headless --path . --script tests/test_streamingview_jobdisplay_smoke.gd

func _init() -> void:
	print("[GD] Running test_streamingview_jobdisplay_smoke...")
	var ok := false
	var test = load("res://tests/test_streamingview_jobdisplay_smoke.cs").new()
	if test != null:
		ok = test.Run()
	if ok:
		print("[GD OK] test_streamingview_jobdisplay_smoke passed with Code 0.")
		quit(0)
	else:
		printerr("[GD ERR] test_streamingview_jobdisplay_smoke failed!")
		quit(1)
