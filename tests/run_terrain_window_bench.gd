@tool
extends SceneTree

## Headless-драйвер бенчмарка follow-окна террейна (dev-terrain-window, PLAN §117 F2).
## Сравнивает старый полный пересчёт 512×512 через WorldCellQuery.CellAt против
## новой сетки климата + инкрементального сдвига полосой 64×512.
## Run: godot --headless --path . --script tests/run_terrain_window_bench.gd

func _init() -> void:
	print("[GD] Running TerrainWindowBench...")
	var runner_script = load("res://tests/TerrainWindowBench.cs")
	if runner_script == null:
		printerr("[GD ERR] Failed to load tests/TerrainWindowBench.cs")
		quit(1)
		return

	var runner = runner_script.new()
	if runner == null:
		printerr("[GD ERR] Failed to instantiate TerrainWindowBench")
		quit(1)
		return

	var err: Variant = runner.Run("new,shift,verify")
	runner.free()
	if String(err) == "":
		print("[GD OK] Terrain window bench finished with Code 0.")
		quit(0)
		return

	printerr("[GD ERR] Terrain window bench failed: ", err)
	quit(1)
