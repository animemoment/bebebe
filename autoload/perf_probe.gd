extends Node
## PerfProbe — объективный рантайм-пробник производительности кадров.
##
## Собирает длительности кадров в кольцевой буфер (по умолчанию 1800 кадров,
## ~30 с при 60 fps) без аллокаций на кадр, считает процентили (p50/p90/p95/p99),
## max, среднее, количество «долгих» кадров (>16.7 мс и >33 мс) и хранит
## «худшие 10 кадров» с контекстом (сцена, камера, число узлов, активные тела).
##
## Доступ извне — через уже существующую команду моста call_method:
##   node_path="/root/PerfProbe", method="report" | "reset" | "mark" | "set_enabled"
## Отдельная MCP-команда не требуется.
##
## Пример:
##   PerfProbe.reset()
##   PerfProbe.mark("before_dash")
##   # ... действие игрока ...
##   print(PerfProbe.report().p95_ms)

const VERSION := 1

const DEFAULT_CAPACITY := 1800
## Размер кольца снимков контекста (сцена/камера/узлы/тела).
const CTX_CAPACITY := 64
const MAX_MARKS := 512
const WORST_COUNT := 10
## Порог «долгого» кадра, мс (60 fps).
const LONG_MS := 16.7
## Порог «очень долгого» кадра, мс (30 fps).
const VERY_LONG_MS := 33.0
## Как часто обновлять снимок контекста, мкс (0.5 с). Длинные кадры снимаются вне очереди.
const CTX_REFRESH_USEC := 500000

## Включён ли сбор. При false _process отключён полностью (нулевые накладные расходы).
var enabled: bool = true
## Ёмкость кольцевого буфера в кадрах.
var capacity: int = DEFAULT_CAPACITY

# --- Кольцевой буфер кадров (выделяется один раз, на кадр ничего не аллоцируется) ---
var _cap: int = 0
var _delta_ms := PackedFloat32Array()
var _proc_ms := PackedFloat32Array()
var _phys_ms := PackedFloat32Array()
var _fps := PackedFloat32Array()
var _frame_no := PackedInt32Array()
var _ctx_id := PackedInt32Array()
var _head: int = 0
var _count: int = 0

# --- Кольцо снимков контекста ---
var _ctx_last: int = 0
var _ctx_scene := PackedStringArray()
var _ctx_cam := PackedVector3Array()
var _ctx_dim := PackedInt32Array()
var _ctx_nodes := PackedInt32Array()
var _ctx_body3d := PackedInt32Array()
var _ctx_body2d := PackedInt32Array()
var _ctx_stamp_usec := PackedInt64Array()

# --- Метки ---
var _marks: Array[Dictionary] = []

# --- Собственная стоимость пробника ---
var _cost_total_usec: int = 0
var _cost_max_usec: int = 0
var _cost_frames: int = 0
var _last_ctx_usec: int = 0

# --- Последний кадр (для mark до записи кадра) ---
var _last_delta_ms: float = 0.0
var _last_fps: float = 0.0


func _init() -> void:
	# Работаем и на паузе: пробник должен мерить и замороженные кадры.
	process_mode = Node.PROCESS_MODE_ALWAYS
	_alloc(capacity)


func _ready() -> void:
	set_process(enabled)


# ---------------------------------------------------------------------------
# Публичный API
# ---------------------------------------------------------------------------

