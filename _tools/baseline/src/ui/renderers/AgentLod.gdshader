// Микро-шейдер агентов для дальнего макро-зума (LOD 2, Zoom < 0.22).
// Без текстурного сэмплирования — 0 чтений памяти текстур VRAM, 0 промахов кэша.
// INSTANCE_CUSTOM = (prev.x, prev.y, target.x, target.y).
shader_type canvas_item;

uniform float lerp_factor : hint_range(0.0, 1.0) = 1.0;
uniform vec4 agent_color : source_color = vec4(0.3, 0.65, 0.9, 1.0);

void vertex() {
	vec2 agent_position = mix(INSTANCE_CUSTOM.xy, INSTANCE_CUSTOM.zw, clamp(lerp_factor, 0.0, 1.0));
	VERTEX += agent_position;
}

void fragment() {
	COLOR = agent_color;
}
