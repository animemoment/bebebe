extends SceneTree

## Cycle-2 regression smoke test (PLAN.md §112.1 / §112.6).
## The coercion + resource-semantics bodies moved verbatim from
## node_handler.gd / resource_handler.gd into _value_coercion.gd
## (McpValueCoercion) and _resource_values.gd (McpResourceValues).
## This exercises the moved code end-to-end headlessly: coercion semantics,
## typed containers, resource instantiation/application, the nested
## {"__class__": ...} shortcut, the thin backward-compat delegates, and the
## absence of the old load cycle (both handlers load in either order).
## Run: godot --headless --path . --script addons/godot_ai/tests/test_cycle2_coercion.gd

const ValueCoercion := preload("res://addons/godot_ai/handlers/_value_coercion.gd")
const ResourceValues := preload("res://addons/godot_ai/handlers/_resource_values.gd")
const NodeHandler := preload("res://addons/godot_ai/handlers/node_handler.gd")
const ResourceHandler := preload("res://addons/godot_ai/handlers/resource_handler.gd")

var failures: int = 0


func _check(label: String, cond: bool) -> void:
	if cond:
		print("PASS  ", label)
	else:
		failures += 1
		print("FAIL  ", label)


func _initialize() -> void:
	# --- Load-order cycle check: both handlers must load in either order. ---
	var rh_first: Variant = load("res://addons/godot_ai/handlers/resource_handler.gd")
	var nh_first: Variant = load("res://addons/godot_ai/handlers/node_handler.gd")
	_check("no load cycle (resource_handler then node_handler)", rh_first != null and nh_first != null)
	var nh_again: Variant = load("res://addons/godot_ai/handlers/node_handler.gd")
	var rh_again: Variant = load("res://addons/godot_ai/handlers/resource_handler.gd")
	_check("no load cycle (node_handler then resource_handler)", nh_again != null and rh_again != null)

	# --- McpValueCoercion: coercion semantics (moved from node_handler). ---
	var v3: Variant = ValueCoercion._coerce_value({"x": 1.0, "y": 2.0, "z": 3.0}, TYPE_VECTOR3)
	_check("coerce dict->Vector3", v3 is Vector3 and v3 == Vector3(1.0, 2.0, 3.0))
	var bad_shape: Variant = ValueCoercion._coerce_value({"x": 1.0}, TYPE_VECTOR3)
	var shape_err: Variant = ValueCoercion._check_coerced(bad_shape, TYPE_VECTOR3, "prop")
	_check("wrong-shape dict flags via _check_coerced", shape_err is Dictionary)
	var ok_err: Variant = ValueCoercion._check_coerced(v3, TYPE_VECTOR3, "prop")
	_check("coerced Vector3 passes _check_coerced", ok_err == null)
	var packed: Variant = ValueCoercion._coerce_value([{"x": 1, "y": 2}], TYPE_PACKED_VECTOR2_ARRAY)
	_check("coerce list->PackedVector2Array", packed is PackedVector2Array and packed.size() == 1)
	var fstr: Variant = ValueCoercion._coerce_value("4.0", TYPE_FLOAT)
	_check("coerce stringified float (#964)", fstr is float and fstr == 4.0)
	var hint: String = ValueCoercion._shape_hint(TYPE_VECTOR2)
	_check("_shape_hint for Vector2", hint == "{\"x\":0,\"y\":0}")

	# --- McpResourceValues: typed containers (moved from node_handler,
	# --- used to reach back through resource_handler's _get_node_handler). ---
	var slot_arr: Array[int] = [7]
	var typed_arr: Variant = ResourceValues._coerce_typed_array([1, 2, 3], slot_arr)
	_check("typed Array[int] coercion", typed_arr is Array and (typed_arr as Array).size() == 3)
	var bad_arr: Variant = ResourceValues._coerce_typed_array([1, "nope"], slot_arr, "prop")
	_check("typed Array[int] rejects bad element", bad_arr is Dictionary)
	var slot_dict: Dictionary[String, int] = {"seed": 1}
	var typed_dict: Dictionary = ResourceValues._coerce_typed_dictionary({"seed": 2, "extra": 3}, slot_dict, "prop")
	_check("typed Dictionary[String,int] coercion", typed_dict.is_typed() and typed_dict.size() == 2 and typed_dict.get("seed") == 2)
	var bad_dict: Dictionary = ResourceValues._coerce_typed_dictionary({"seed": "x"}, slot_dict, "prop")
	_check("typed Dictionary rejects bad value", not bad_dict.is_typed())

	# --- McpResourceValues: resource semantics (moved from resource_handler). ---
	var grad: Variant = ResourceValues._instantiate_resource("Gradient")
	_check("instantiate engine Gradient", grad is Gradient)
	var node_type: Variant = ResourceValues._instantiate_resource("Node")
	_check("Node type rejected", node_type is Dictionary)
	var unknown: Variant = ResourceValues._instantiate_resource("DefinitelyNotAType")
	_check("unknown type returns error envelope", unknown is Dictionary)
	var apply_err: Variant = ResourceValues._apply_resource_properties(grad as Gradient, {"offsets": [0.0, 1.0]})
	_check("apply_resource_properties happy path", apply_err == null and (grad as Gradient).offsets.size() == 2)
	var apply_bad: Variant = ResourceValues._apply_resource_properties(grad as Gradient, {"no_such_prop": 1})
	_check("apply_resource_properties unknown prop errors", apply_bad is Dictionary)

	# --- Nested {"__class__": ...} shortcut through the typed-object path
	# --- (the exact recursion that used to cross the handler cycle). ---
	var gt: Variant = ResourceValues._instantiate_resource("GradientTexture1D")
	var nested_err: Variant = ResourceValues._apply_resource_properties(
		gt as GradientTexture1D,
		{"gradient": {"__class__": "Gradient", "offsets": [0.0, 0.5, 1.0]}}
	)
	_check(
		"nested __class__ shortcut fills sub-resource",
		nested_err == null and (gt as GradientTexture1D).gradient != null and (gt as GradientTexture1D).gradient.offsets.size() == 3
	)

	# --- Thin delegates: external callers keep working unchanged. ---
	var dv: Variant = NodeHandler._coerce_value({"x": 1, "y": 2}, TYPE_VECTOR2)
	_check("NodeHandler._coerce_value delegate", dv is Vector2)
	var de: Variant = NodeHandler._check_coerced({"x": 1}, TYPE_VECTOR2)
	_check("NodeHandler._check_coerced delegate", de is Dictionary)
	var da: Variant = NodeHandler._coerce_typed_array([1, 2], slot_arr)
	_check("NodeHandler._coerce_typed_array delegate", da is Array and (da as Array).size() == 2)
	var dd: Dictionary = NodeHandler._coerce_typed_dictionary({"seed": 5}, slot_dict)
	_check("NodeHandler._coerce_typed_dictionary delegate", dd.is_typed() and dd.get("seed") == 5)
	var ri: Variant = ResourceHandler._instantiate_resource("Gradient")
	_check("ResourceHandler._instantiate_resource delegate", ri is Gradient)
	var re: Variant = ResourceHandler._apply_resource_properties(ri as Gradient, {"offsets": [0.5]})
	_check("ResourceHandler._apply_resource_properties delegate", re == null and (ri as Gradient).offsets.size() == 1)
	var rue: Variant = ResourceHandler._unknown_resource_type_error("Nope")
	_check("ResourceHandler._unknown_resource_type_error delegate", rue is Dictionary)

	print("")
	if failures == 0:
		print("ALL CYCLE-2 SMOKE TESTS PASSED")
		quit(0)
	else:
		print("%d CYCLE-2 SMOKE TEST(S) FAILED" % failures)
		quit(1)
