extends Node
## LiveBridge — управление РЕАЛЬНОЙ запущенной игрой из внешнего процесса (агента).
##
## Отличия от старого моста: кадро-синхронные скриншоты с сохранением на диск,
## кроп/зум, оверлей действий агента (игрок видит, что происходит), ожидание
## условий, авто-вход в игру из меню, перевод игровых координат в экранные.
##
## Протокол: TCP 127.0.0.1:9097, по одному JSON-объекту на строку.
## Запрос:  {"id":1,"cmd":"shot","args":{...}}
## Ответ:   {"id":1,"ok":true,"data":{...}}
##
## Порт можно переопределить: --live-port=9098 в аргументах запуска.

const DEFAULT_PORT := 9097
const HUD_LAYER := 120
const MAX_LOG := 200

var port: int = DEFAULT_PORT
var _server := TCPServer.new()
var _clients: Array = []          # [{peer, buf}]
var _last_cmd := "—"
var _last_result := ""
var _cmd_log: Array = []
var _hud: CanvasLayer
var _hud_label: Label
var _hud_visible := true
var _busy := false


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	for a in OS.get_cmdline_args():
		if a.begins_with("--live-port="):
			port = int(a.split("=")[1])
	_build_hud()
	var err := _server.listen(port, "127.0.0.1")
	if err != OK:
		push_warning("LiveBridge: не удалось слушать порт %d (%d)" % [port, err])
		_hud_text("LiveBridge: порт занят")
	else:
		_hud_text("LiveBridge: слушаю %d" % port)
	set_process(true)


func _process(_delta: float) -> void:
	_poll_server()
	if _hud_label != null and Engine.get_frames_per_second() > 0:
		var extra := "FPS: %d | сцена: %s" % [
			Engine.get_frames_per_second(),
			get_tree().current_scene.scene_file_path if get_tree().current_scene != null else "—",
		]
		_hud_label.text = "AGENT: %s\n%s\n%s" % [_last_cmd, extra, _last_result]


# ------------------------------------------------------------------ transport
func _poll_server() -> void:
	while _server.is_connection_available():
		var peer := _server.take_connection()
		_clients.append({"peer": peer, "buf": ""})
	var dead: Array = []
	for c in _clients:
		var peer: StreamPeerTCP = c["peer"]
		peer.poll()
		if peer.get_status() != StreamPeerTCP.STATUS_CONNECTED:
			dead.append(c)
			continue
		var avail := peer.get_available_bytes()
		if avail <= 0:
			continue
		var got: Array = peer.get_data(avail)
		if got[0] != OK:
			continue
		var text := (got[1] as PackedByteArray).get_string_from_utf8()
		c["buf"] = String(c["buf"]) + text
		while true:
			var buf: String = c["buf"]
			var nl := buf.find("\n")
			if nl < 0:
				break
			var line := buf.substr(0, nl)
			c["buf"] = buf.substr(nl + 1)
			if line.strip_edges() != "":
				_handle_line(c, line)
	for c in dead:
		_clients.erase(c)


func _handle_line(c: Dictionary, line: String) -> void:
	var req = JSON.parse_string(line)
	if typeof(req) != TYPE_DICTIONARY:
		_send(c, {"id": 0, "ok": false, "error": "bad_json"})
		return
	var id = req.get("id", 0)
	var cmd := String(req.get("cmd", ""))
	var args: Dictionary = req.get("args", {})
	_last_cmd = cmd
	_record(cmd, args)
	# всё, что ждёт кадров/условий -- в корутине, чтобы не блокировать кадр
	_dispatch(c, id, cmd, args)


