// Тени агентов: CPU передаёт только prev/target, координаты и деформация
// силуэта вычисляются на GPU в том же кадре, что и спрайт.
shader_type canvas_item;

uniform vec2 sun_dir = vec2(1.0, 0.0);
uniform float sun_len_n = 1.0;
uniform float sun_width_n = 0.55;
uniform vec2 quad_size = vec2(32.0, 32.0);
uniform float lerp_factor : hint_range(0.0, 1.0) = 1.0;

// Ноги спрайта в people.png: строка 23 из 32.
// В центрированном кваде [-16, 16] ноги = +7.0.
const float FEET_Y = 7.0;

void vertex() {
	vec2 agent_position = mix(INSTANCE_CUSTOM.xy, INSTANCE_CUSTOM.zw, clamp(lerp_factor, 0.0, 1.0));
	// Высота над подошвами: 0 на подошвах (VERTEX.y = 7.0), до 17 на макушке (VERTEX.y = -10.0).
	float height = max(0.0, FEET_Y - VERTEX.y);
	// Сохраняем естественную ширину спрайта VERTEX.x, сдвигая тень по направлению солнца.
	// 0.65 по оси Y дает реалистичный 2.5D ракурс проекции на землю.
	VERTEX.x = agent_position.x + VERTEX.x + sun_dir.x * (height * sun_len_n);
	VERTEX.y = agent_position.y + FEET_Y + sun_dir.y * (height * sun_len_n * 0.65);
}

void fragment() {
	vec4 tex = texture(TEXTURE, UV);
	// Мягкая полутень: подошвы (UV.y ≈ 0.72) плотные (alpha 1.0),
	// макушка (UV.y ≈ 0.19) мягко рассеивается (alpha 0.65).
	float penumbra = mix(0.65, 1.0, clamp((UV.y - 0.19) / (0.72 - 0.19), 0.0, 1.0));
	COLOR = tex * COLOR;
	COLOR.a *= penumbra;
}
