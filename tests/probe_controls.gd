var pos = Vector2(975.9, 556.1)
var out = []
var stack = [get_tree().root]
while stack.size() > 0:
	var n = stack.pop_back()
	if n is Control:
		if n.mouse_filter != 2 and n.get_global_rect().has_point(pos):
			out.append([str(n.get_path()), n.mouse_filter])
	var kids = n.get_children()
	for i in range(kids.size() - 1, -1, -1):
		stack.append(kids[i])
return {"blocking": out, "count": out.size()}