func _dispatch(c: Dictionary, id, cmd: String, args: Dictionary) -> void:
	var res: Dictionary
	match cmd:
		"ping", "hello":
			res = _cmd_state()
		"state":
			res = _cmd_state()
		"shot":
			res = await _cmd_shot(args)
		"look":
			res = await _cmd_look(args)
		"ui":
			res = _cmd_ui()
		"tree":
			res = _cmd_tree(args)
		"eval":
			res = _cmd_eval(args)
		"run":
			res = await _cmd_run(args)
		"click":
			res = await _cmd_click(args)
		"move":
			res = _cmd_move(args)
		"key":
			res = _cmd_key(args)
		"text":
			res = await _cmd_text(args)
		"wait":
			await get_tree().create_timer(float(args.get("ms", 100)) / 1000.0).timeout
			res = {"ok": true}
		"wait_until":
			res = await _cmd_wait_until(args)
		"start_game":
			res = await _cmd_start_game(args)
		"scene":
			res = _cmd_scene(args)
		"hud":
			_hud_visible = bool(args.get("on", true))
			if _hud != null:
				_hud.visible = _hud_visible
			res = {"ok": true, "hud": _hud_visible}
		"tile_to_screen":
			res = _cmd_tile_to_screen(args)
		"perf":
			res = _cmd_perf()
		"quit":
			_send(c, {"id": id, "ok": true, "data": {"quitting": true}})
			_quit_soon()
			return
		_:
			res = {"ok": false, "error": "unknown_cmd: " + cmd}
	if not res.has("ok"):
		res["ok"] = true
	_send(c, {"id": id, "ok": bool(res.get("ok", true)), "data": res})


func _send(c: Dictionary, obj: Dictionary) -> void:
	var peer: StreamPeerTCP = c["peer"]
	if peer.get_status() != StreamPeerTCP.STATUS_CONNECTED:
		return
	peer.put_data((JSON.stringify(obj) + "\n").to_utf8_buffer())
	_last_result = "ok" if bool(obj.get("ok", false)) else String(obj.get("error", "error"))


# ------------------------------------------------------------------- commands
func _cmd_state() -> Dictionary:
	var scene := get_tree().current_scene
	var nodes := _count_nodes(get_tree().root)
	return {
		"ok": true,
		"engine": Engine.get_version_info().get("string", "?"),
		"scene": scene.scene_file_path if scene != null else "",
		"scene_name": scene.name if scene != null else "",
		"nodes": nodes,
		"fps": Engine.get_frames_per_second(),
		"frame": Engine.get_frames_drawn(),
		"window": {
			"mode": DisplayServer.window_get_mode(),
			"focused": DisplayServer.window_is_focused(),
			"position": DisplayServer.window_get_position(),
			"size": DisplayServer.window_get_size(),
			"display": DisplayServer.get_name(),
			"screen": DisplayServer.window_get_current_screen(),
		},
		"log": _cmd_log.slice(-10),
	}


func _cmd_shot(args: Dictionary) -> Dictionary:
	var scale := int(args.get("scale", 1))
	var region := Rect2i()
	if args.has("region"):
		var r: Array = args["region"]
		region = Rect2i(int(r[0]), int(r[1]), int(r[2]), int(r[3]))
	return await _capture(region, scale, String(args.get("name", "shot")))


func _cmd_look(args: Dictionary) -> Dictionary:
	# Композит: полный кадр + увеличенный кроп вокруг игрового центра/камеры + UI.
	var frames: int = int(args.get("frames", 2))
	if frames > 0:
		await RenderingServer.frame_post_draw
	var full: Dictionary = await _capture(Rect2i(), 1, "look_full")
	var vp := get_viewport().get_visible_rect().size
	var scale_f := float(args.get("zoom", 2.0))
	var cw := int(vp.x / scale_f)
	var ch := int(vp.y / scale_f)
	var center := Vector2(vp.x * 0.5, vp.y * 0.5)
	if args.has("center_tile"):
		var t: Array = args["center_tile"]
		var sp = _tile_to_screen(int(t[0]), int(t[1]))
		if sp != null:
			center = sp
	var rx := int(clamp(center.x - cw * 0.5, 0, max(0, vp.x - cw)))
	var ry := int(clamp(center.y - ch * 0.5, 0, max(0, vp.y - ch)))
	var zoomed: Dictionary = await _capture(Rect2i(rx, ry, cw, ch), int(scale_f), "look_zoom")
	var ui := _cmd_ui()
	return {
		"ok": true,
		"full_path": full.get("path", ""),
		"zoom_path": zoomed.get("path", ""),
		"zoom_rect": [rx, ry, cw, ch],
		"screen_size": [int(vp.x), int(vp.y)],
		"ui": ui.get("elements", []),
		"state": _cmd_state(),
	}


