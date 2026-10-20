var n = get_tree().root.get_node_or_null("/root/Node2D/StreamingWorldView/WorldPlan")
var ts = n.tile_set
var ids = []
for i in range(ts.get_source_count()):
	ids.append(ts.get_source_id(i))
var before = n.get_cell_source_id(Vector2i(1212, 412))
n.set_cell(Vector2i(1212, 412), 10, Vector2i(0, 0))
var after = n.get_cell_source_id(Vector2i(1212, 412))
n.set_cell(Vector2i(1212, 412), -1)
return {"sources": ids, "before": before, "after_set_src10": after}
