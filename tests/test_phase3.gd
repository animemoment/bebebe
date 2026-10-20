@tool
extends McpTestSuite

func suite_name() -> String:
	return "phase3"

func test_phase3_csharp_architecture_suite():
	var runner_script = load("res://tests/Phase3StressTestRunner.cs")
	assert_true(runner_script != null, "Phase3StressTestRunner.cs script must load")
	var runner = runner_script.new()
	assert_true(runner != null, "Phase3StressTestRunner instance must be created")
	track(runner)
	
	var err = runner.RunAllTests()
	assert_eq(err, "", "All C# Phase 3 architectural tests must pass: " + str(err))
