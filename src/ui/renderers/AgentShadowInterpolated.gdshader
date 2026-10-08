// Тени агентов: CPU передаёт только prev/target, координаты и деформация
// силуэта вычисляются на GPU в том же кадре, что и спрайт.
shader_type canvas_item;

uniform vec2 sun_dir = vec2(1.0, 0.0);
uniform float sun_len_n = 1.0;
uniform float sun_width_n = 0.55;
uniform vec2 quad_size = vec2(32.0, 32.0);
uniform float lerp_factor : hint_range(0.0, 1.0) = 1.0;

void vertex() {
	vec2 agent_position = mix(INSTANCE_CUSTOM.xy, INSTANCE_CUSTOM.zw, clamp(lerp_factor, 0.0, 1.0));
	vec2 perp = vec2(-sun_dir.y, sun_dir.x);
	float height = 16.0 - VERTEX.y;
	// MultiMesh имеет identity-transform; якорь тени — ноги агента.
	VERTEX = agent_position + vec2(0.0, 16.0)
		+ sun_dir * height * sun_len_n
		+ perp * (VERTEX.x * sun_width_n);
}

void fragment() {
	COLOR = texture(TEXTURE, UV) * COLOR;
}
