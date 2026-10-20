@tool
extends SceneTree

# §119 P5 смоук: Build.tscn инстанцируется, кнопка CentralBlockButton на месте,
# иконка загрузилась по uid://dsiddo1lt1scb.

func _init() -> void:
	var packed = load("res://scenes/ui/Build.tscn")
	if packed == null:
		printerr("[GD ERR] Build.tscn не загрузился")
		quit(1)
		return
	var root = packed.instantiate()
	if root == null:
		printerr("[GD ERR] Build.tscn не инстанцировался")
		quit(1)
		return
	var btn = root.find_child("CentralBlockButton", true, false)
	if btn == null:
		printerr("[GD ERR] CentralBlockButton не найден в Build.tscn")
		quit(1)
		return
	var icon = btn.get("icon")
	if icon == null:
		printerr("[GD ERR] у CentralBlockButton нет иконки (uid://dsiddo1lt1scb не загрузился)")
		quit(1)
		return
	var lbl = root.find_child("Label", true, false)
	print("[GD OK] Build.tscn: CentralBlockButton найден, иконка=", icon.resource_name if icon.resource_name != "" else "central.png",
		", размер=", icon.get_width(), "x", icon.get_height(),
		", label=", lbl.text.replace("\n", " / ") if lbl != null else "нет")
	root.free()
	quit(0)