## Собрать отчёт по последним window_frames кадрам (<= ёмкости буфера).
## Возвращает JSON-сериализуемую структуру.
func report(window_frames: int = DEFAULT_CAPACITY) -> Dictionary:
	var n := _count
	var want := window_frames
	if want <= 0 or want > n:
		want = n

	var out := {
		"frames": want,
		"capacity": _cap,
		"enabled": enabled,
		"version": VERSION,
		"p50_ms": 0.0,
		"p90_ms": 0.0,
		"p95_ms": 0.0,
		"p99_ms": 0.0,
		"max_ms": 0.0,
		"mean_ms": 0.0,
		"min_ms": 0.0,
		"long_16": 0,
		"long_33": 0,
		"worst": [],
		"marks": [],
		"fps_now": _r(Engine.get_frames_per_second()),
		"frame_now": Engine.get_process_frames(),
		"probe_cost_ms_avg": 0.0,
		"probe_cost_ms_max": 0.0,
	}

	# Метки видны всегда, даже если буфер пуст (например, после reset).
	if n == 0:
		out["marks"] = _marks.duplicate(true)
		out["window_start_frame"] = -1
		out["window_end_frame"] = -1
		return out

	var start := _window_start(want)
	var sorted_ms := PackedFloat32Array()
	sorted_ms.resize(want)

	var sum := 0.0
	var longest_ms := 0.0
	var long16 := 0
	var long33 := 0
	var order: Array = []
	order.resize(want)

	for k in want:
		var ix := (start + k) % _cap
		var d := _delta_ms[ix]
		sorted_ms[k] = d
		sum += d
		if d > longest_ms:
			longest_ms = d
		if d > LONG_MS:
			long16 += 1
		if d > VERY_LONG_MS:
			long33 += 1
		order[k] = ix

	sorted_ms.sort()

	var avg := sum / float(want)
	out["frames"] = want
	out["p50_ms"] = _r(_percentile(sorted_ms, 50.0))
	out["p90_ms"] = _r(_percentile(sorted_ms, 90.0))
	out["p95_ms"] = _r(_percentile(sorted_ms, 95.0))
	out["p99_ms"] = _r(_percentile(sorted_ms, 99.0))
	out["max_ms"] = _r(longest_ms)
	out["mean_ms"] = _r(avg)
	out["min_ms"] = _r(sorted_ms[0])
	out["long_16"] = long16
	out["long_33"] = long33

	var first_frame := int(_frame_no[start])
	var last_frame := int(_frame_no[(start + want - 1) % _cap])
	out["window_start_frame"] = first_frame
	out["window_end_frame"] = last_frame

	# Худшие кадры: сортируем индексы окна по убыванию delta.
	order.sort_custom(func(a, b): return _delta_ms[a] > _delta_ms[b])
	var take: int = mini(WORST_COUNT, order.size())
	var worst: Array = []
	for w in take:
		var ix: int = order[w]
		var cid: int = _ctx_id[ix]
		var cam: Vector3 = _ctx_cam[cid]
		var entry := {
			"frame": int(_frame_no[ix]),
			"delta_ms": _r(_delta_ms[ix]),
			"process_ms": _r(_proc_ms[ix]),
			"physics_ms": _r(_phys_ms[ix]),
			"fps": _r(_fps[ix]),
			"scene": _ctx_scene[cid],
			"camera": [_r(cam.x), _r(cam.y)],
			"camera_dim": int(_ctx_dim[cid]),
			"nodes": int(_ctx_nodes[cid]),
			"physics_3d_objects": int(_ctx_body3d[cid]),
			"physics_2d_objects": int(_ctx_body2d[cid]),
			"marks": _labels_for_frame(int(_frame_no[ix])),
		}
		if int(_ctx_dim[cid]) == 3:
			entry["camera3"] = [_r(cam.x), _r(cam.y), _r(cam.z)]
		worst.append(entry)
	out["worst"] = worst

	# Метки, попавшие в окно.
	var marks_out: Array = []
	for m in _marks:
		var f := int(m.get("frame", -1))
		if f >= first_frame and f <= last_frame:
			marks_out.append((m as Dictionary).duplicate(true))
	out["marks"] = marks_out

	if _cost_frames > 0:
		out["probe_cost_ms_avg"] = _r(float(_cost_total_usec) / float(_cost_frames) / 1000.0)
		out["probe_cost_ms_max"] = _r(float(_cost_max_usec) / 1000.0)

	return out


## Очистить буфер кадров, метки и счётчики стоимости.
func reset() -> void:
	_head = 0
	_count = 0
	_ctx_last = 0
	_last_ctx_usec = 0
	_last_delta_ms = 0.0
	_last_fps = 0.0
	_marks.clear()
	_cost_total_usec = 0
	_cost_max_usec = 0
	_cost_frames = 0
	if _cap > 0:
		_delta_ms.fill(0.0)
		_proc_ms.fill(0.0)
		_phys_ms.fill(0.0)
		_fps.fill(0.0)
		_frame_no.fill(0)
		_ctx_id.fill(0)


## Поставить метку на текущий кадр (попадёт в report().marks и в worst[].marks).
func mark(label: String) -> void:
	if _marks.size() >= MAX_MARKS:
		_marks.remove_at(0)
	_marks.append({
		"frame": Engine.get_process_frames(),
		"label": label,
		"delta_ms": _r(_last_delta_ms),
		"fps": _r(_last_fps),
	})


## Включить/выключить сбор. При false _process снят с обработки — нулевые накладные расходы.
func set_enabled(value: bool) -> void:
	enabled = value
	set_process(value)


## Текущий размер выборки в буфере.
func get_frame_count() -> int:
	return _count


# ---------------------------------------------------------------------------
# Сэмплирование
# ---------------------------------------------------------------------------

