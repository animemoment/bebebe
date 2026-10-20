@tool
class_name McpValueCoercion
extends RefCounted

## Pure value coercion between JSON-shaped Variants and Godot typed slots.
##
## Cycle-2 extraction (see PLAN.md §112.1): the bodies below moved VERBATIM
## from node_handler.gd. This leaf module owns no handler state and no
## resource semantics — only the shared static coercion/check helpers used
## by node_handler, _resource_values.gd, and the validation handlers
## (curve_handler, texture_handler). Dependencies: ErrorCodes + the global
## McpJsonValues parser — nothing else, by design.

const ErrorCodes := preload("res://addons/godot_ai/utils/error_codes.gd")


## Canonical dict-key sets for dict→Variant coercion. Alpha on `COLOR_KEYS`
## is optional — the coercer defaults it to 1.0 when absent.
const VECTOR2_KEYS: Array[String] = ["x", "y"]
const VECTOR3_KEYS: Array[String] = ["x", "y", "z"]
const VECTOR4_KEYS: Array[String] = ["x", "y", "z", "w"]
const COLOR_KEYS: Array[String] = ["r", "g", "b"]


## End-to-end coerce check for compound JSON-shaped targets
## (Vector2/Vector3/Color). Returns a full `make(...)`-shaped error dict
## if `value` didn't land as the target Variant after `_coerce_value`,
## else null. Wrong-shape dicts get the `_check_dict_coerce_failed`
## message (expected-vs-got keys); non-dict inputs (Array, String,
## primitive) name the received type and a JSON shape hint. No-op for
## non-compound targets — Godot's setter handles those.
##
## Used by set_property, resource_handler, and validation handlers
## (curve, texture). Issue #191 — passing a list, JSON string, or
## anything else to a Vector3 property used to silently store
## Vector3.ZERO; this gates that path.
static func _check_coerced(value: Variant, target_type: int, prefix: String = "") -> Variant:
	var ok := false
	match target_type:
		TYPE_VECTOR2:
			ok = value is Vector2
		TYPE_VECTOR3:
			ok = value is Vector3
		TYPE_COLOR:
			ok = value is Color
		TYPE_PACKED_VECTOR2_ARRAY:
			ok = value is PackedVector2Array
		TYPE_PACKED_VECTOR3_ARRAY:
			ok = value is PackedVector3Array
		TYPE_PACKED_VECTOR4_ARRAY:
			ok = value is PackedVector4Array
		TYPE_PACKED_COLOR_ARRAY:
			ok = value is PackedColorArray
		TYPE_PACKED_INT32_ARRAY:
			ok = value is PackedInt32Array
		TYPE_PACKED_INT64_ARRAY:
			ok = value is PackedInt64Array
		TYPE_PACKED_FLOAT32_ARRAY:
			ok = value is PackedFloat32Array
		TYPE_PACKED_FLOAT64_ARRAY:
			ok = value is PackedFloat64Array
		TYPE_PACKED_STRING_ARRAY:
			ok = value is PackedStringArray
		TYPE_VECTOR2I: ok = value is Vector2i
		TYPE_VECTOR3I: ok = value is Vector3i
		TYPE_VECTOR4: ok = value is Vector4
		TYPE_VECTOR4I: ok = value is Vector4i
		TYPE_QUATERNION: ok = value is Quaternion
		TYPE_RECT2: ok = value is Rect2
		TYPE_RECT2I: ok = value is Rect2i
		TYPE_AABB: ok = value is AABB
		TYPE_PLANE: ok = value is Plane
		TYPE_BASIS: ok = value is Basis
		TYPE_TRANSFORM2D: ok = value is Transform2D
		TYPE_TRANSFORM3D: ok = value is Transform3D
		TYPE_PROJECTION: ok = value is Projection
		_:
			# null / untyped-TYPE_NIL / already-correct-type are handled by
			# Godot's setter; anything else would silently no-op, so error.
			if value == null or target_type == TYPE_NIL or typeof(value) == target_type:
				return null
			var unsupported := ErrorCodes.make(
				ErrorCodes.WRONG_TYPE,
				"Cannot write %s to a %s property; godot-ai has no coercion for that type" % [
					type_string(typeof(value)), type_string(target_type),
				],
			)
			return ErrorCodes.prefix_message(unsupported, prefix)
	if ok:
		return null
	var dict_err := _check_dict_coerce_failed(value, target_type)
	if dict_err != null:
		return ErrorCodes.prefix_message(dict_err, prefix)
	## Wording stays neutral on shape — `_shape_hint` already produces a
	## dict-shaped string for Vector2/3/Color and a list-shaped one for
	## the Packed*Array slots. The old "expected a dict like [...]" phrasing
	## read self-contradictory for packed targets (PR #424 review).
	var err := ErrorCodes.make(
		ErrorCodes.WRONG_TYPE,
		"Cannot coerce %s to %s; expected %s" % [
			type_string(typeof(value)), type_string(target_type), _shape_hint(target_type),
		],
	)
	return ErrorCodes.prefix_message(err, prefix)


