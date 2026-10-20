@tool
extends "res://addons/godot_ai/handlers/command_handler.gd"

const ErrorCodes := preload("res://addons/godot_ai/utils/error_codes.gd")
const VariantSerializer := preload("res://addons/godot_ai/utils/variant_serializer.gd")

## Handles node creation and manipulation with undo/redo support.

## Cycle-2 extraction (PLAN.md §112.1): shared coercion lives in
## _value_coercion.gd, resource semantics + typed containers in
## _resource_values.gd. resource_handler.gd is no longer preloaded here —
## the mutual preload/load pair formed the dependency cycle.
const ValueCoercion := preload("res://addons/godot_ai/handlers/_value_coercion.gd")
const ResourceValues := preload("res://addons/godot_ai/handlers/_resource_values.gd")

var _undo_redo: EditorUndoRedoManager


func _init(undo_redo: EditorUndoRedoManager) -> void:
	_undo_redo = undo_redo


func create_node(params: Dictionary) -> Dictionary:
	var node_type: String = params.get("type", "")
	var node_name: String = params.get("name", "")
	var parent_path: String = params.get("parent_path", "")
	var scene_path: String = params.get("scene_path", "")

	var scene_check := McpScenePath.require_edited_scene(params.get("scene_file", ""))
	if scene_check.has("error"):
		return scene_check
	var scene_root: Node = scene_check.node

	var parent: Node = scene_root
	if not parent_path.is_empty():
		parent = McpScenePath.resolve(parent_path, scene_root)
		if parent == null:
			return ErrorCodes.make(ErrorCodes.NODE_NOT_FOUND, McpScenePath.format_parent_error(parent_path, scene_root))

	var new_node: Node

	if not scene_path.is_empty():
		# Scene instancing path — load and instantiate a PackedScene.
		# GEN_EDIT_STATE_INSTANCE makes the editor treat the result as a real
		# scene instance (foldout icon, the .tscn stores a reference instead of
		# an exploded subtree). Descendants remain owned by their sub-scene;
		# setting their owner to our scene_root would break the instance link.
		var scene_path_err = McpPathValidator.loadable_error(scene_path, "scene_path")
		if scene_path_err != null:
			return scene_path_err
		if not ResourceLoader.exists(scene_path):
			return ErrorCodes.make(ErrorCodes.RESOURCE_NOT_FOUND, "Scene not found: %s" % scene_path)
		var packed_scene = ResourceLoader.load(scene_path)
		if packed_scene == null or not packed_scene is PackedScene:
			return ErrorCodes.make(ErrorCodes.WRONG_TYPE, "Resource at %s is not a PackedScene" % scene_path)
		new_node = packed_scene.instantiate(PackedScene.GEN_EDIT_STATE_INSTANCE)
		if new_node == null:
			return ErrorCodes.make(ErrorCodes.INTERNAL_ERROR, "Failed to instantiate scene: %s" % scene_path)
	else:
		# ClassDB path — create by type.
		if node_type.is_empty():
			return ErrorCodes.make(ErrorCodes.MISSING_REQUIRED_PARAM, "Missing required param: type (or provide scene_path)")
		if not ClassDB.class_exists(node_type):
			return ErrorCodes.make(ErrorCodes.VALUE_OUT_OF_RANGE, "Unknown node type: %s" % node_type)
		if not ClassDB.is_parent_class(node_type, "Node"):
			return ErrorCodes.make(ErrorCodes.WRONG_TYPE, "%s is not a Node type" % node_type)
		new_node = ClassDB.instantiate(node_type)
		if new_node == null:
			return ErrorCodes.make(ErrorCodes.INTERNAL_ERROR, "Failed to instantiate %s" % node_type)

	if not node_name.is_empty():
		new_node.name = node_name

	_undo_redo.create_action("MCP: Create %s" % new_node.name)
	_undo_redo.add_do_method(parent, "add_child", new_node, true)
	_undo_redo.add_do_method(new_node, "set_owner", scene_root)
	_undo_redo.add_do_reference(new_node)
	_undo_redo.add_undo_method(parent, "remove_child", new_node)
	_undo_redo.commit_action()

	var response := {
		"name": new_node.name,
		"type": new_node.get_class(),
		"path": McpScenePath.from_node(new_node, scene_root),
		"parent_path": McpScenePath.from_node(parent, scene_root),
		"undoable": true,
	}
	if not scene_path.is_empty():
		response["scene_path"] = scene_path
	return {"data": response}


