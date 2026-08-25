## PropertyCollector — Сбор всех @export и [Export] переменных сцены
## Использует нативный get_property_list(), одинаково поддерживая GDScript и C#
extends RefCounted

var _editor_interface: EditorInterface

func _init(p_editor_interface: EditorInterface) -> void:
	_editor_interface = p_editor_interface

func collect_exported_properties() -> Dictionary:
	var root: Node = _editor_interface.get_edited_scene_root()
	if not root:
		return {}
	var result: Dictionary = {}
	_collect_recursive(root, root, result)
	return result

func _collect_recursive(node: Node, root: Node, result: Dictionary) -> void:
	var path: String = _get_node_path_relative(node, root)
	var props: Array = _get_exported_props(node)
	if props.size() > 0:
		result[path] = {
			"node": node,
			"properties": props
		}
	for child in node.get_children():
		_collect_recursive(child, root, result)

func _get_node_path_relative(node: Node, root: Node) -> String:
	if node == root:
		return str(node.name)
	var root_path: String = str(root.get_path())
	var node_path: String = str(node.get_path())
	if not node_path.begins_with(root_path + "/"):
		return str(node.name)
	return node_path.substr(root_path.length() + 1)

## Получение экспортированных свойств узла через нативный get_property_list
func _get_exported_props(node: Node) -> Array:
	var script = node.get_script()
	if not script:
		return []

	var result: Array = []
	var prop_list: Array = node.get_property_list()
	var current_group: String = ""
	var current_subgroup: String = ""

	for p in prop_list:
		var usage: int = p.get("usage", 0)
		var prop_name: String = p.get("name", "")

		# Отслеживание групп и подгрупп
		if usage & PROPERTY_USAGE_GROUP:
			current_group = prop_name
			current_subgroup = ""
			continue
		elif usage & PROPERTY_USAGE_SUBGROUP:
			current_subgroup = prop_name
			continue

		# Свойство считается экспортированным, если оно выставлено в инспектор (EDITOR + STORAGE)
		# либо помечено как SCRIPT_VARIABLE с отображением в редакторе
		var is_exported: bool = (usage & PROPERTY_USAGE_EDITOR) != 0 and (usage & PROPERTY_USAGE_STORAGE) != 0

		# Исключаем встроенные внутренние свойства узлов движка
		if is_exported and not _is_built_in_property(prop_name):
			if prop_name in node:
				result.append({
					"name": prop_name,
					"value": node.get(prop_name),
					"type": p.get("type", TYPE_NIL),
					"group": current_group,
					"subgroup": current_subgroup,
				})

	return result

func _is_built_in_property(name: String) -> bool:
	# Исключение стандартных метаданных и системных полей Node
	return name.begins_with("script") or name.begins_with("_") or name in [
		"process_mode", "process_priority", "process_physics_priority",
		"editor_description", "visible", "modulate", "self_modulate",
		"show_behind_parent", "top_level", "clip_children", "light_mask",
		"visibility_layer", "z_index", "z_as_relative", "y_sort_enabled",
		"texture_filter", "texture_repeat", "material", "use_parent_material"
	]