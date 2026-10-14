// Тени агентов в вершинном шейдере: клин от ног, а не параллелограмм.
// Точка привязки = ноги (CPU пишет трансляцию (px, py+16) без сдвига).
// Шейдер растягивает по ВЫСОТЕ над ногами: ноги стоят, макушка уезжает
// вдоль солнца на height * sun_len_n. VERTEX ∈ [-16,16]², ноги = (0,+16).
//
// Проверка: утро — клин влево от ног, полдень — скрыты (Visible=false),
// вечер — вправо. Ноги тени всегда на ногах агента.
shader_type canvas_item;

// Единичное направление солнца (SunState.Dir).
uniform vec2 sun_dir = vec2(1.0, 0.0);
// Нормированная длина тени: sLenN = (LengthPx + 16) / 32.
uniform float sun_len_n = 1.0;
// Нормированная ширина: sWdtN = WidthScale (0.55).
uniform float sun_width_n = 0.55;
// Размер квада в px (32x32). Документирует масштаб.
uniform vec2 quad_size = vec2(32.0, 32.0);

const float FEET_Y = 7.0;

void vertex() {
	// CPU instance transform помещает начало в (px, py + FEET_Y).
	float height = max(0.0, FEET_Y - VERTEX.y);
	VERTEX.x = VERTEX.x + sun_dir.x * (height * sun_len_n);
	VERTEX.y = sun_dir.y * (height * sun_len_n * 0.65);
}

void fragment() {
	vec4 tex = texture(TEXTURE, UV);
	float penumbra = mix(0.65, 1.0, clamp((UV.y - 0.19) / (0.72 - 0.19), 0.0, 1.0));
	COLOR = tex * COLOR;
	COLOR.a *= penumbra;
}
