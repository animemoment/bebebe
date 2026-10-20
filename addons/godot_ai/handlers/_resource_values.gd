@tool
class_name McpResourceValues
extends RefCounted

## Resource instantiation, resource-property application, and typed
## Array[T] / Dictionary[K, V] filling.
##
## Cycle-2 extraction (see PLAN.md §112.1): the bodies below moved VERBATIM
## from node_handler.gd and resource_handler.gd, which used to preload each
## other for exactly these helpers. This module owns the resource-semantics
## half; pure value coercion lives in _value_coercion.gd (McpValueCoercion).
## The old cross-handler recursion (apply → typed array → object element →
## apply) now stays INSIDE this single module. Dependencies: ErrorCodes,
## McpValueCoercion, McpPathValidator, ClassDB/ProjectSettings — no handler
## references in either direction.

const ErrorCodes := preload("res://addons/godot_ai/utils/error_codes.gd")
const ValueCoercion := preload("res://addons/godot_ai/handlers/_value_coercion.gd")


## Validate that `type_str` names a concrete Resource subclass that we can
## instantiate. Returns an error dict on failure, or null on success.
static func _validate_resource_class(type_str: String) -> Variant:
	if not ClassDB.class_exists(type_str):
		return ErrorCodes.make(ErrorCodes.VALUE_OUT_OF_RANGE, "Unknown resource type: %s" % type_str)
	if ClassDB.is_parent_class(type_str, "Node"):
		return ErrorCodes.make(
			ErrorCodes.WRONG_TYPE,
			"%s is a Node type, not a Resource — use node_create instead" % type_str
		)
	if not ClassDB.is_parent_class(type_str, "Resource"):
		var parent := ClassDB.get_parent_class(type_str)
		return ErrorCodes.make(
			ErrorCodes.WRONG_TYPE,
			"%s is not a Resource type (extends %s)" % [type_str, parent]
		)
	if not ClassDB.can_instantiate(type_str):
		return ErrorCodes.make(
			ErrorCodes.WRONG_TYPE,
			"%s is abstract and cannot be instantiated — use a concrete subclass (e.g. BoxMesh, BoxShape3D, StyleBoxFlat)" % type_str
		)
	return null


## Build the "Unknown resource type" error with a steer toward op="scan". A type
## that reaches here is neither an engine built-in (ClassDB) nor a registered
## project class (the global script-class registry). In an agent-driven workflow
## the most common cause is a `class_name` script just made via script_create
## that isn't registered yet — the global class table only rebuilds on a
## filesystem scan (normally an editor-focus event). Point the caller at the one
## cheap call that fixes that, so it doesn't fall back to a full plugin reload.
## See #614 for the headless scan op.
static func _unknown_resource_type_error(type_str: String) -> Dictionary:
	return ErrorCodes.make(
		ErrorCodes.VALUE_OUT_OF_RANGE,
		(
			"Unknown resource type: %s — not an engine built-in or a registered project class. "
			+ "If you just created it with script_create, the global class table is stale until a "
			+ "scan: call filesystem_manage(op=\"scan\"), then retry. Otherwise check the spelling."
		) % type_str
	)


