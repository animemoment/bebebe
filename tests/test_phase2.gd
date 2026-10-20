@tool
extends McpTestSuite

func suite_name() -> String:
	return "phase2"

func test_phase2_csharp_architecture_suite():
	var runner_script = load("res://tests/Phase2StressTestRunner.cs")
	assert_true(runner_script != null, "Phase2StressTestRunner.cs script must load")
	var runner = runner_script.new()
	assert_true(runner != null, "Phase2StressTestRunner instance must be created")
	track(runner)
	
	var err = runner.RunAllTests()
	assert_eq(err, "", "All C# Phase 2 architectural tests must pass: " + str(err))
