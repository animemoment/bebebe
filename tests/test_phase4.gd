@tool
extends McpTestSuite

func suite_name() -> String:
	return "phase4"

func test_phase4_csharp_architecture_suite():
	var runner_script = load("res://tests/Phase4StressTestRunner.cs")
	assert_true(runner_script != null, "Phase4StressTestRunner.cs script must load")
	var runner = runner_script.new()
	assert_true(runner != null, "Phase4StressTestRunner instance must be created")
	track(runner)
	
	var err = runner.RunAllTests()
	assert_eq(err, "", "All C# Phase 4 architectural tests must pass: " + str(err))