## Resolve a resource type name to a fresh instance. Handles engine built-ins
## (ClassDB) and project `class_name` Resources (the global script-class
## registry). Returns a Resource on success, or an error dict on failure.
static func _instantiate_resource(type_str: String) -> Variant:
	if ClassDB.class_exists(type_str):
		var class_err: Variant = _validate_resource_class(type_str)
		if class_err != null:
			return class_err
		var built_in := ClassDB.instantiate(type_str)
		if built_in == null or not (built_in is Resource):
			return ErrorCodes.make(ErrorCodes.INTERNAL_ERROR, "Failed to instantiate %s as a Resource" % type_str)
		return built_in
	for entry in ProjectSettings.get_global_class_list():
		if entry.get("class", "") == type_str:
			var script_path: String = entry.get("path", "")
			var scr: Variant = load(script_path)
			# Reject non-Resource script classes BEFORE constructing them:
			# scr.new() runs _init(), and an @tool class_name extending a
			# non-RefCounted type (e.g. Node) would otherwise build — and leak —
			# an orphan instance this path never frees. get_instance_base_type()
			# resolves to the native base, so multi-level custom Resource
			# hierarchies (B extends A extends Resource) still pass.
			var base_or_err: Variant = _script_base_type_or_error(scr, type_str, script_path)
			if base_or_err is Dictionary:
				return base_or_err
			var base_type: StringName = base_or_err
			if not ClassDB.is_parent_class(base_type, "Resource"):
				return ErrorCodes.make(ErrorCodes.WRONG_TYPE, "%s is not a Resource type (extends %s)" % [type_str, base_type])
			if not scr.can_instantiate():
				return ErrorCodes.make(ErrorCodes.WRONG_TYPE, "%s cannot be instantiated in the editor (abstract, or a non-@tool script — add @tool to instantiate it here)" % type_str)
			# Reject scripts whose _init() requires arguments BEFORE scr.new():
			# scr.new() passes no args, so a required-arg _init raises and aborts
			# this handler mid-call, null-cascading into a generic "malformed
			# result" error instead of a clean rejection. get_script_method_list()
			# reports the effective (incl. inherited) _init; required args =
			# args - default_args. Statically detectable only — a _init that runs
			# but throws still falls through to scr.new() and the dispatcher catch.
			for method in scr.get_script_method_list():
				if method.get("name", "") == "_init":
					var required_args: int = (method.get("args", []) as Array).size() - (method.get("default_args", []) as Array).size()
					if required_args > 0:
						return ErrorCodes.make(ErrorCodes.WRONG_TYPE, "%s cannot be instantiated: its _init() requires arguments" % type_str)
					break
			var made: Variant = scr.new()
			if made == null or not (made is Resource):
				return ErrorCodes.make(ErrorCodes.INTERNAL_ERROR, "Failed to instantiate %s as a Resource" % type_str)
			return made
	return _unknown_resource_type_error(type_str)


## Maximum nesting depth for the {"__class__": ...} sub-resource shortcut.
## Caller-supplied dicts recurse through _apply_resource_properties; without a
## cap a deeply nested payload overflows the GDScript call stack and crashes
## the editor (#536). 32 is far beyond any legitimate sub-resource chain.
const MAX_NESTED_RESOURCE_DEPTH := 32