func _cmd_ui() -> Dictionary:
	var out: Array = []
	var stack: Array = [get_tree().root]
	while not stack.is_empty():
		var n: Node = stack.pop_back()
		if n is Control:
			var c := n as Control
			if c.is_visible_in_tree():
				var txt := ""
				if c is Label:
					txt = (c as Label).text
				elif c is Button:
					txt = (c as Button).text
				elif c is RichTextLabel:
					txt = (c as RichTextLabel).text
				elif c is LineEdit:
					txt = (c as LineEdit).text
				out.append({
					"path": str(c.get_path()),
					"type": c.get_class(),
					"text": txt.substr(0, 80),
					"rect": [int(c.global_position.x), int(c.global_position.y),
							 int(c.size.x), int(c.size.y)],
				})
		for ch in n.get_children():
			stack.append(ch)
	return {"ok": true, "elements": out, "count": out.size()}


func _cmd_tree(args: Dictionary) -> Dictionary:
	var depth := int(args.get("depth", 2))
	var root_path := String(args.get("path", "/root"))
	var node := get_tree().root.get_node_or_null(root_path)
	if node == null:
		return {"ok": false, "error": "no_node: " + root_path}
	return {"ok": true, "tree": _tree_dict(node, depth)}


func _cmd_eval(args: Dictionary) -> Dictionary:
	var code := String(args.get("code", ""))
	if code.strip_edges() == "":
		return {"ok": false, "error": "empty_code"}
	if not _is_single_expression(code):
		return {"ok": false, "error": "not_single_expression",
				"hint": "для блоков используйте cmd=run"}
	var expr := Expression.new()
	var err := expr.parse(code, ["node", "self_node"])
	if err != OK:
		return {"ok": false, "error": "parse_error: " + expr.get_error_text()}
	var val = expr.execute([self, self], null, false)
	if expr.has_execute_failed():
		return {"ok": false, "error": "exec_failed: " + expr.get_error_text()}
	return {"ok": true, "result": val, "type": type_string(typeof(val))}


func _cmd_run(args: Dictionary) -> Dictionary:
	var code := String(args.get("code", ""))
	var src := "extends Node\nfunc run():\n"
	for line in code.split("\n"):
		src += "\t" + line + "\n"
	var script := GDScript.new()
	script.source_code = src
	var err := script.reload()
	if err != OK:
		return {"ok": false, "error": "parse_error", "detail": "reload=%d" % err}
	var inst = script.new()
	if inst == null:
		return {"ok": false, "error": "instantiate_failed"}
	var result = inst.call("run")
	if inst is Node:
		inst.free()
	return {"ok": true, "result": result}


func _cmd_click(args: Dictionary) -> Dictionary:
	var pos := Vector2(float(args.get("x", 0)), float(args.get("y", 0)))
	var button := int(args.get("button", MOUSE_BUTTON_LEFT))
	var motion := InputEventMouseMotion.new()
	motion.position = pos
	motion.global_position = pos
	Input.parse_input_event(motion)
	await get_tree().process_frame
	var down := InputEventMouseButton.new()
	down.button_index = button
	down.pressed = true
	down.position = pos
	down.global_position = pos
	Input.parse_input_event(down)
	await get_tree().process_frame
	var up := InputEventMouseButton.new()
	up.button_index = button
	up.pressed = false
	up.position = pos
	up.global_position = pos
	Input.parse_input_event(up)
	await get_tree().process_frame
	return {"ok": true, "clicked": [int(pos.x), int(pos.y)]}


func _cmd_move(args: Dictionary) -> Dictionary:
	var pos := Vector2(float(args.get("x", 0)), float(args.get("y", 0)))
	var motion := InputEventMouseMotion.new()
	motion.position = pos
	motion.global_position = pos
	Input.parse_input_event(motion)
	return {"ok": true, "moved": [int(pos.x), int(pos.y)]}


