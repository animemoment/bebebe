return {
	"engine": Engine.get_version_info().string,
	"fps": Engine.get_frames_per_second(),
	"scene_name": get_tree().current_scene.name,
	"scene_path": get_tree().current_scene.scene_file_path,
	"node_count": get_tree().get_node_count(),
	"autoloads": ["_mcp_game_helper", "McpInteractionServer"],
}