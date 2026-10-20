@tool
extends McpTestSuite

func suite_name() -> String:
	return "phase0"

func test_garden_beds_terrain_configuration():
	var scene = load("res://scenes/main/Main.tscn")
	assert_true(scene != null, "Main.tscn must load successfully")
	var instance = scene.instantiate()
	assert_true(instance != null, "Main.tscn must instantiate")
	track(instance)
	
	var garden_beds = instance.find_child("GardenBeds", true, false)
	assert_true(garden_beds != null, "GardenBeds node must exist in Main.tscn")
	assert_true(garden_beds is TileMapLayer, "GardenBeds must be a TileMapLayer")
	
	var tile_set = garden_beds.tile_set
	assert_true(tile_set != null, "GardenBeds must have an assigned TileSet")
	assert_true(tile_set.get_terrain_sets_count() > 0, "TileSet must define at least one terrain set")
	assert_true(tile_set.get_terrains_count(0) > 0, "Terrain set 0 must define at least one terrain")
	
	var source = tile_set.get_source(2)
	assert_true(source != null, "Source 2 must exist in GardenBeds TileSet")
	assert_true(source is TileSetAtlasSource, "Source 2 must be TileSetAtlasSource")
	
	# Verify that tile 0:0 has terrain set = 0 and terrain = 0
	var tile_data = source.get_tile_data(Vector2i(0, 0), 0)
	assert_true(tile_data != null, "Tile 0:0 must exist")
	assert_eq(tile_data.terrain_set, 0, "Tile 0:0 must belong to terrain_set 0")
	assert_eq(tile_data.terrain, 0, "Tile 0:0 must belong to terrain 0")

func test_hud_exported_property_in_main_scene():
	var scene = load("res://scenes/main/Main.tscn")
	var instance = scene.instantiate()
	track(instance)
	
	var hud_prop = instance.get("HUD")
	assert_true(hud_prop != null, "Main root must have HUD property assigned (F-017 resolved)")

func test_build_menu_placeholder_buttons_hidden():
	var scene = load("res://scenes/ui/Build.tscn")
	assert_true(scene != null, "Build.tscn must load successfully")
	var instance = scene.instantiate()
	track(instance)
	
	var hbox2 = instance.find_child("HBoxContainer2", true, false)
	assert_true(hbox2 != null, "HBoxContainer2 must exist in Build.tscn")
	assert_false(hbox2.visible, "HBoxContainer2 with placeholder buttons must be hidden (S-060 resolved)")

func test_job_display_information_label_present():
	var scene = load("res://scenes/ui/JobDisplay.tscn")
	assert_true(scene != null, "JobDisplay.tscn must load successfully")
	var instance = scene.instantiate()
	track(instance)
	
	var info_node = instance.find_child("Information", true, false)
	assert_true(info_node != null, "Information node must exist in JobDisplay.tscn (S-059 resolved)")
	assert_true(info_node is Label, "Information node must be a Label")