## Build a "{\"x\":1,...}" hint string from the canonical key constants
## so adding a key (e.g. Vector4) only touches VECTORN_KEYS. Packed*Array
## targets short-circuit to a literal list-shaped hint.
static func _shape_hint(target_type: int) -> String:
	match target_type:
		TYPE_PACKED_VECTOR2_ARRAY:
			return "[{\"x\":0,\"y\":0}, ...]"
		TYPE_PACKED_VECTOR3_ARRAY:
			return "[{\"x\":0,\"y\":0,\"z\":0}, ...]"
		TYPE_PACKED_VECTOR4_ARRAY:
			return "[{\"x\":0,\"y\":0,\"z\":0,\"w\":0}, ...]"
		TYPE_PACKED_COLOR_ARRAY:
			return "[{\"r\":0,\"g\":0,\"b\":0,\"a\":1}, ...]"
		TYPE_PACKED_INT32_ARRAY, TYPE_PACKED_INT64_ARRAY:
			return "[int, ...]"
		TYPE_PACKED_FLOAT32_ARRAY, TYPE_PACKED_FLOAT64_ARRAY:
			return "[float, ...]"
		TYPE_PACKED_STRING_ARRAY:
			return "[\"...\", ...]"
		TYPE_VECTOR2I:
			return "{\"x\":0,\"y\":0}"
		TYPE_VECTOR3I:
			return "{\"x\":0,\"y\":0,\"z\":0}"
		TYPE_VECTOR4, TYPE_VECTOR4I, TYPE_QUATERNION:
			return "{\"x\":0,\"y\":0,\"z\":0,\"w\":0}"
		TYPE_RECT2, TYPE_RECT2I, TYPE_AABB:
			return "{\"position\":{...},\"size\":{...}}"
		TYPE_PLANE:
			return "{\"normal\":{...},\"d\":0}"
		TYPE_BASIS:
			return "{\"x\":{...},\"y\":{...},\"z\":{...}}"
		TYPE_TRANSFORM2D:
			return "{\"x\":{...},\"y\":{...},\"origin\":{...}}"
		TYPE_TRANSFORM3D:
			return "{\"basis\":{...},\"origin\":{...}}"
		TYPE_PROJECTION:
			return "{\"x\":{...},\"y\":{...},\"z\":{...},\"w\":{...}}"
	var keys: Array[String] = []
	match target_type:
		TYPE_VECTOR2: keys = VECTOR2_KEYS
		TYPE_VECTOR3: keys = VECTOR3_KEYS
		TYPE_COLOR: keys = COLOR_KEYS
	var pairs: Array[String] = []
	for k in keys:
		pairs.append("\"%s\":0" % k)
	return "{" + ",".join(pairs) + "}"


## Detect a failed dict→typed-Variant coercion. Returns an INVALID_PARAMS
## error dict if `value` is still a Dictionary after a coercion attempt
## targeting a Vector2/Vector3/Color slot, else null. Message names the
## expected keys and the keys actually received so agents self-correct
## on the next retry.
static func _check_dict_coerce_failed(value: Variant, target_type: int) -> Variant:
	if not (value is Dictionary):
		return null
	var expected: Array[String] = []
	var type_name := ""
	match target_type:
		TYPE_VECTOR2:
			expected = VECTOR2_KEYS
			type_name = "Vector2"
		TYPE_VECTOR3:
			expected = VECTOR3_KEYS
			type_name = "Vector3"
		TYPE_COLOR:
			expected = COLOR_KEYS
			type_name = "Color"
		_:
			return null
	var got_keys: Array = (value as Dictionary).keys()
	return ErrorCodes.make(
		ErrorCodes.WRONG_TYPE,
		"Cannot coerce dict to %s: expected keys %s; got %s" % [type_name, str(expected), str(got_keys)]
	)