## Apply a dict of property values to a freshly-instantiated Resource,
## reusing McpValueCoercion's coercion so Vector3/Color/etc. dicts land typed.
## Returns null on success or an error dict on failure.
## `depth` is internal recursion bookkeeping for the nested {"__class__": ...}
## shortcut — external callers use the default of 0.
static func _apply_resource_properties(res: Resource, properties: Dictionary, depth: int = 0) -> Variant:
	if depth > MAX_NESTED_RESOURCE_DEPTH:
		return ErrorCodes.make(
			ErrorCodes.INVALID_PARAMS,
			"Nested resource properties exceed the maximum depth of %d — flatten the {\"__class__\": ...} nesting or create the deep sub-resources in separate calls" % MAX_NESTED_RESOURCE_DEPTH
		)
	var prop_types := {}
	for prop in res.get_property_list():
		prop_types[prop.name] = prop.get("type", TYPE_NIL)
	for key in properties.keys():
		if not prop_types.has(key):
			var valid: Array[String] = []
			for prop in res.get_property_list():
				if prop.get("usage", 0) & PROPERTY_USAGE_EDITOR:
					valid.append(prop.name)
			valid.sort()
			# Name the script's class_name (e.g. MyTestResource) rather than the
			# native base (Resource) so the hint names the type the agent created,
			# and point at the real MCP verb — resource_manage(op="get_info") now
			# answers for project class_name Resources too.
			var type_label := res.get_class()
			var res_script: Variant = res.get_script()
			if res_script is Script and not String(res_script.get_global_name()).is_empty():
				type_label = String(res_script.get_global_name())
			var err := ErrorCodes.make(
				ErrorCodes.PROPERTY_NOT_ON_CLASS,
				"Property '%s' not found on %s. Call resource_manage(op=\"get_info\", params={\"type\": \"%s\"}) to list available properties." % [key, type_label, type_label]
			)
			err["error"]["data"] = {"valid_properties": valid}
			return err
		var target_type: int = prop_types[key]
		if target_type == TYPE_NIL:
			target_type = typeof(res.get(key))
		var v = properties[key]
		if target_type == TYPE_OBJECT and v is String:
			if v == "":
				v = null
			else:
				var vpath_err = McpPathValidator.loadable_error(v, "property '%s'" % key)
				if vpath_err != null:
					return vpath_err
				var loaded := ResourceLoader.load(v)
				if loaded == null:
					return ErrorCodes.make(
						ErrorCodes.INVALID_PARAMS,
						"Resource not found at path '%s' for property '%s'" % [v, key]
					)
				v = loaded
		elif target_type == TYPE_OBJECT and v is Dictionary and v.has("__class__"):
			# Nested shortcut: the same {"__class__": "X", ...} form that
			# node_handler.set_property accepts, now also supported here so
			# resource_create/environment_create callers can populate
			# sub-resource slots (ShaderMaterial.shader, etc.) in one shot.
			var sub_type: String = v.get("__class__", "")
			# Resolve via the shared helper so the nested shortcut accepts both
			# engine built-ins (ClassDB) and project `class_name` Resources,
			# exactly like the top-level resource_create path.
			var sub_made := _instantiate_resource(sub_type)
			if sub_made is Dictionary:
				# Preserve the property-slot context the inline path used to add.
				sub_made["error"]["message"] = "%s (for property '%s')" % [sub_made["error"]["message"], key]
				return sub_made
			var sub_res: Resource = sub_made
			var remaining: Dictionary = (v as Dictionary).duplicate()
			remaining.erase("__class__")
			if not remaining.is_empty():
				var nested_err := _apply_resource_properties(sub_res, remaining, depth + 1)
				if nested_err != null:
					return nested_err
			v = sub_res
		else:
			var slot_value: Variant = res.get(key)
			if target_type == TYPE_ARRAY and slot_value is Array and (slot_value as Array).is_typed():
				## Typed Array[T] slot (#612): mirror set_property's dispatch —
				## the generic passthrough would hand an untyped Array to the
				## typed setter, which drops it silently while we report success.
				var typed_out: Variant = _coerce_typed_array(
					v, slot_value, "Property '%s'" % key
				)
				if typed_out is Dictionary:
					return typed_out
				v = typed_out
			elif (
				target_type == TYPE_DICTIONARY
				and slot_value is Dictionary
				and (slot_value as Dictionary).is_typed()
			):
				## Typed Dictionary[K, V] slot (#612 stage 3): success is a
				## typed duplicate of the slot; the error envelope is untyped.
				var typed_dict_out: Dictionary = _coerce_typed_dictionary(
					v, slot_value, "Property '%s'" % key
				)
				if not typed_dict_out.is_typed():
					return typed_dict_out
				v = typed_dict_out
			else:
				v = ValueCoercion._coerce_value(v, target_type)
				## Mirror set_property's coerce check: wrong-shape dicts (#123) and
				## non-dict inputs that don't land as the target compound Variant
				## (#191) both error here instead of writing zero-filled Variants.
				var coerce_err := ValueCoercion._check_coerced(v, target_type, "Property '%s'" % key)
				if coerce_err != null:
					return coerce_err
		res.set(key, v)
	return null


## Resolve a loaded global-class script to its native base type, or an error if
## the script failed to load (not a Script) or to compile (empty base type).
## Shared by the create and get_info custom-Resource paths so both report a
## compile failure rather than a misleading "is not a Resource type (extends )".
static func _script_base_type_or_error(scr: Variant, type_str: String, script_path: String) -> Variant:
	if not (scr is Script):
		return ErrorCodes.make(ErrorCodes.INTERNAL_ERROR, "Failed to load script class %s from %s" % [type_str, script_path])
	var base_type: StringName = scr.get_instance_base_type()
	if String(base_type).is_empty():
		return ErrorCodes.make(ErrorCodes.INTERNAL_ERROR, "%s failed to compile or parse (script %s)" % [type_str, script_path])
	return base_type


