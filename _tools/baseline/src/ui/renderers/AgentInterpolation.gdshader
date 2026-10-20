// Движение 2D MultiMesh агентов интерполируется на GPU.
// COLOR.rg = (cos, sin) направления взгляда/движения.
// INSTANCE_CUSTOM = (prev.x, prev.y, target.x, target.y).
shader_type canvas_item;

uniform float lerp_factor : hint_range(0.0, 1.0) = 1.0;

void vertex() {
	vec2 agent_position = mix(INSTANCE_CUSTOM.xy, INSTANCE_CUSTOM.zw, clamp(lerp_factor, 0.0, 1.0));
	
	// Вращение квада вокруг собственного центра (0, 0)
	float c = COLOR.r;
	float s = COLOR.g;
	VERTEX = vec2(c * VERTEX.x - s * VERTEX.y, s * VERTEX.x + c * VERTEX.y);
	
	VERTEX += agent_position;
}

void fragment() {
	COLOR = texture(TEXTURE, UV);
}
