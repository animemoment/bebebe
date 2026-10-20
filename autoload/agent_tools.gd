extends Node

# AgentTools — вспомогательный автолоад для отладки/верификации агентом.
# ВАЖНО: плагин godot-bridge управляет ТОЛЬКО autoload/mcp_interaction_server.gd
# (перезаписывает его из вендорной копии). Этот файл — наш, его никто не трогает.
#
# Вызов идёт штатной командой call_method:
#   {command:"call_method", params:{node_path:"/root/AgentTools", method:"<name>", args:[...]}}
# Поэтому аргументы должны быть простыми (String/int/float/bool) — длинные
# многострочные скрипты передаём ПУТЁМ К ФАЙЛУ (argv у one-shot node-бриджа
# ограничен, многострочный JSON ломает спавн).

const LOG_PATH := "user://logs/godot.log"

func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS

# ---------------------------------------------------------------- eval

## Выполнить GDScript-код из строки. Возврат — JSON-совместимое значение.
func eval_code(code: String) -> Dictionary:
	return _run_script(code)

## Выполнить GDScript из файла (res:// или абсолютный путь). Тело файла
## должно быть набором инструкций (как тело функции) либо содержать
## `func agent_main():` — тогда вызовется именно она.
func eval_file(path: String) -> Dictionary:
	var real := path
	if not path.begins_with("res://") and not path.begins_with("user://"):
		real = path
	var f := FileAccess.open(real, FileAccess.READ)
	if f == null:
		return {"ok": false, "error": "cannot open %s (err %d)" % [real, FileAccess.get_open_error()]}
	var code := f.get_as_text()
	f.close()
	return _run_script(code)

func _run_script(code: String) -> Dictionary:
	var body := code
	var has_entry := code.find("func agent_main(") >= 0
	var src: String
	if has_entry:
		src = "extends Node\n\n" + code + "\n"
	else:
		src = "extends Node\n\nfunc agent_main():\n" + _indent(code) + "\n"
	var script := GDScript.new()
	script.source_code = src
	var err := script.reload()
	if err != OK:
		return {"ok": false, "error": "compile failed (%d)" % err}
	var n := Node.new()
	n.set_script(script)
	n.process_mode = Node.PROCESS_MODE_ALWAYS
	add_child(n)
	var result: Variant = null
	if n.has_method("agent_main"):
		result = n.call("agent_main")
	n.queue_free()
	return {"ok": true, "result": _jsonify(result), "entry": "agent_main" if has_entry else "inline"}

func _indent(code: String) -> String:
	var out := ""
	for line in code.split("\n"):
		out += "\t" + line + "\n"
	return out

# ---------------------------------------------------------------- screenshot

## Сохранить PNG текущего кадра прямо в файл (без base64). Путь абсолютный.
func shot(path: String) -> Dictionary:
	var vp := get_viewport()
	if vp == null:
		return {"ok": false, "error": "no viewport"}
	var tex := vp.get_texture()
	if tex == null:
		return {"ok": false, "error": "no viewport texture"}
	var img := tex.get_image()
	if img == null:
		return {"ok": false, "error": "no image (frame not drawn yet?)"}
	var err := img.save_png(path)
	return {"ok": err == OK, "path": path, "width": img.get_width(), "height": img.get_height(), "error_code": err}

# ---------------------------------------------------------------- window / coords

## Реальные размеры окна и трансформации — чтобы не угадывать масштаб кликов.
func window_info() -> Dictionary:
	var w := get_window()
	var vp := get_viewport()
	var out := {
		"window_size": {"x": w.size.x, "y": w.size.y},
		"viewport_visible_rect": {"x": vp.get_visible_rect().size.x, "y": vp.get_visible_rect().size.y},
		"content_scale_mode": w.content_scale_mode,
		"content_scale_factor": w.content_scale_factor,
		"content_scale_size": {"x": w.content_scale_size.x, "y": w.content_scale_size.y},
		"window_mode": w.mode,
		"screen": w.current_screen,
		"canvas_transform": {"x": vp.get_canvas_transform().origin.x, "y": vp.get_canvas_transform().origin.y,
			"scale_x": vp.get_canvas_transform().get_scale().x, "scale_y": vp.get_canvas_transform().get_scale().y},
		"stretch_scale": ProjectSettings.get_setting("display/window/stretch/scale", 1.0),
	}
	return out