## Coerce JSON-shaped values into Godot Variants when the target property
## type is known. Returns the coerced value on success, or the input
## unchanged on failure — callers detect the type mismatch via an
## `is <Type>` check (curve_handler, texture_handler) or via the
## `_check_dict_coerce_failed` helper (set_property, resource_handler).
##
## Dictionary→Vector2/Vector3/Color cases REQUIRE all canonical keys;
## wrong-shape dicts flow through unchanged. See issue #123 — previous
## `dict.get(key, 0)` defaults silently zero-filled missing axes.
static func _coerce_value(value: Variant, target_type: int) -> Variant:
	match target_type:
		## Vector2/Vector3/Color route through the canonical strict parser
		## (#714): same dict/array/string shapes as every other handler, and
		## non-numeric components fall through (returning the original
		## value) so _check_coerced flags them instead of crashing a typed
		## constructor or silently writing black/zeros.
		TYPE_VECTOR2:
			var v2 = McpJsonValues.parse_vector2(value)
			if v2 != null:
				return v2
		TYPE_VECTOR3:
			var v3 = McpJsonValues.parse_vector3(value)
			if v3 != null:
				return v3
		TYPE_COLOR:
			var col = McpJsonValues.parse_color(value)
			if col != null:
				return col
		TYPE_BOOL:
			if value is float or value is int:
				return bool(value)
		TYPE_INT:
			if value is float:
				return int(value)
		TYPE_FLOAT:
			if value is int:
				return float(value)
			if value is String:
				## #964: some MCP clients stringify float arguments ("4.0").
				## Accept strictly-numeric strings; unparseable ones flow
				## through unchanged so _check_coerced raises the typed
				## WRONG_TYPE error instead of a silent zero/null write.
				var parsed: Variant = McpJsonValues.parse_float(value)
				if parsed != null:
					return parsed
		TYPE_STRING_NAME:
			if value is String:
				return StringName(value)
		TYPE_NODE_PATH:
			if value is String:
				return NodePath(value)
			if value == null:
				return NodePath()
		TYPE_OBJECT:
			# Resource loading is handled in set_property so we can return a
			# typed error; here we only pass through cleared values.
			if value == null:
				return null
		TYPE_ARRAY:
			if value is Array:
				return value
		TYPE_DICTIONARY:
			if value is Dictionary:
				return value
		TYPE_PACKED_VECTOR2_ARRAY:
			if value is Array:
				var out := PackedVector2Array()
				for item in value:
					if item is Vector2:
						out.append(item)
					elif item is Dictionary and item.has_all(VECTOR2_KEYS):
						out.append(Vector2(item["x"], item["y"]))
					else:
						return value  # leave for _check_coerced to flag
				return out
		TYPE_PACKED_VECTOR3_ARRAY:
			if value is Array:
				var out := PackedVector3Array()
				for item in value:
					if item is Vector3:
						out.append(item)
					elif item is Dictionary and item.has_all(VECTOR3_KEYS):
						out.append(Vector3(item["x"], item["y"], item["z"]))
					else:
						return value
				return out
		TYPE_PACKED_VECTOR4_ARRAY:
			if value is Array:
				var out := PackedVector4Array()
				for item in value:
					if item is Vector4:
						out.append(item)
					elif item is Dictionary and item.has_all(VECTOR4_KEYS):
						out.append(Vector4(item["x"], item["y"], item["z"], item["w"]))
					else:
						return value
				return out
		TYPE_PACKED_COLOR_ARRAY:
			if value is Array:
				var out := PackedColorArray()
				for item in value:
					if item is Color:
						out.append(item)
					elif item is Dictionary and item.has_all(COLOR_KEYS):
						out.append(Color(item["r"], item["g"], item["b"], item.get("a", 1.0)))
					elif item is String:
						out.append(Color(item))
					else:
						return value
				return out
		TYPE_PACKED_INT32_ARRAY, TYPE_PACKED_INT64_ARRAY:
			if value is Array:
				var out: Variant = PackedInt32Array() if target_type == TYPE_PACKED_INT32_ARRAY else PackedInt64Array()
				for item in value:
					if item is int or item is float:
						out.append(int(item))
					else:
						return value
				return out
		TYPE_PACKED_FLOAT32_ARRAY, TYPE_PACKED_FLOAT64_ARRAY:
			if value is Array:
				var out: Variant = PackedFloat32Array() if target_type == TYPE_PACKED_FLOAT32_ARRAY else PackedFloat64Array()
				for item in value:
					if item is float or item is int:
						out.append(float(item))
					else:
						return value
				return out
		TYPE_PACKED_STRING_ARRAY:
			if value is Array:
				var out := PackedStringArray()
				for item in value:
					if item is String:
						out.append(item)
					else:
						return value
				return out
		TYPE_VECTOR2I:
			if value is Dictionary and value.has_all(VECTOR2_KEYS):
				return Vector2i(int(value["x"]), int(value["y"]))
		TYPE_VECTOR3I:
			if value is Dictionary and value.has_all(VECTOR3_KEYS):
				return Vector3i(int(value["x"]), int(value["y"]), int(value["z"]))
		TYPE_VECTOR4:
			if value is Dictionary and value.has_all(VECTOR4_KEYS):
				return Vector4(value["x"], value["y"], value["z"], value["w"])
		TYPE_VECTOR4I:
			if value is Dictionary and value.has_all(VECTOR4_KEYS):
				return Vector4i(int(value["x"]), int(value["y"]), int(value["z"]), int(value["w"]))
		TYPE_QUATERNION:
			if value is Dictionary and value.has_all(VECTOR4_KEYS):
				return Quaternion(value["x"], value["y"], value["z"], value["w"])
		TYPE_RECT2:
			if value is Dictionary and value.has("position") and value.has("size"):
				var p: Variant = _coerce_value(value["position"], TYPE_VECTOR2)
				var s: Variant = _coerce_value(value["size"], TYPE_VECTOR2)
				if p is Vector2 and s is Vector2:
					return Rect2(p, s)
		TYPE_RECT2I:
			if value is Dictionary and value.has("position") and value.has("size"):
				var p: Variant = _coerce_value(value["position"], TYPE_VECTOR2I)
				var s: Variant = _coerce_value(value["size"], TYPE_VECTOR2I)
				if p is Vector2i and s is Vector2i:
					return Rect2i(p, s)
		TYPE_AABB:
			if value is Dictionary and value.has("position") and value.has("size"):
				var p: Variant = _coerce_value(value["position"], TYPE_VECTOR3)
				var s: Variant = _coerce_value(value["size"], TYPE_VECTOR3)
				if p is Vector3 and s is Vector3:
					return AABB(p, s)
		TYPE_PLANE:
			if value is Dictionary and value.has("normal") and value.has("d"):
				var n: Variant = _coerce_value(value["normal"], TYPE_VECTOR3)
				if n is Vector3:
					return Plane(n, float(value["d"]))
		TYPE_BASIS:
			if value is Dictionary and value.has_all(["x", "y", "z"]):
				var bx: Variant = _coerce_value(value["x"], TYPE_VECTOR3)
				var by: Variant = _coerce_value(value["y"], TYPE_VECTOR3)
				var bz: Variant = _coerce_value(value["z"], TYPE_VECTOR3)
				if bx is Vector3 and by is Vector3 and bz is Vector3:
					return Basis(bx, by, bz)
		TYPE_TRANSFORM2D:
			if value is Dictionary and value.has_all(["x", "y", "origin"]):
				var tx: Variant = _coerce_value(value["x"], TYPE_VECTOR2)
				var ty: Variant = _coerce_value(value["y"], TYPE_VECTOR2)
				var to_: Variant = _coerce_value(value["origin"], TYPE_VECTOR2)
				if tx is Vector2 and ty is Vector2 and to_ is Vector2:
					return Transform2D(tx, ty, to_)
		TYPE_TRANSFORM3D:
			if value is Dictionary and value.has("basis") and value.has("origin"):
				var b: Variant = _coerce_value(value["basis"], TYPE_BASIS)
				var o: Variant = _coerce_value(value["origin"], TYPE_VECTOR3)
				if b is Basis and o is Vector3:
					return Transform3D(b, o)
		TYPE_PROJECTION:
			if value is Dictionary and value.has_all(VECTOR4_KEYS):
				var px: Variant = _coerce_value(value["x"], TYPE_VECTOR4)
				var py: Variant = _coerce_value(value["y"], TYPE_VECTOR4)
				var pz: Variant = _coerce_value(value["z"], TYPE_VECTOR4)
				var pw: Variant = _coerce_value(value["w"], TYPE_VECTOR4)
				if px is Vector4 and py is Vector4 and pz is Vector4 and pw is Vector4:
					return Projection(px, py, pz, pw)
		# PackedByteArray intentionally unhandled — needs design decision
		# (base64 string vs. raw int list); JSON has no native byte type.
	return value
