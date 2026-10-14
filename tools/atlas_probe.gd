extends SceneTree

# Проба атласа ForWorldMap.png: печатает средний цвет каждой клетки сетки 16x16
# и классифицирует клетки по биому. Запуск: godot --headless --script tools/atlas_probe.gd

const ATLAS_PATH := "res://ui/assets/textures/map/ForWorldMap.png"
const CELL := 16

func _initialize() -> void:
	var img := Image.new()
	var err := img.load(ATLAS_PATH)
	if err != OK:
		print("ATLAS_PROBE_ERROR: load failed err=", err)
		quit(1)
		return
	if img.is_compressed():
		img.decompress()
	var w := img.get_width()
	var h := img.get_height()
	print("ATLAS_PROBE: ", ATLAS_PATH, " ", w, "x", h, " fmt=", img.get_format())
	var cols := int(w / CELL)
	var rows := int(h / CELL)
	print("ATLAS_PROBE: grid ", cols, "x", rows, " cell=", CELL)
	for cy in rows:
		var line := "row %02d | " % cy
		for cx in cols:
			var r := 0.0
			var g := 0.0
			var b := 0.0
			var a := 0.0
			var uniq := {}
			for y in range(cy * CELL, (cy + 1) * CELL):
				for x in range(cx * CELL, (cx + 1) * CELL):
					var px := img.get_pixel(x, y)
					r += px.r
					g += px.g
					b += px.b
					a += px.a
					uniq["%d,%d,%d,%d" % [roundi(px.r * 255), roundi(px.g * 255), roundi(px.b * 255), roundi(px.a * 255)]] = true
			var n := float(CELL * CELL)
			var cr := roundi(r / n * 255.0)
			var cg := roundi(g / n * 255.0)
			var cb := roundi(b / n * 255.0)
			var ca := roundi(a / n * 255.0)
			line += "#%02X%02X%02X/%02X(%d,%d,%d,%d)u%d  " % [cr, cg, cb, ca, cr, cg, cb, ca, uniq.size()]
		print(line)
	quit(0)