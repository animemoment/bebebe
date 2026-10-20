extends SceneTree

# §117/F4 (dev-streaming-view): раннер теста паритета прореживания декора
# StreamingWorldView с островом. Запуск:
#   godot --headless --path <project> --script tests/test_streamingview_decor_seam.gd

func _init():
	print("[GD] Running TestStreamingViewDecorSeam...")
	var test = load("res://tests/test_streamingview_decor_seam.cs").new()
	var ok = test.Run()
	if ok:
		print("[GD OK] TestStreamingViewDecorSeam passed with Code 0.")
		quit(0)
	else:
		printerr("[GD ERR] TestStreamingViewDecorSeam failed!")
		quit(1)
