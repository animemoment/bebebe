shader_type canvas_item;

// Канонические параметры воды из terrain_blend.gdshader для визуального равенства
uniform vec4 water_color : source_color = vec4(0.20, 0.44, 0.82, 1.0);
uniform vec4 water_deep_color : source_color = vec4(0.14, 0.30, 0.68, 1.0);
uniform float water_time = 0.0;
uniform bool enable_glints = true;
uniform vec4 glint_color : source_color = vec4(0.88, 0.96, 1.0, 1.0);
uniform float pixel_size : hint_range(1.0, 8.0, 1.0) = 2.0;

varying vec2 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
}

float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

void fragment() {
	vec4 base_color = texture(TEXTURE, UV);
	
	// Определение водной поверхности по цветовому профилю тайла воды
	bool is_water = (base_color.b > 0.45 && base_color.r < 0.35 && base_color.g < 0.60);
	
	if (is_water) {
		vec2 pix_wp = floor(world_pos / pixel_size) * pixel_size;
		float anim_time = (water_time > 0.0) ? water_time : TIME;
		
		// Точная формула волны глубины из terrain_blend.gdshader
		float depth_wave = sin((pix_wp.x * 0.015 + pix_wp.y * 0.02) + anim_time * 0.3) * 0.03;
		vec4 water_col = mix(water_color, water_deep_color, 0.4 + depth_wave);
		
		if (enable_glints) {
			vec2 glint_cell_size = vec2(48.0, 24.0);
			vec2 cell_idx = floor(pix_wp / glint_cell_size);
			vec2 local_p = pix_wp - cell_idx * glint_cell_size;

			float h1 = hash(cell_idx * 1.37 + vec2(15.1, 7.3));
			float h2 = hash(cell_idx * 2.19 + vec2(83.4, 29.8));
			vec2 glint_pos = floor((vec2(h1, h2) * (glint_cell_size - vec2(8.0, 6.0)) + vec2(4.0, 3.0)) / pixel_size) * pixel_size;

			float cell_phase = hash(cell_idx * 5.71);
			float cycle = fract(anim_time * 0.22 + cell_phase);

			if (cycle < 0.12) {
				vec2 delta = abs(local_p - glint_pos);
				if (delta.y < pixel_size && delta.x <= pixel_size * 1.5) {
					float brightness = sin(cycle / 0.12 * 3.14159);
					// §117: 0.95 — ровно как в terrain_blend.gdshader (было 0.9 —
					// лёгкое расхождение яркости бликов двух вод у границы слоёв).
					water_col = mix(water_col, glint_color, brightness * 0.95);
				}
			}
		}
		COLOR = water_col;
	} else {
		COLOR = base_color;
	}
}