func delete_node(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path
	var scene_root: Node = resolved.scene_root

	var root_err := _reject_if_scene_root(node, scene_root, "delete")
	if root_err != null:
		return root_err

	var parent := node.get_parent()
	var idx := node.get_index()

	_undo_redo.create_action("MCP: Delete %s" % node.name)
	_undo_redo.add_do_method(parent, "remove_child", node)
	_undo_redo.add_undo_method(parent, "add_child", node, true)
	_undo_redo.add_undo_method(parent, "move_child", node, idx)
	_undo_redo.add_undo_method(node, "set_owner", scene_root)
	_undo_redo.add_undo_reference(node)
	_undo_redo.commit_action()

	return {
		"data": {
			"path": node_path,
			"undoable": true,
		}
	}


func reparent_node(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path
	var scene_root: Node = resolved.scene_root

	var new_parent_path: String = params.get("new_parent", "")
	if new_parent_path.is_empty():
		return ErrorCodes.make(ErrorCodes.MISSING_REQUIRED_PARAM, "Missing required param: new_parent")

	var new_parent := McpScenePath.resolve(new_parent_path, scene_root)
	if new_parent == null:
		return ErrorCodes.make(ErrorCodes.NODE_NOT_FOUND, McpScenePath.format_parent_error(new_parent_path, scene_root))

	var root_err := _reject_if_scene_root(node, scene_root, "reparent")
	if root_err != null:
		return root_err

	# Prevent reparenting a node to itself or to one of its own descendants.
	# Godot's `A.is_ancestor_of(B)` returns true iff B is a descendant of A, so
	# the direction here matters: we want `node.is_ancestor_of(new_parent)` to
	# catch "new_parent is below node in the tree" and thus would create a
	# cycle. The previous direction (`new_parent.is_ancestor_of(node)`) asked
	# the opposite question — whether we were trying to move a node to one of
	# its own ancestors — which is a perfectly valid operation. See issue #121.
	if node == new_parent or node.is_ancestor_of(new_parent):
		return ErrorCodes.make(ErrorCodes.INVALID_PARAMS, "Cannot reparent a node to itself or its descendant")

	var old_parent := node.get_parent()
	var old_idx := node.get_index()
	## Ported from upstream PR #927 at
	## 1a95bcca51d81d29de925c2f636814eaa037c1c2 (issue #904). Snapshot
	## descendants before commit: remove_child clears owner on any child whose
	## owner sits outside the pruned subtree, so both do and undo must restore
	## those owners as part of the recorded action.
	var descendants := _collect_descendants(node)

	_undo_redo.create_action("MCP: Reparent %s" % node.name)
	_undo_redo.add_do_method(old_parent, "remove_child", node)
	_undo_redo.add_do_method(new_parent, "add_child", node, true)
	_undo_redo.add_do_method(node, "set_owner", scene_root)
	for child in descendants:
		_undo_redo.add_do_method(child, "set_owner", scene_root)
	_undo_redo.add_do_reference(node)
	_undo_redo.add_undo_method(new_parent, "remove_child", node)
	_undo_redo.add_undo_method(old_parent, "add_child", node, true)
	_undo_redo.add_undo_method(old_parent, "move_child", node, old_idx)
	_undo_redo.add_undo_method(node, "set_owner", scene_root)
	for child in descendants:
		## Keep a null owner as null. Substituting scene_root would make an
		## intentionally unowned descendant scene-owned on undo (#904).
		var prior_owner: Node = child.owner
		_undo_redo.add_undo_method(child, "set_owner", prior_owner)
	_undo_redo.add_undo_reference(node)
	_undo_redo.commit_action()

	return {
		"data": {
			"path": McpScenePath.from_node(node, scene_root),
			"old_parent": McpScenePath.from_node(old_parent, scene_root),
			"new_parent": McpScenePath.from_node(new_parent, scene_root),
			"undoable": true,
		}
	}


func set_property(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path
	var scene_root: Node = resolved.scene_root

	var property: String = params.get("property", "")
	if property.is_empty():
		return ErrorCodes.make(ErrorCodes.MISSING_REQUIRED_PARAM, "Missing required param: property")

	if not "value" in params:
		return ErrorCodes.make(ErrorCodes.MISSING_REQUIRED_PARAM, "Missing required param: value")

	var value = params.get("value")

	var found := false
	var prop_type: int = TYPE_NIL
	for prop in node.get_property_list():
		if prop.name == property:
			found = true
			prop_type = prop.get("type", TYPE_NIL)
			break
	if not found:
		return ErrorCodes.make(ErrorCodes.PROPERTY_NOT_ON_CLASS, McpPropertyErrors.build_message(node, property))

	var old_value = node.get(property)
	# Prefer declared property type; fall back to runtime type for dynamic props
	# (scripted @export vars can report TYPE_NIL in the property list).
	var target_type: int = prop_type if prop_type != TYPE_NIL else typeof(old_value)

	var instantiated_resource := false

	# Some MCP clients (Cline) stringify the documented {"__class__": "BoxMesh", ...}
	# value before sending. Promote that string back to a Dictionary here so the
	# `__class__` branch below handles it, instead of the next branch treating
	# the JSON blob as a res:// path and emitting "Resource not found: {...}".
	# See #206.
	if target_type == TYPE_OBJECT and value is String and value.begins_with("{"):
		var json := JSON.new()
		if json.parse(value) == OK and json.data is Dictionary and (json.data as Dictionary).has("__class__"):
			value = json.data

	var nil_resource_string: bool = target_type == TYPE_NIL and (value == "" or (value is String and value.begins_with("res://")))
	var resource_string_value: bool = value is String and (target_type == TYPE_OBJECT or nil_resource_string)
	if resource_string_value:
		if value == "":
			value = null
		else:
			var value_path_err = McpPathValidator.loadable_error(value, "value")
			if value_path_err != null:
				return value_path_err
			if not ResourceLoader.exists(value):
				return ErrorCodes.make(ErrorCodes.RESOURCE_NOT_FOUND, "Resource not found: %s" % value)
			var loaded := ResourceLoader.load(value)
			if loaded == null:
				return ErrorCodes.make(ErrorCodes.RESOURCE_NOT_FOUND, "Resource not found: %s" % value)
			value = loaded
	elif target_type == TYPE_OBJECT and value is Dictionary and value.has("__class__"):
		# Shortcut: {"__class__": "BoxMesh", "size": {...}} instantiates a
		# fresh Resource subclass and applies the remaining keys as
		# properties. Mirrors resource_create's inline-assign path but
		# avoids a separate tool call for the common case.
		var type_str: String = value.get("__class__", "")
		var made := ResourceValues._instantiate_resource(type_str)
		if made is Dictionary:
			return made
		var res: Resource = made
		var remaining: Dictionary = (value as Dictionary).duplicate()
		remaining.erase("__class__")
		if not remaining.is_empty():
			var apply_err := ResourceValues._apply_resource_properties(res, remaining)
			if apply_err != null:
				return apply_err
		value = res
		instantiated_resource = true
	elif target_type == TYPE_ARRAY and old_value is Array and (old_value as Array).is_typed():
		## Typed Array[T] slot (#612): the generic TYPE_ARRAY passthrough
		## hands an untyped Array to Godot's typed setter, which rejects it
		## wholesale and leaves the slot at its default — with success still
		## reported. Route through the element-aware coercer instead; errors
		## name the offending element index.
		var typed_out: Variant = _coerce_typed_array(value, old_value)
		if typed_out is Dictionary:
			return typed_out
		value = typed_out
	elif (
		target_type == TYPE_DICTIONARY
		and old_value is Dictionary
		and (old_value as Dictionary).is_typed()
	):
		## Typed Dictionary[K, V] slot (#612 stage 3) — same silent-drop
		## family as typed arrays. A successful result is always a TYPED
		## Dictionary (a cleared duplicate of the slot), while the error
		## envelope is an untyped {"error": ...} — that's the discriminator
		## (a legit payload could contain an "error" key; typedness can't lie).
		var typed_dict_out: Dictionary = _coerce_typed_dictionary(value, old_value)
		if not typed_dict_out.is_typed():
			return typed_dict_out
		value = typed_dict_out
	else:
		value = _coerce_value(value, target_type)
		## Refuse any value that didn't land as the target compound Variant
		## — wrong-shape dict (#123) or non-dict input like list / JSON string
		## that used to silently default-construct Vector3.ZERO (#191).
		var coerce_err := _check_coerced(value, target_type)
		if coerce_err != null:
			return coerce_err

	_undo_redo.create_action("MCP: Set %s.%s" % [node.name, property])
	_undo_redo.add_do_property(node, property, value)
	_undo_redo.add_undo_property(node, property, old_value)
	if instantiated_resource:
		_undo_redo.add_do_reference(value)
	_undo_redo.commit_action()

	return {
		"data": {
			"path": node_path,
			"property": property,
			"value": _serialize_value(node.get(property)),
			"old_value": _serialize_value(old_value),
			"undoable": true,
		}
	}


func rename_node(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path
	var scene_root: Node = resolved.scene_root

	var new_name: String = params.get("new_name", "")
	if new_name.is_empty():
		return ErrorCodes.make(ErrorCodes.MISSING_REQUIRED_PARAM, "Missing required param: new_name")

	## The scene root's name is baked into the .tscn serialization and is
	## referenced by every NodePath that starts with `/<root>` (AnimationPlayer
	## tracks, RemoteTransform3D targets, exported NodePath @vars, etc.).
	## Renaming it silently breaks those references. The MCP tool's docstring
	## has always promised "Cannot rename the scene root" — enforce it. #122
	if node == scene_root:
		return ErrorCodes.make(ErrorCodes.INVALID_PARAMS, "Cannot rename the scene root")

	if new_name.validate_node_name() != new_name:
		return ErrorCodes.make(ErrorCodes.VALUE_OUT_OF_RANGE, "Invalid characters in name: %s" % new_name)

	var old_name := String(node.name)
	if old_name == new_name:
		return {
			"data": {
				"path": node_path,
				"name": new_name,
				"old_name": old_name,
				"unchanged": true,
				"undoable": false,
				"reason": "Name unchanged",
			}
		}

	var parent := node.get_parent()
	for sibling in parent.get_children():
		if sibling != node and String(sibling.name) == new_name:
			return ErrorCodes.make(ErrorCodes.INVALID_PARAMS, "A sibling already has the name '%s'" % new_name)

	_undo_redo.create_action("MCP: Rename %s to %s" % [old_name, new_name])
	_undo_redo.add_do_property(node, "name", new_name)
	_undo_redo.add_undo_property(node, "name", old_name)
	_undo_redo.commit_action()

	return {
		"data": {
			"path": McpScenePath.from_node(node, scene_root),
			"old_path": node_path,
			"name": String(node.name),
			"old_name": old_name,
			"undoable": true,
		}
	}


func duplicate_node(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path
	var scene_root: Node = resolved.scene_root

	var root_err := _reject_if_scene_root(node, scene_root, "duplicate")
	if root_err != null:
		return root_err

	var parent := node.get_parent()
	var dup: Node = node.duplicate()
	if dup == null:
		return ErrorCodes.make(ErrorCodes.INTERNAL_ERROR, "Failed to duplicate node")

	# Apply optional name
	var new_name: String = params.get("name", "")
	if not new_name.is_empty():
		dup.name = new_name

	## Ported from upstream PR #927 (issue #904). Record descendant owners
	## inside the action so redo restores them. Undo is just remove_child of
	## the copy; descendants live on `dup` via add_do_reference and do not need
	## their own undo set_owner.
	var descendants := _collect_descendants(dup)

	_undo_redo.create_action("MCP: Duplicate %s" % node.name)
	_undo_redo.add_do_method(parent, "add_child", dup, true)
	_undo_redo.add_do_method(dup, "set_owner", scene_root)
	for child in descendants:
		_undo_redo.add_do_method(child, "set_owner", scene_root)
	_undo_redo.add_do_reference(dup)
	_undo_redo.add_undo_method(parent, "remove_child", dup)
	_undo_redo.commit_action()

	return {
		"data": {
			"path": McpScenePath.from_node(dup, scene_root),
			"original_path": node_path,
			"name": dup.name,
			"type": dup.get_class(),
			"undoable": true,
		}
	}


func move_node(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path
	var scene_root: Node = resolved.scene_root

	var root_err := _reject_if_scene_root(node, scene_root, "reorder")
	if root_err != null:
		return root_err

	if not "index" in params:
		return ErrorCodes.make(ErrorCodes.MISSING_REQUIRED_PARAM, "Missing required param: index")

	var new_index: int = params.get("index", 0)
	var parent := node.get_parent()
	var old_index := node.get_index()
	var sibling_count := parent.get_child_count()

	if new_index < 0 or new_index >= sibling_count:
		return ErrorCodes.make(ErrorCodes.VALUE_OUT_OF_RANGE, "Index %d out of range (0..%d)" % [new_index, sibling_count - 1])

	_undo_redo.create_action("MCP: Move %s to index %d" % [node.name, new_index])
	_undo_redo.add_do_method(parent, "move_child", node, new_index)
	_undo_redo.add_undo_method(parent, "move_child", node, old_index)
	_undo_redo.commit_action()

	return {
		"data": {
			"path": node_path,
			"old_index": old_index,
			"new_index": new_index,
			"undoable": true,
		}
	}


func add_to_group(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path

	var group_value: Variant = params.get("group", "")
	var type_err := McpParamValidators.require_string("group", group_value)
	if type_err != null:
		return type_err
	var group := String(group_value)
	if group.is_empty():
		return ErrorCodes.make(ErrorCodes.MISSING_REQUIRED_PARAM, "Missing required param: group")

	if node.is_in_group(group):
		return {"data": {"path": node_path, "group": group, "already_member": true, "undoable": false, "reason": "No change made"}}

	_undo_redo.create_action("MCP: Add %s to group %s" % [node.name, group])
	_undo_redo.add_do_method(node, "add_to_group", group, true)
	_undo_redo.add_undo_method(node, "remove_from_group", group)
	_undo_redo.commit_action()

	return {
		"data": {
			"path": node_path,
			"group": group,
			"undoable": true,
		}
	}


func remove_from_group(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path

	var group_value: Variant = params.get("group", "")
	var type_err := McpParamValidators.require_string("group", group_value)
	if type_err != null:
		return type_err
	var group := String(group_value)
	if group.is_empty():
		return ErrorCodes.make(ErrorCodes.MISSING_REQUIRED_PARAM, "Missing required param: group")

	if not node.is_in_group(group):
		return {"data": {"path": node_path, "group": group, "not_member": true, "undoable": false, "reason": "Node not in group"}}

	_undo_redo.create_action("MCP: Remove %s from group %s" % [node.name, group])
	_undo_redo.add_do_method(node, "remove_from_group", group)
	_undo_redo.add_undo_method(node, "add_to_group", group, true)
	_undo_redo.commit_action()

	return {
		"data": {
			"path": node_path,
			"group": group,
			"undoable": true,
		}
	}


func set_selection(params: Dictionary) -> Dictionary:
	var paths: Array = params.get("paths", [])
	var _scene_check := McpNodeValidator.require_scene_or_error()
	if _scene_check.has("error"):
		return _scene_check
	var scene_root: Node = _scene_check.scene_root

	var selection := EditorInterface.get_selection()
	selection.clear()

	var selected: Array[String] = []
	var not_found: Array[String] = []
	for path_variant in paths:
		var path: String = str(path_variant)
		var node := McpScenePath.resolve(path, scene_root)
		if node:
			selection.add_node(node)
			selected.append(path)
		else:
			not_found.append(path)

	return {
		"data": {
			"selected": selected,
			"not_found": not_found,
			"count": selected.size(),
			"undoable": false,
			"reason": "Selection changes are not tracked in undo history",
		}
	}


## All descendants of `node` (not including `node` itself), depth-first.
## Used to record per-child set_owner inside an undo action without targeting
## the handler as an UndoRedo receiver (upstream PR #927 / issue #904).
static func _collect_descendants(node: Node) -> Array[Node]:
	var out: Array[Node] = []
	for child in node.get_children():
		out.append(child)
		out.append_array(_collect_descendants(child))
	return out


## --- Cycle-2 extraction delegates ---------------------------------------------
## The coercion and typed-container bodies that lived here moved VERBATIM to
## _value_coercion.gd (McpValueCoercion) and _resource_values.gd
## (McpResourceValues). These thin delegates keep set_property's dispatch and
## the external callers (texture_handler, curve_handler) working unchanged.
static func _coerce_value(value: Variant, target_type: int) -> Variant:
	return ValueCoercion._coerce_value(value, target_type)


static func _check_coerced(value: Variant, target_type: int, prefix: String = "") -> Variant:
	return ValueCoercion._check_coerced(value, target_type, prefix)


static func _coerce_typed_array(value: Variant, slot_value: Array, prefix: String = "") -> Variant:
	return ResourceValues._coerce_typed_array(value, slot_value, prefix)


static func _coerce_typed_dictionary(
	value: Variant, slot_value: Dictionary, prefix: String = ""
) -> Dictionary:
	return ResourceValues._coerce_typed_dictionary(value, slot_value, prefix)


func get_node_properties(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path
	var scene_root: Node = resolved.scene_root

	# Optional token-reducing filter: `fields` restricts the response to a
	# named subset. Defaults off (empty), so existing callers see the full
	# dump unchanged. The MCP tool types this as a list, but batch_execute and
	# raw callers bypass that, so validate the shape here before iterating.
	var fields_param: Variant = params.get("fields", [])
	if fields_param == null:
		fields_param = []
	if not (fields_param is Array):
		return ErrorCodes.make(
			ErrorCodes.INVALID_PARAMS,
			"'fields' must be an array of property names, got %s (%s)" % [
				type_string(typeof(fields_param)), str(fields_param),
			],
		)
	var field_filter := {}
	for f in fields_param:
		## Property names are strings on the wire; anything else is a
		## malformed filter (e.g. [123] or [["fov"]]) — reject rather than
		## silently stringify into a filter that matches nothing (#123/#126:
		## strict within the accepted shape). StringName is allowed for
		## editor-side callers.
		if not (f is String or f is StringName):
			return ErrorCodes.make(
				ErrorCodes.INVALID_PARAMS,
				"'fields' elements must be property-name strings, got %s in %s" % [
					type_string(typeof(f)), str(fields_param),
				],
			)
		field_filter[str(f)] = true
	var use_field_filter := not field_filter.is_empty()

	var properties: Array[Dictionary] = []
	var editor_property_count := 0
	var matched_fields := {}
	for prop in node.get_property_list():
		var usage: int = prop.get("usage", 0)
		if not (usage & PROPERTY_USAGE_EDITOR):
			continue
		editor_property_count += 1
		if use_field_filter:
			if not field_filter.has(prop.name):
				continue
			matched_fields[prop.name] = true
		# Null reads are values, not omissions: `script` on an unscripted node
		# and unset Resource slots (mesh, material, …) read back null and must
		# appear as "value": null with their declared type, so callers can tell
		# "Object-typed, currently unset" from "doesn't exist" (#771).
		properties.append({
			"name": prop.name,
			"type": type_string(prop.type),
			"value": _serialize_value(node.get(prop.name)),
		})
	# Requested names that matched no editor-visible property — distinguishes
	# "you asked for something that doesn't exist" from "exists and is null".
	var unknown_fields: Array[String] = []
	for f in field_filter:
		if not matched_fields.has(f):
			unknown_fields.append(f)
	return {
		"data": {
			"path": node_path,
			"node_type": node.get_class(),
			"properties": properties,
			"count": properties.size(),
			# Total editor-visible properties before field filtering, so a
			# caller that passed `fields` knows how many were withheld.
			# Invariant: an unfiltered call returns every editor-visible
			# property, so count == total_count; only the `fields` filter
			# can make count < total_count.
			"total_count": editor_property_count,
			"unknown_fields": unknown_fields,
		}
	}


func get_children(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path
	var scene_root: Node = resolved.scene_root

	var children: Array[Dictionary] = []
	for child in node.get_children():
		children.append({
			"name": child.name,
			"type": child.get_class(),
			"path": McpScenePath.from_node(child, scene_root),
			"children_count": child.get_child_count(),
		})
	return {
		"data": {
			"parent_path": node_path,
			"children": children,
			"count": children.size(),
		}
	}


func get_groups(params: Dictionary) -> Dictionary:
	var resolved := _resolve_node(params)
	if resolved.has("error"):
		return resolved
	var node: Node = resolved.node
	var node_path: String = resolved.path

	var groups: Array[String] = []
	for group in node.get_groups():
		# Skip internal groups (start with underscore)
		if not str(group).begins_with("_"):
			groups.append(str(group))
	return {
		"data": {
			"path": node_path,
			"groups": groups,
			"count": groups.size(),
		}
	}


## Validate path param, resolve to node. Returns dict with node/path/scene_root
## on success, or an error dict (has "error" key) on failure. Thin wrapper
## around the shared `McpNodeValidator.resolve_or_error` helper (audit-v2 #20).
func _resolve_node(params: Dictionary) -> Dictionary:
	return McpNodeValidator.resolve_or_error(
		params.get("path", ""), "path", params.get("scene_file", ""),
	)


## Reject operations targeting the scene root. Returns an INVALID_PARAMS error
## dict with "Cannot <op> the scene root", or null if `node` is not the root.
static func _reject_if_scene_root(node: Node, scene_root: Node, op: String) -> Variant:
	if node == scene_root:
		return ErrorCodes.make(ErrorCodes.INVALID_PARAMS, "Cannot %s the scene root" % op)
	return null


## Convert a Godot Variant to a JSON-safe value. Compound geometry types
## (AABB, Rect2, Transforms, …) and packed arrays serialize as structured
## dicts/arrays so agents can inspect fields instead of parsing Godot's
## debug repr — see issue #214.
static func _serialize_value(value: Variant) -> Variant:
	return VariantSerializer.serialize(value)
