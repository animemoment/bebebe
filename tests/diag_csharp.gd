@tool
extends SceneTree

## Диагностика: почему CSharpScript не резолвится в headless --script режиме.
## Run: godot --headless --path . --script tests/diag_csharp.gd

func _init() -> void:
	print("[DIAG] GodotSharp singleton: ", Engine.has_singleton("GodotSharp"))
	var s = load("res://tests/Phase3StressTestRunner.cs")
	print("[DIAG] old Phase3StressTestRunner.cs: ", s)
	if s != null:
		print("[DIAG] class=", s.get_class(), " has_new=", s.has_method("new"))
		if s.has_method("new"):
			var inst = s.call("new")
			print("[DIAG] instance=", inst)
			if inst != null:
				var r = inst.RunAllTests()
				print("[DIAG] RunAllTests -> ", r)
	var s2 = load("res://tests/TestGeneratorBench.cs")
	print("[DIAG] new test_generator_bench.cs: ", s2)
	if s2 != null:
		print("[DIAG] class=", s2.get_class(), " has_new=", s2.has_method("new"))
		if s2.has_method("new"):
			var inst2 = s2.call("new")
			print("[DIAG] instance=", inst2)
			if inst2 != null:
				var r2 = inst2.RunBenchmarks("cellat")
				print("[DIAG] RunBenchmarks -> ", r2)
	print("[DIAG] done")
	quit(0)