## Fill a typed `Array[T]` slot from a JSON list (#612 stage 1: value-element
## types). `slot_value` is the property's current typed Array — Godot's getter
## returns the (possibly empty) typed container, which carries the element
## type, so no PROPERTY_HINT_TYPE_STRING parsing is needed. Elements coerce
## one at a time through the existing `_coerce_value` / `_check_coerced`
## pair, then bulk-move via `Array.assign()` with a post-assign size check,
## so a wrong element can never silently drop the write: it errors naming
## the element index. Returns the filled typed Array on success, or a
## `make(...)`-shaped error Dictionary (callers discriminate on
## `result is Dictionary` — a successful result is always an Array).
##
## Object elements (Array[Texture2D], Array[MyResource], ...) coerce per
## element through `_coerce_object_element` (#612 stage 2), mirroring the
## single-slot TYPE_OBJECT paths: res:// strings load, {"__class__": ...}
## instantiates, and each landed element is conformance-checked against the
## slot's element class/script so a wrong-class Resource errors naming the
## index instead of being rejected wholesale by `assign`.
static func _coerce_typed_array(value: Variant, slot_value: Array, prefix: String = "") -> Variant:
	var elem_label := _typed_array_element_label(slot_value)
	if not (value is Array):
		var err := ErrorCodes.make(
			ErrorCodes.WRONG_TYPE,
			"Cannot write %s to a typed Array[%s] property; expected a list" % [
				type_string(typeof(value)), elem_label,
			],
		)
		return ErrorCodes.prefix_message(err, prefix)
	var elem_type := slot_value.get_typed_builtin()
	var staging: Array = []
	var in_list: Array = value
	for i in in_list.size():
		var elem_prefix := ("element %d" % i) if prefix.is_empty() else "%s element %d" % [prefix, i]
		var coerced: Variant
		if elem_type == TYPE_OBJECT:
			coerced = _coerce_object_element(in_list[i], elem_prefix)
			if coerced is Dictionary:
				## Object elements are never legit Dictionaries (a dict input
				## is either {"__class__"} — consumed above — or an error), so
				## a Dictionary return is unambiguously the error envelope.
				return coerced
			if coerced != null and not _object_element_conforms(coerced, slot_value):
				var conform_err := ErrorCodes.make(
					ErrorCodes.WRONG_TYPE,
					"element is %s, which is not a %s" % [
						(coerced as Object).get_class(), elem_label,
					],
				)
				return ErrorCodes.prefix_message(conform_err, elem_prefix)
		else:
			coerced = ValueCoercion._coerce_value(in_list[i], elem_type)
			var elem_err := ValueCoercion._check_coerced(coerced, elem_type, elem_prefix)
			if elem_err != null:
				return elem_err
			if coerced == null:
				## Prefix with `elem_prefix` (which already folds in `prefix`),
				## not `prefix` again — the latter double-stamped the property
				## context (PR #682 review). Object arrays allow null entries
				## (Godot typed object arrays store null); value-type arrays
				## don't.
				var null_err := ErrorCodes.make(
					ErrorCodes.WRONG_TYPE,
					"cannot store null in Array[%s]" % elem_label,
				)
				return ErrorCodes.prefix_message(null_err, elem_prefix)
		staging.append(coerced)
	var out := slot_value.duplicate()
	out.clear()
	out.assign(staging)
	if out.size() != staging.size():
		## Backstop for element shapes `_check_coerced` waves through but the
		## typed container still rejects — `assign` loud-rejects and leaves a
		## short array, which without this check would be a partial write.
		var assign_err := ErrorCodes.make(
			ErrorCodes.WRONG_TYPE,
			"Array[%s] element conversion failed during assign (%d of %d elements landed)" % [
				elem_label, out.size(), staging.size(),
			],
		)
		return ErrorCodes.prefix_message(assign_err, prefix)
	return out


## "int" / "Vector3" / "Texture2D" / "MyItemData" — element-type name of a
## typed Array slot, for error messages.
static func _typed_array_element_label(slot_value: Array) -> String:
	if slot_value.get_typed_builtin() == TYPE_OBJECT:
		return _object_type_label(slot_value.get_typed_class_name(), slot_value.get_typed_script())
	return type_string(slot_value.get_typed_builtin())


## Coerce one element of an object-typed Array (#612 stage 2). Mirrors the
## single-slot TYPE_OBJECT paths in set_property: a res:// path string loads
## the Resource; {"__class__": "X", ...} (including the #206 stringified
## form) instantiates via _instantiate_resource and applies the remaining
## keys; "" / null store a null entry (typed object arrays allow them).
## Returns the Object (or null), or a make(...)-shaped error Dictionary with
## `elem_prefix` already folded in.
static func _coerce_object_element(elem: Variant, elem_prefix: String) -> Variant:
	if elem == null:
		return null
	if elem is Object:
		return elem
	if elem is String and (elem as String).begins_with("{"):
		var json := JSON.new()
		if json.parse(elem) == OK and json.data is Dictionary and (json.data as Dictionary).has("__class__"):
			elem = json.data
	if elem is String:
		if String(elem).is_empty():
			return null
		var path_err = McpPathValidator.loadable_error(elem, "value")
		if path_err != null:
			return ErrorCodes.prefix_message(path_err, elem_prefix)
		if not ResourceLoader.exists(elem):
			return ErrorCodes.prefix_message(
				ErrorCodes.make(ErrorCodes.RESOURCE_NOT_FOUND, "Resource not found: %s" % elem),
				elem_prefix,
			)
		var loaded := ResourceLoader.load(elem)
		if loaded == null:
			return ErrorCodes.prefix_message(
				ErrorCodes.make(ErrorCodes.RESOURCE_NOT_FOUND, "Resource not found: %s" % elem),
				elem_prefix,
			)
		return loaded
	if elem is Dictionary and (elem as Dictionary).has("__class__"):
		var type_str: String = (elem as Dictionary).get("__class__", "")
		var made := _instantiate_resource(type_str)
		if made is Dictionary:
			return ErrorCodes.prefix_message(made, elem_prefix)
		var res: Resource = made
		var remaining: Dictionary = (elem as Dictionary).duplicate()
		remaining.erase("__class__")
		if not remaining.is_empty():
			var apply_err: Variant = _apply_resource_properties(res, remaining)
			if apply_err != null:
				return ErrorCodes.prefix_message(apply_err, elem_prefix)
		return res
	return ErrorCodes.prefix_message(
		ErrorCodes.make(
			ErrorCodes.WRONG_TYPE,
			'cannot convert %s to an Object element; pass a res:// path or {"__class__": ...}'
			% type_string(typeof(elem)),
		),
		elem_prefix,
	)