func _cmd_key(args: Dictionary) -> Dictionary:
	var keycode := int(args.get("keycode", 0))
	var ev := InputEventKey.new()
	ev.keycode = keycode
	ev.physical_keycode = keycode
	ev.pressed = bool(args.get("pressed", true))
	Input.parse_input_event(ev)
	return {"ok": true}


func _cmd_text(args: Dictionary) -> Dictionary:
	var s := String(args.get("text", ""))
	for i in s.length():
		var ev := InputEventKey.new()
		ev.unicode = s.unicode_at(i)
		ev.pressed = true
		Input.parse_input_event(ev)
		await get_tree().process_frame
		var up := InputEventKey.new()
		up.unicode = s.unicode_at(i)
		up.pressed = false
		Input.parse_input_event(up)
	return {"ok": true, "text": s}


func _cmd_wait_until(args: Dictionary) -> Dictionary:
	var code := String(args.get("expr", "true"))
	var timeout_ms := int(args.get("timeout_ms", 5000))
	var poll_ms := int(args.get("poll_ms", 100))
	var started := Time.get_ticks_msec()
	while Time.get_ticks_msec() - started < timeout_ms:
		var r := _cmd_eval({"code": code})
		if bool(r.get("ok", false)) and _truthy(r.get("result")):
			return {"ok": true, "matched": true,
					"elapsed_ms": Time.get_ticks_msec() - started,
					"result": r.get("result")}
		await get_tree().create_timer(float(poll_ms) / 1000.0).timeout
	return {"ok": true, "matched": false, "elapsed_ms": Time.get_ticks_msec() - started}


func _cmd_start_game(args: Dictionary) -> Dictionary:
	var matchers: Array = args.get("buttons", ["играть", "play", "начать", "старт", "new game"])
	var btn := _find_button(matchers)
	if btn == null:
		return {"ok": false, "error": "no_button", "looked_for": matchers,
				"ui": _cmd_ui().get("elements", [])}
	btn.pressed.emit()
	var wait_ms := int(args.get("wait_ms", 4000))
	await get_tree().create_timer(float(wait_ms) / 1000.0).timeout
	return {"ok": true, "pressed": (btn as Button).text, "state": _cmd_state()}


func _cmd_scene(args: Dictionary) -> Dictionary:
	var path := String(args.get("path", ""))
	if path == "":
		return {"ok": false, "error": "no_path"}
	var err := get_tree().change_scene_to_file(path)
	return {"ok": err == OK, "error": "" if err == OK else "change_scene_failed: %d" % err}


func _cmd_tile_to_screen(args: Dictionary) -> Dictionary:
	var p = _tile_to_screen(int(args.get("x", 0)), int(args.get("y", 0)))
	if p == null:
		return {"ok": false, "error": "no_camera"}
	return {"ok": true, "screen": [int(p.x), int(p.y)]}


func _cmd_perf() -> Dictionary:
	return {
		"ok": true,
		"fps": Engine.get_frames_per_second(),
		"frame": Engine.get_frames_drawn(),
		"process_ms": Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0,
		"physics_ms": Performance.get_monitor(Performance.TIME_PHYSICS_PROCESS) * 1000.0,
		"objects": Performance.get_monitor(Performance.OBJECT_NODE_COUNT),
		"draw_calls": Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME),
	}


# -------------------------------------------------------------------- helpers
func _capture(region: Rect2i, scale: int, name: String) -> Dictionary:
	await RenderingServer.frame_post_draw
	var img: Image = get_viewport().get_texture().get_image()
	if img == null:
		return {"ok": false, "error": "no_image"}
	if region.size.x > 0 and region.size.y > 0:
		var r := region.intersection(Rect2i(Vector2i.ZERO, img.get_size()))
		if r.size.x > 0 and r.size.y > 0:
			img = img.get_region(r)
	if scale > 1:
		img.resize(img.get_width() * scale, img.get_height() * scale, Image.INTERPOLATE_NEAREST)
	var dir := ProjectSettings.globalize_path("res://shots/live")
	DirAccess.make_dir_recursive_absolute(dir)
	var stamp := Time.get_datetime_string_from_system().replace(":", "-").replace("T", "_")
	var path := dir.path_join("%s_%s.png" % [name, stamp])
	var err := img.save_png(path)
	return {"ok": err == OK, "path": path, "width": img.get_width(), "height": img.get_height(),
			"error": "" if err == OK else "save_failed: %d" % err}


