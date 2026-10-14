extends SceneTree

func _init() -> void:
	print("[GD] Running C# TestMacroRenderGpuLoad...")
	var script = load("res://tests/TestMacroRenderGpuLoad.cs")
	if script == null:
		printerr("[GD ERR] Cannot load TestMacroRenderGpuLoad.cs")
		quit(1)
		return
	var root_node = script.new()
	root.add_child(root_node)
