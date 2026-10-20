@tool
extends McpTestSuite

func suite_name() -> String:
	return "phase5"

func test_phase5_csharp_architecture_suite():
	var runner_script = load("res://tests/Phase5StressTestRunner.cs")
	assert_true(runner_script != null, "Phase5StressTestRunner.cs script must load")
	var runner = runner_script.new()
	assert_true(runner != null, "Phase5StressTestRunner instance must be created")
	track(runner)
	
	var err = runner.RunAllTests()
	assert_eq(err, "", "All C# Phase 5 architectural tests must pass: " + str(err))