func _process(delta: float) -> void:
	if not enabled:
		return

	var t0 := Time.get_ticks_usec()

	var i := _head
	var d_ms := delta * 1000.0
	var proc_ms := float(Performance.get_monitor(Performance.TIME_PROCESS)) * 1000.0
	var phys_ms := float(Performance.get_monitor(Performance.TIME_PHYSICS_PROCESS)) * 1000.0
	var fps := float(Engine.get_frames_per_second())
	var fno := Engine.get_process_frames()

	_delta_ms[i] = d_ms
	_proc_ms[i] = proc_ms
	_phys_ms[i] = phys_ms
	_fps[i] = fps
	_frame_no[i] = fno

	# Снимок контекста: вне очереди на длинном кадре, иначе не чаще 0.5 с.
	if _count == 0 or d_ms > LONG_MS or (t0 - _last_ctx_usec) >= CTX_REFRESH_USEC:
		_capture_context(t0)

	_ctx_id[i] = _ctx_last

	_last_delta_ms = d_ms
	_last_fps = fps

	_head = (i + 1) % _cap
	if _count < _cap:
		_count += 1

	# Стоимость самого пробника (без стоимости _capture_context — она редкая).
	var spent := Time.get_ticks_usec() - t0
	_cost_total_usec += spent
	_cost_frames += 1
	if spent > _cost_max_usec:
		_cost_max_usec = spent


## Снять контекст текущего кадра в кольцо снимков.
func _capture_context(stamp_usec: int) -> void:
	var slot := (_ctx_last + 1) % CTX_CAPACITY
	if _count == 0:
		slot = 0

	var scene_path := ""
	var tree := get_tree()
	if tree != null:
		var cs: Node = tree.current_scene
		if cs != null:
			scene_path = cs.scene_file_path
			if scene_path.is_empty() and not cs.name.is_empty():
				scene_path = "<%s>" % String(cs.name)

	var cam := Vector3.ZERO
	var dim := 0
	var vp := get_viewport()
	if vp != null:
		if vp.has_method("get_camera_3d"):
			var c3: Camera3D = vp.get_camera_3d()
			if c3 != null:
				cam = c3.global_position
				dim = 3
		if dim == 0 and vp.has_method("get_camera_2d"):
			var c2: Camera2D = vp.get_camera_2d()
			if c2 != null:
				var p2 := c2.global_position
				cam = Vector3(p2.x, p2.y, 0.0)
				dim = 2

	# Число тел: берём оба монитора, 3D — основной для этого проекта.
	var b3d := int(Performance.get_monitor(Performance.PHYSICS_3D_ACTIVE_OBJECTS))
	var b2d := int(Performance.get_monitor(Performance.PHYSICS_2D_ACTIVE_OBJECTS))

	_ctx_scene[slot] = scene_path
	_ctx_cam[slot] = cam
	_ctx_dim[slot] = dim
	_ctx_nodes[slot] = int(Performance.get_monitor(Performance.OBJECT_NODE_COUNT))
	_ctx_body3d[slot] = b3d
	_ctx_body2d[slot] = b2d
	_ctx_stamp_usec[slot] = stamp_usec

	_ctx_last = slot
	_last_ctx_usec = stamp_usec


# ---------------------------------------------------------------------------
# Внутренние помощники
# ---------------------------------------------------------------------------

func _alloc(p_cap: int) -> void:
	_cap = maxi(1, p_cap)

	_delta_ms.resize(_cap)
	_proc_ms.resize(_cap)
	_phys_ms.resize(_cap)
	_fps.resize(_cap)
	_frame_no.resize(_cap)
	_ctx_id.resize(_cap)
	_delta_ms.fill(0.0)
	_proc_ms.fill(0.0)
	_phys_ms.fill(0.0)
	_fps.fill(0.0)
	_frame_no.fill(0)
	_ctx_id.fill(0)

	_ctx_scene.resize(CTX_CAPACITY)
	_ctx_cam.resize(CTX_CAPACITY)
	_ctx_dim.resize(CTX_CAPACITY)
	_ctx_nodes.resize(CTX_CAPACITY)
	_ctx_body3d.resize(CTX_CAPACITY)
	_ctx_body2d.resize(CTX_CAPACITY)
	_ctx_stamp_usec.resize(CTX_CAPACITY)
	_ctx_scene.fill("")
	_ctx_cam.fill(Vector3.ZERO)
	_ctx_dim.fill(0)
	_ctx_nodes.fill(0)
	_ctx_body3d.fill(0)
	_ctx_body2d.fill(0)
	_ctx_stamp_usec.fill(0)

	_head = 0
	_count = 0
	_ctx_last = 0


func _window_start(want: int) -> int:
	return (_head - want + _cap * 2) % _cap


## Номер элемента в отсортированном по возрастанию массиве для процентиля p (nearest-rank).
static func _percentile(sorted_vals: PackedFloat32Array, p: float) -> float:
	var n := sorted_vals.size()
	if n == 0:
		return 0.0
	var rank := int(ceil(p * 0.01 * float(n))) - 1
	if rank < 0:
		rank = 0
	if rank >= n:
		rank = n - 1
	return float(sorted_vals[rank])


func _labels_for_frame(frame_no: int) -> Array:
	var labels: Array = []
	for m in _marks:
		if int(m.get("frame", -1)) == frame_no:
			labels.append(String(m.get("label", "")))
	return labels


static func _r(v: float) -> float:
	if is_nan(v) or is_inf(v):
		return 0.0
	return snappedf(v, 0.001)