## Экранная позиция (в координатах окна) для тайла мира — по камере и WallLayer.
## Полезно, чтобы кликать точно по клетке, а не «примерно».
func screen_pos_for_tile(tile_x: int, tile_y: int, wall_layer_path: String = "/root/Node2D/MapRenderer/WallLayer") -> Dictionary:
	var wl := get_tree().root.get_node_or_null(wall_layer_path)
	if wl == null:
		return {"ok": false, "error": "no WallLayer at %s" % wall_layer_path}
	var local: Vector2 = wl.map_to_local(Vector2i(tile_x, tile_y)) # в локальных координатах слоя
	var global: Vector2 = wl.to_global(local)
	var cam := get_viewport().get_camera_2d()
	if cam == null:
		return {"ok": false, "error": "no Camera2D"}
	var screen: Vector2 = cam.get_screen_center_position()
	var zoom: Vector2 = cam.zoom
	var vp_size: Vector2 = get_viewport().get_visible_rect().size
	var sx := (global.x - screen.x) * zoom.x + vp_size.x * 0.5
	var sy := (global.y - screen.y) * zoom.y + vp_size.y * 0.5
	return {"ok": true, "screen": {"x": sx, "y": sy}, "zoom": {"x": zoom.x, "y": zoom.y},
		"camera_center": {"x": screen.x, "y": screen.y}, "viewport": {"x": vp_size.x, "y": vp_size.y}}

## Обратное: какой тайл под экранной точкой.
func tile_for_screen_pos(sx: float, sy: float, wall_layer_path: String = "/root/Node2D/MapRenderer/WallLayer") -> Dictionary:
	var wl := get_tree().root.get_node_or_null(wall_layer_path)
	if wl == null:
		return {"ok": false, "error": "no WallLayer"}
	var cam := get_viewport().get_camera_2d()
	if cam == null:
		return {"ok": false, "error": "no Camera2D"}
	var vp_size: Vector2 = get_viewport().get_visible_rect().size
	var zoom: Vector2 = cam.zoom
	var center: Vector2 = cam.get_screen_center_position()
	var gx := (sx - vp_size.x * 0.5) / zoom.x + center.x
	var gy := (sy - vp_size.y * 0.5) / zoom.y + center.y
	var tile: Vector2i = wl.local_to_map(wl.to_local(Vector2(gx, gy)))
	return {"ok": true, "tile": {"x": tile.x, "y": tile.y}, "world": {"x": gx, "y": gy}}

# ---------------------------------------------------------------- logs

## Хвост лога (user://logs/godot.log) с фильтром по подстроке.
func log_tail(pattern: String = "", max_lines: int = 40) -> Dictionary:
	var p := ProjectSettings.globalize_path(LOG_PATH)
	if not FileAccess.file_exists(LOG_PATH):
		return {"ok": false, "error": "no log file", "path": p}
	var f := FileAccess.open(LOG_PATH, FileAccess.READ)
	if f == null:
		return {"ok": false, "error": "cannot open log", "path": p}
	f.seek(max(0, f.get_length() - 262144)) # последние 256 КБ
	var content := f.get_as_text()
	f.close()
	var lines := content.split("\n")
	var out: Array = []
	for i in range(lines.size() - 1, -1, -1):
		var ln: String = lines[i]
		if ln.strip_edges().is_empty():
			continue
		if pattern.is_empty() or ln.find(pattern) >= 0:
			out.push_front(ln)
			if out.size() >= max_lines:
				break
	return {"ok": true, "count": out.size(), "lines": out, "path": p}

# ---------------------------------------------------------------- tree / nodes

## Найти ноды по подстроке пути или имени.
func nodes_matching(pattern: String, limit: int = 60) -> Dictionary:
	var out: Array = []
	var stack: Array = [get_tree().root]
	while not stack.is_empty() and out.size() < limit:
		var n: Node = stack.pop_back()
		var path := str(n.get_path())
		if path.find(pattern) >= 0 or str(n.name).find(pattern) >= 0:
			out.append({"path": path, "name": str(n.name), "class": n.get_class(), "visible": n is CanvasItem and n.visible})
		var kids := n.get_children()
		for i in range(kids.size() - 1, -1, -1):
			stack.append(kids[i])
	return {"ok": true, "count": out.size(), "nodes": out}

## Компактный дамп свойств ноды (только непустые, с лимитом).
func dump(path: String, limit: int = 80) -> Dictionary:
	var n := get_tree().root.get_node_or_null(path)
	if n == null:
		return {"ok": false, "error": "node not found: %s" % path}
	var props := {}
	var count := 0
	for p in n.get_property_list():
		var name: String = p.get("name", "")
		if name.begins_with("_") or name == "script" or name == "resource_local_to_scene":
			continue
		var usage: int = p.get("usage", 0)
		if (usage & PROPERTY_USAGE_EDITOR) == 0:
			continue
		var v: Variant = n.get(name)
		props[name] = _jsonify(v)
		count += 1
		if count >= limit:
			break
	return {"ok": true, "path": path, "class": n.get_class(), "count": count, "properties": props}

