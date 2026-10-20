@tool
extends McpTestSuite

func suite_name() -> String:
	return "phase1"

func test_phase1_csharp_stress_suite():
	var runner_script = load("res://tests/Phase1StressTestRunner.cs")
	assert_true(runner_script != null, "Phase1StressTestRunner.cs script must load")
	var runner = runner_script.new()
	assert_true(runner != null, "Phase1StressTestRunner instance must be created")
	track(runner)
	
	var err = runner.RunAllTests()
	assert_eq(err, "", "All C# Phase 1 concurrency and lifecycle tests must pass: " + str(err))

func test_main_menu_controller_scene():
	var scene = load("res://scenes/ui/menu.tscn")
	assert_true(scene != null, "menu.tscn must load successfully")
	var instance = scene.instantiate()
	track(instance)
	var play_btn = instance.find_child("Play", true, false)
	assert_true(play_btn != null, "Play button must exist in menu.tscn")