## True when `elem` satisfies the object-typed slot's element constraint —
## script type when the slot is Array[MyScriptClass], native class
## otherwise. Checked per element so a wrong-class Resource errors naming the
## index instead of being rejected wholesale by `Array.assign()`.
static func _object_element_conforms(elem: Object, slot_value: Array) -> bool:
	return _object_conforms(elem, slot_value.get_typed_class_name(), slot_value.get_typed_script())


## Shared class/script conformance predicate for typed Array elements and
## typed Dictionary values (#612 stages 2–3).
static func _object_conforms(elem: Object, cls_name: StringName, script: Variant) -> bool:
	if script is Script:
		return is_instance_of(elem, script)
	var cls := String(cls_name)
	return cls.is_empty() or elem.is_class(cls)


## Fill a typed `Dictionary[K, V]` slot from a JSON object (#612 stage 3).
## `slot_value` is the property's current typed Dictionary — the getter
## returns the (possibly empty) typed container carrying both constraint
## sides. Keys coerce via `_coerce_typed_dict_key` (JSON object keys are
## always Strings, so int/float/StringName key slots parse the string and
## fail closed on anything inexact); values mirror the typed-array element
## rules — object values through `_coerce_object_element` + conformance,
## everything else through `_coerce_value`/`_check_coerced`. Never partial:
## any bad key or value errors naming the key and nothing is written.
##
## Returns the filled TYPED Dictionary on success or an UNTYPED
## `make(...)`-shaped error Dictionary — callers discriminate on
## `is_typed()`, since a success result is always a duplicate of the typed
## slot and error envelopes are plain dicts.
static func _coerce_typed_dictionary(
	value: Variant, slot_value: Dictionary, prefix: String = ""
) -> Dictionary:
	var label := _typed_dictionary_label(slot_value)
	if not (value is Dictionary):
		var err := ErrorCodes.make(
			ErrorCodes.WRONG_TYPE,
			"Cannot write %s to a typed %s property; expected an object" % [
				type_string(typeof(value)), label,
			],
		)
		return ErrorCodes.prefix_message(err, prefix)
	var key_type := slot_value.get_typed_key_builtin() if slot_value.is_typed_key() else TYPE_NIL
	var value_type := (
		slot_value.get_typed_value_builtin() if slot_value.is_typed_value() else TYPE_NIL
	)
	var out := slot_value.duplicate()
	out.clear()
	var in_dict: Dictionary = value
	for raw_key in in_dict.keys():
		var key_prefix := (
			'key "%s"' % str(raw_key) if prefix.is_empty()
			else '%s key "%s"' % [prefix, str(raw_key)]
		)
		var key: Variant = _coerce_typed_dict_key(raw_key, key_type, label, key_prefix)
		if key is Dictionary:
			## Scalar-only key coercion never returns a legit Dictionary key,
			## so a Dictionary here is unambiguously the error envelope.
			return key
		var raw_value: Variant = in_dict[raw_key]
		var coerced: Variant
		if value_type == TYPE_NIL:
			## Untyped value side (e.g. Dictionary[String, Variant]).
			coerced = raw_value
		elif value_type == TYPE_OBJECT:
			coerced = _coerce_object_element(raw_value, key_prefix)
			if coerced is Dictionary:
				return coerced
			if coerced != null and not _object_conforms(
				coerced,
				slot_value.get_typed_value_class_name(),
				slot_value.get_typed_value_script(),
			):
				var conform_err := ErrorCodes.make(
					ErrorCodes.WRONG_TYPE,
					"value is %s, which is not a %s" % [
						(coerced as Object).get_class(),
						_object_type_label(
							slot_value.get_typed_value_class_name(),
							slot_value.get_typed_value_script(),
						),
					],
				)
				return ErrorCodes.prefix_message(conform_err, key_prefix)
		else:
			coerced = ValueCoercion._coerce_value(raw_value, value_type)
			var value_err := ValueCoercion._check_coerced(coerced, value_type, key_prefix)
			if value_err != null:
				return value_err
			if coerced == null:
				var null_err := ErrorCodes.make(
					ErrorCodes.WRONG_TYPE,
					"cannot store null as a %s value in %s" % [type_string(value_type), label],
				)
				return ErrorCodes.prefix_message(null_err, key_prefix)
		out[key] = coerced
	if out.size() != in_dict.size():
		## Two input keys collapsing onto one coerced key ("1" and "01" both
		## parse to int 1) would silently lose an entry — refuse instead.
		var collide_err := ErrorCodes.make(
			ErrorCodes.WRONG_TYPE,
			"%s keys collide after coercion (%d of %d entries landed)" % [
				label, out.size(), in_dict.size(),
			],
		)
		return ErrorCodes.prefix_message(collide_err, prefix)
	return out


