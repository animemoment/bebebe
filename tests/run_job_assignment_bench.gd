@tool
extends SceneTree

## task-14 / P8а: бенчмарк распределителя работ «ДО» (точка сравнения с §119).
## Run: godot --headless --path . --script tests/run_job_assignment_bench.gd

func _init() -> void:
	print("[GD] Running C# BenchJobAssignment...")
	var test_script = load("res://tests/BenchJobAssignment.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load BenchJobAssignment.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null or not test.has_method("Run"):
		printerr("[GD ERR] BenchJobAssignment has no Run()")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] BenchJobAssignment reported failure!")
		quit(1)
		return

	print("[GD OK] BenchJobAssignment passed.")
	quit(0)