func _tile_to_screen(tx: int, ty: int):
	var cam := get_viewport().get_camera_2d()
	if cam == null:
		return null
	var world := Vector2(tx * 32.0 + 16.0, ty * 32.0 + 16.0)  # сетка 32 px
	return get_viewport().get_canvas_transform() * world


func _find_button(matchers: Array) -> Button:
	var stack: Array = [get_tree().root]
	while not stack.is_empty():
		var n: Node = stack.pop_back()
		if n is Button and (n as Button).is_visible_in_tree():
			var t := (n as Button).text.to_lower()
			for m in matchers:
				if t.contains(String(m).to_lower()):
					return n
		for ch in n.get_children():
			stack.append(ch)
	return null


func _count_nodes(n: Node) -> int:
	var total := 1
	for ch in n.get_children():
		total += _count_nodes(ch)
	return total


func _tree_dict(n: Node, depth: int) -> Dictionary:
	var d := {"name": n.name, "type": n.get_class()}
	if depth > 0:
		var kids: Array = []
		for ch in n.get_children():
			kids.append(_tree_dict(ch, depth - 1))
		d["children"] = kids
	return d


func _is_single_expression(code: String) -> bool:
	if code.contains("\n") or code.contains(";"):
		return false
	var s := code.strip_edges()
	var banned := ["var ", "for ", "while ", "if ", "func ", "class ", "const "]
	for b in banned:
		if s.begins_with(b):
			return false
	return true


func _truthy(v) -> bool:
	if v == null:
		return false
	if typeof(v) == TYPE_BOOL:
		return v
	if typeof(v) in [TYPE_INT, TYPE_FLOAT]:
		return float(v) != 0.0
	if typeof(v) == TYPE_STRING:
		return String(v) != ""
	return true


func _record(cmd: String, args: Dictionary) -> void:
	_cmd_log.append({"t": Time.get_datetime_string_from_system(), "cmd": cmd,
					 "args": args})
	if _cmd_log.size() > MAX_LOG:
		_cmd_log = _cmd_log.slice(_cmd_log.size() - MAX_LOG)


func _quit_soon() -> void:
	await get_tree().process_frame
	get_tree().quit()


# ------------------------------------------------------------------------ HUD
func _build_hud() -> void:
	_hud = CanvasLayer.new()
	_hud.name = "AgentHUD"
	_hud.layer = HUD_LAYER
	add_child(_hud)
	var panel := PanelContainer.new()
	var sb := StyleBoxFlat.new()
	sb.bg_color = Color(0.05, 0.05, 0.08, 0.72)
	sb.border_color = Color(0.2, 0.9, 0.4, 0.9)
	sb.set_border_width_all(2)
	sb.set_corner_radius_all(6)
	sb.content_margin_left = 10
	sb.content_margin_right = 10
	sb.content_margin_top = 6
	sb.content_margin_bottom = 6
	panel.add_theme_stylebox_override("panel", sb)
	panel.position = Vector2(12, 12)
	_hud_label = Label.new()
	_hud_label.add_theme_color_override("font_color", Color(0.75, 1.0, 0.8))
	_hud_label.add_theme_color_override("font_shadow_color", Color(0, 0, 0, 0.8))
	_hud_label.add_theme_constant_override("shadow_offset_x", 1)
	_hud_label.add_theme_constant_override("shadow_offset_y", 1)
	_hud_label.text = "LiveBridge"
	panel.add_child(_hud_label)
	_hud.add_child(panel)


func _hud_text(t: String) -> void:
	if _hud_label != null:
		_hud_label.text = t