## Coerce one JSON-object key onto a typed Dictionary's key slot. JSON keys
## are always Strings, so int/float/StringName key types accept exactly the
## strings that parse cleanly; everything else fails closed naming the key.
## Object/compound key types are unreachable from JSON and refuse loudly.
static func _coerce_typed_dict_key(
	raw_key: Variant, key_type: int, label: String, key_prefix: String
) -> Variant:
	if key_type == TYPE_NIL or typeof(raw_key) == key_type:
		return raw_key
	if raw_key is String:
		var key_str := raw_key as String
		match key_type:
			TYPE_STRING_NAME:
				return StringName(key_str)
			TYPE_INT:
				if key_str.is_valid_int():
					return int(key_str)
			TYPE_FLOAT:
				if key_str.is_valid_float():
					return float(key_str)
		## String key that didn't parse: JSON object keys are ALWAYS strings,
		## so blaming the String-ness would imply the caller could somehow
		## send a non-string key — name the expected key type instead.
		var parse_err := ErrorCodes.make(
			ErrorCodes.WRONG_TYPE,
			"key does not parse as %s (the %s key type)" % [type_string(key_type), label],
		)
		return ErrorCodes.prefix_message(parse_err, key_prefix)
	if raw_key is float and key_type == TYPE_INT and is_equal_approx(raw_key, roundf(raw_key)):
		## Whole JSON numbers arrive as floats through some non-JSON callers.
		return int(raw_key)
	var err := ErrorCodes.make(
		ErrorCodes.WRONG_TYPE,
		"cannot use %s as a %s key" % [type_string(typeof(raw_key)), label],
	)
	return ErrorCodes.prefix_message(err, key_prefix)


## "Dictionary[String, int]" / "Dictionary[int, Texture2D]" — for error
## messages. Untyped sides read as Variant.
static func _typed_dictionary_label(slot_value: Dictionary) -> String:
	var key_label := "Variant"
	if slot_value.is_typed_key():
		key_label = (
			_object_type_label(
				slot_value.get_typed_key_class_name(), slot_value.get_typed_key_script()
			)
			if slot_value.get_typed_key_builtin() == TYPE_OBJECT
			else type_string(slot_value.get_typed_key_builtin())
		)
	var value_label := "Variant"
	if slot_value.is_typed_value():
		value_label = (
			_object_type_label(
				slot_value.get_typed_value_class_name(), slot_value.get_typed_value_script()
			)
			if slot_value.get_typed_value_builtin() == TYPE_OBJECT
			else type_string(slot_value.get_typed_value_builtin())
		)
	return "Dictionary[%s, %s]" % [key_label, value_label]


## Class/script display name for an object-typed constraint side.
static func _object_type_label(cls_name: StringName, script: Variant) -> String:
	var cls := String(cls_name)
	if script is Script and not String((script as Script).get_global_name()).is_empty():
		cls = String((script as Script).get_global_name())
	return cls if not cls.is_empty() else "Object"
