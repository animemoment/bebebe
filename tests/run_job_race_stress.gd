@tool
extends SceneTree

## task-20 / P8-г: стресс-гонка агрегатов кулдаунов JobSpatialPyramid.
## Run: godot --headless --path . --script tests/run_job_race_stress.gd

func _init() -> void:
	print("[GD] Running C# TestJobRaceStress...")
	var test_script = load("res://tests/TestJobRaceStress.cs")
	if test_script == null:
		printerr("[GD ERR] Failed to load TestJobRaceStress.cs")
		quit(1)
		return

	var test = test_script.new()
	if test == null or not test.has_method("Run"):
		printerr("[GD ERR] TestJobRaceStress has no Run()")
		quit(1)
		return

	var res = test.Run()
	if res is bool and not res:
		printerr("[GD ERR] TestJobRaceStress reported failure!")
		quit(1)
		return

	print("[GD OK] TestJobRaceStress passed.")
	quit(0)
