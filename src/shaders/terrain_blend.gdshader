shader_type canvas_item;

// Карта типов биомов (512x512 R8: 0 = Grass, 1 = Sand, 2 = Water)
uniform sampler2D terrain_map : filter_nearest;

// Текстуры биомов
uniform sampler2D tex_grass : repeat_enable, filter_nearest;
uniform sampler2D tex_sand : repeat_enable, filter_nearest;
uniform sampler2D tex_water : repeat_enable, filter_nearest;

// Размер карты в тайлах (512x512)
uniform vec2 map_size = vec2(512.0, 512.0);

// Размер виртуального арт-пикселя для квантования границ (2.0 = классический пиксель-арт)
uniform float pixel_size : hint_range(1.0, 8.0, 1.0) = 2.0;

// Режим монолитного цвета воды (устраняет швы и полосы тайлов)
uniform bool use_solid_water = true;
uniform vec4 water_color : source_color = vec4(0.20, 0.44, 0.82, 1.0);
uniform vec4 water_deep_color : source_color = vec4(0.14, 0.30, 0.68, 1.0);

// Время симуляции (передается из C# с учетом скорости игры)
uniform float water_time = 0.0;

// Физические спекулярные блики солнца на воде
uniform bool enable_glints = true;
uniform vec4 glint_color : source_color = vec4(0.88, 0.96, 1.0, 1.0);

// Сила извилистости и сглаживания углов (0.0 = тайловые углы, 0.45 = естественные изгибы)
uniform float shore_curvature : hint_range(0.1, 0.8, 0.05) = 0.45;

varying vec2 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
}

// Быстрый 2D Хеш
float hash(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

// 2D Value-шум
float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i + vec2(0.0, 0.0)), hash(i + vec2(1.0, 0.0)), u.x),
	           mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

// Безопасная выборка клетки из terrain_map
int sample_cell(ivec2 cell) {
	cell = clamp(cell, ivec2(0), ivec2(map_size) - ivec2(1));
	return int(round(texelFetch(terrain_map, cell, 0).r * 255.0));
}

// Плавный FBM шум для извилистых берегов
float get_shore_fbm(vec2 p) {
	float n1 = noise(p * 0.005) * 0.62;
	float n2 = noise(p * 0.013 + vec2(27.4, 61.8)) * 0.28;
	float n3 = noise(p * 0.035 + vec2(83.1, 19.5)) * 0.10;
	return (n1 + n2 + n3 - 0.5);
}

// Непрерывное билинейное поле весов биомов со ступенчатым пиксельным квантованием
int evaluate_biome(vec2 p) {
	vec2 tile_uv = (p / 64.0) - vec2(0.5);
	ivec2 cell_i = ivec2(floor(tile_uv));
	vec2 f = fract(tile_uv);

	// Hermite S-кривая для скругления углов
	vec2 s = f * f * (3.0 - 2.0 * f);

	float w00 = (1.0 - s.x) * (1.0 - s.y);
	float w10 = s.x * (1.0 - s.y);
	float w01 = (1.0 - s.x) * s.y;
	float w11 = s.x * s.y;

	int c00 = sample_cell(cell_i);
	int c10 = sample_cell(cell_i + ivec2(1, 0));
	int c01 = sample_cell(cell_i + ivec2(0, 1));
	int c11 = sample_cell(cell_i + ivec2(1, 1));

	float w_grass = 0.0;
	float w_sand  = 0.0;
	float w_water = 0.0;

	if (c00 == 0) w_grass += w00; else if (c00 == 1) w_sand += w00; else w_water += w00;
	if (c10 == 0) w_grass += w10; else if (c10 == 1) w_sand += w10; else w_water += w10;
	if (c01 == 0) w_grass += w01; else if (c01 == 1) w_sand += w01; else w_water += w01;
	if (c11 == 0) w_grass += w11; else if (c11 == 1) w_sand += w11; else w_water += w11;

	// Примешиваем извилистый макро-шум FBM
	float fbm = get_shore_fbm(p) * shore_curvature;

	// Дискретные пиксельные зубцы/пучки травы (придают кромке четкий пиксель-арт вид)
	float teeth = (floor(noise(p * 0.075) * 3.0) / 3.0 - 0.33) * 0.18;

	float water_pot = w_water + fbm - teeth;
	float land_pot = (w_grass + w_sand) - fbm + teeth;

	if (water_pot > land_pot && w_water > 0.05) {
		return 2; // Water
	}

	// Разделение между травой и песком
	float sand_shift = (noise(p * 0.008 + vec2(42.1, 87.3)) - 0.5) * 0.25;
	if (w_sand + sand_shift > w_grass) {
		return 1; // Sand
	}

	return 0; // Grass
}