## Вызвать метод у любой ноды с приведением аргументов (обёртка над callv).
func call_on(path: String, method: String, args: Array = []) -> Dictionary:
	var n := get_tree().root.get_node_or_null(path)
	if n == null:
		return {"ok": false, "error": "node not found: %s" % path}
	if not n.has_method(method):
		return {"ok": false, "error": "no method %s on %s" % [method, path],
			"methods_sample": _matching_methods(n, method)}
	var r: Variant = n.callv(method, args)
	return {"ok": true, "result": _jsonify(r)}

func _matching_methods(n: Node, needle: String) -> Array:
	var out: Array = []
	for m in n.get_method_list():
		var mn: String = m.get("name", "")
		if mn.to_lower().find(needle.to_lower()) >= 0:
			out.append(mn)
		if out.size() >= 25:
			break
	return out

## Список методов ноды (для поиска нужного API).
func methods_of(path: String, pattern: String = "", limit: int = 120) -> Dictionary:
	var n := get_tree().root.get_node_or_null(path)
	if n == null:
		return {"ok": false, "error": "node not found: %s" % path}
	var out: Array = []
	for m in n.get_method_list():
		var mn: String = m.get("name", "")
		if pattern.is_empty() or mn.to_lower().find(pattern.to_lower()) >= 0:
			out.append(mn)
		if out.size() >= limit:
			break
	return {"ok": true, "count": out.size(), "methods": out}

# ---------------------------------------------------------------- perf / state

## Быстрый срез состояния: fps, ноды, видимость ключевых объектов.
func perf() -> Dictionary:
	return {
		"fps": Performance.get_monitor(Performance.TIME_FPS),
		"process_ms": Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0,
		"physics_ms": Performance.get_monitor(Performance.TIME_PHYSICS_PROCESS) * 1000.0,
		"objects": Performance.get_monitor(Performance.OBJECT_NODE_COUNT),
		"orphan_nodes": Performance.get_monitor(Performance.OBJECT_ORPHAN_NODE_COUNT),
		"render_draw_calls": Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME),
		"static_mem_mb": Performance.get_monitor(Performance.MEMORY_STATIC) / 1048576.0,
		"video_mem_mb": Performance.get_monitor(Performance.RENDER_VIDEO_MEM_USED) / 1048576.0,
	}

# ---------------------------------------------------------------- helpers

func _jsonify(v: Variant) -> Variant:
	if v == null:
		return null
	match typeof(v):
		TYPE_BOOL, TYPE_INT, TYPE_FLOAT, TYPE_STRING:
			return v
		TYPE_STRING_NAME:
			return str(v)
		TYPE_VECTOR2:
			return {"x": v.x, "y": v.y}
		TYPE_VECTOR2I:
			return {"x": v.x, "y": v.y}
		TYPE_VECTOR3:
			return {"x": v.x, "y": v.y, "z": v.z}
		TYPE_VECTOR3I:
			return {"x": v.x, "y": v.y, "z": v.z}
		TYPE_COLOR:
			return {"r": v.r, "g": v.g, "b": v.b, "a": v.a}
		TYPE_RECT2, TYPE_RECT2I:
			return {"x": v.position.x, "y": v.position.y, "w": v.size.x, "h": v.size.y}
		TYPE_ARRAY:
			var arr: Array = []
			for e in v:
				arr.append(_jsonify(e))
			return arr
		TYPE_PACKED_STRING_ARRAY:
			var arr2: Array = []
			for e in v:
				arr2.append(_jsonify(e))
			return arr2
		TYPE_PACKED_INT32_ARRAY:
			var arr3: Array = []
			for e in v:
				arr3.append(_jsonify(e))
			return arr3
		TYPE_PACKED_INT64_ARRAY:
			var arr4: Array = []
			for e in v:
				arr4.append(_jsonify(e))
			return arr4
		TYPE_PACKED_FLOAT32_ARRAY:
			var arr5: Array = []
			for e in v:
				arr5.append(_jsonify(e))
			return arr5
		TYPE_PACKED_FLOAT64_ARRAY:
			var arr6: Array = []
			for e in v:
				arr6.append(_jsonify(e))
			return arr6
		TYPE_PACKED_BYTE_ARRAY:
			var arr7: Array = []
			for e in v:
				arr7.append(_jsonify(e))
			return arr7
		TYPE_DICTIONARY:
			var d := {}
			for k in v.keys():
				d[str(k)] = _jsonify(v[k])
			return d
		TYPE_NODE_PATH:
			return str(v)
		_:
			return str(v)