// Сэмплирование текстуры биома
vec4 sample_biome_color(int type, vec2 wp, float anim_time) {
	if (type == 0) {
		return texture(tex_grass, fract(wp / 64.0));
	} else if (type == 1) {
		return texture(tex_sand, fract(wp / 128.0));
	} else {
		if (use_solid_water) {
			// Спокойная чистая водная гладь с едва заметным глубинным переливом (без швов и полос)
			float depth_wave = sin((wp.x * 0.015 + wp.y * 0.02) + anim_time * 0.3) * 0.03;
			return mix(water_color, water_deep_color, 0.4 + depth_wave);
		}
		return texture(tex_water, fract(wp / 64.0));
	}
}

void fragment() {
	// 1. Квантование мировых координат по виртуальной пиксельной сетке (чёткий пиксель-арт)
	vec2 pix_wp = floor(world_pos / pixel_size) * pixel_size;

	// 2. Время анимации (синхронизировано с TimeManager из C#, либо фоллбек на TIME)
	float anim_time = (water_time > 0.0) ? water_time : TIME;

	// 3. Вычисление биома через непрерывное билинейное поле с пиксельными зубцами
	int main_type = evaluate_biome(pix_wp);

	// 4. Базовый цвет биома
	vec4 final_col = sample_biome_color(main_type, pix_wp, anim_time);

	// 5. Геоморфология обрывистого берега и физика света (Bluff Cliff & Shadow)
	if (main_type == 2) {
		// Проверяем, находится ли вода под обрывом суши (тень от берега на юг и восток)
		int land_up1 = evaluate_biome(pix_wp - vec2(0.0, pixel_size));
		int land_left1 = evaluate_biome(pix_wp - vec2(pixel_size, 0.0));
		int land_up2 = evaluate_biome(pix_wp - vec2(0.0, pixel_size * 2.0));

		if (land_up1 == 0 || land_left1 == 0) {
			// Глубокая контактная тень у самого подножия обрыва
			final_col.rgb *= 0.68;
		} else if (land_up2 == 0) {
			// Полутень уступа берега
			final_col.rgb *= 0.84;
		} else if (enable_glints) {
			// Водная сетка ячеек 48x24 пикселей (в каждой ячейке не более одной редкой искорки)
			vec2 glint_cell_size = vec2(48.0, 24.0);
			vec2 cell_idx = floor(pix_wp / glint_cell_size);
			vec2 local_p = pix_wp - cell_idx * glint_cell_size;

			// Случайное положение искорки внутри ячейки, выровненное по пиксельной сетке
			float h1 = hash(cell_idx * 1.37 + vec2(15.1, 7.3));
			float h2 = hash(cell_idx * 2.19 + vec2(83.4, 29.8));
			vec2 glint_pos = floor((vec2(h1, h2) * (glint_cell_size - vec2(8.0, 6.0)) + vec2(4.0, 3.0)) / pixel_size) * pixel_size;

			// Индивидуальная фаза мерцания ячейки
			float cell_phase = hash(cell_idx * 5.71);
			float cycle = fract(anim_time * 0.22 + cell_phase);

			// Блик вспыхивает лишь на 12% времени цикла (редкие, деликатные искорки)
			if (cycle < 0.12) {
				vec2 delta = abs(local_p - glint_pos);
				// Горизонтальная форма пиксельного блика 2x1 пиксель
				if (delta.y < pixel_size && delta.x <= pixel_size * 1.5) {
					float brightness = sin(cycle / 0.12 * 3.14159);
					final_col.rgb = mix(final_col.rgb, glint_color.rgb, brightness * 0.95);
				}
			}
		}
	}
	// 6. Трава: обрывистый срез грунта у воды и солнечная бровка
	else if (main_type == 0) {
		int water_down = evaluate_biome(pix_wp + vec2(0.0, pixel_size));
		int water_right = evaluate_biome(pix_wp + vec2(pixel_size, 0.0));
		int water_up = evaluate_biome(pix_wp - vec2(0.0, pixel_size));

		if (water_down == 2 || water_right == 2) {
			// Землистый уступ дерна на обрыве к воде (почвенный профиль)
			final_col.rgb = vec3(0.24, 0.16, 0.10);
		} else if (water_up == 2) {
			// Освещенная солнцем верхняя бровка берега
			final_col.rgb += vec3(0.07);
		}
	}
	// 7. Контактная тень травы на песке
	else if (main_type == 1) {
		int type_up = evaluate_biome(pix_wp - vec2(0.0, pixel_size));
		if (type_up == 0) {
			final_col.rgb *= 0.86;
		}
	}

	COLOR = final_col;
}
