// fbm_noise.glsl — одна октава Perlin-шума (fBm) на GPU.
// Пункт 4 PLAN.md: поблочная генерация heightMap/forestMap через compute-шейдер.
//
// Точность: GLSL float32 vs C# float32 — бит-в-бит НЕ гарантируется.
// Порядок операций повторяет CPU (NoiseGenerator.AccumulateOctave), но FMA/fused-операции
// и округления драйвера могут дать расхождение ~1 ulp на значение. Это нормально:
// пороги waterThreshold/mountainThreshold имеют запас, визуально карты совпадают.
//
// Аккумуляция: шейдер делает values[id] += ... поверх существующего содержимого,
// как CPU делает map[x, y] += ... . Поэтому выходной буфер перед первой октавой
// один раз заполняется нулями на CPU (BufferUpdate нулевым массивом), а перед
// последующими октавами НЕ чистится. Буфер персистентный (ключ "fbm_out").
// Ридбэк (ReadbackSync) делается ОДИН раз после всех октав — в этом выигрыш пункта 4.
//
// Детерминизм: решётка gradIdx готовится на CPU тем же new Random((int)seed)
// и тем же порядком циклов (gx внешний, gy внутренний), что и CPU-путь,
// в раскладке flat[gx*gridH+gy] — точной копии памяти C# int[gridW,gridH].
// Шейдер читает gradIdx[x*gridH+y], поэтому при одинаковом seed градиент
// в ячейке (x,y) совпадает с CPU gradientIndices[x,y] (до FMA-округлений float).
#[compute]
#version 450

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

// Выходная карта width*height float. НЕ writeonly: аккумуляция += требует чтения.
// Каждый тред пишет только свой id — гонок нет.
layout(set = 0, binding = 0, std430) restrict buffer OutMap {
    float values[];
};

// Решётка индексов градиентов gridW*gridH в раскладке C# int[gridW,gridH]:
// слот (gx,gy) = gx*gridH+gy (row-major по ПЕРВОМУ индексу).
// Чтение gradIdx[x*gridH+y] даёт ровно то же значение, что CPU gradientIndices[x,y].
layout(set = 0, binding = 1, std430) readonly buffer GradGrid {
    uint gradIdx[];
};

// Push-константы: 6 скаляров по 4 байта = 24 байта (лимит 128, запас большой).
layout(push_constant) uniform PushConstants {
    uint mapW;
    uint mapH;
    uint gridW;
    uint gridH;
    float scale;
    float weight;
} pc;

// Те же 8 градиентов, что NoiseGenerator.Gradients (там литерал 0.707f — повторяем).
vec2 gradVec(uint i) {
    if (i == 0u) return vec2(1.0, 0.0);
    if (i == 1u) return vec2(-1.0, 0.0);
    if (i == 2u) return vec2(0.0, 1.0);
    if (i == 3u) return vec2(0.0, -1.0);
    if (i == 4u) return vec2(0.707, 0.707);
    if (i == 5u) return vec2(-0.707, 0.707);
    if (i == 6u) return vec2(0.707, -0.707);
    return vec2(-0.707, -0.707);
}

void main() {
    uint x = gl_GlobalInvocationID.x;
    uint y = gl_GlobalInvocationID.y;
    // Гард краевых тредов (карта не обязана быть кратна 16).
    if (x >= pc.mapW || y >= pc.mapH) {
        return;
    }
    uint id = y * pc.mapW + x;

    // ТОЧНО та же математика, что AccumulateOctave на CPU.
    float sx = float(x) / pc.scale;
    float sy = float(y) / pc.scale;

    float ffx = floor(sx);
    float ffy = floor(sy);
    int x0 = int(ffx);
    int y0 = int(ffy);
    // Clamp как в CPU (на практике решётка с запасом +2, клампы — no-op, но повторяем).
    int x1 = min(x0 + 1, int(pc.gridW) - 1);
    int y1 = min(y0 + 1, int(pc.gridH) - 1);
    x0 = clamp(x0, 0, int(pc.gridW) - 1);
    y0 = clamp(y0, 0, int(pc.gridH) - 1);

    float tx = sx - ffx;
    float ty = sy - ffy;

    vec2 g00 = gradVec(gradIdx[uint(x0) * pc.gridH + uint(y0)]);
    vec2 g10 = gradVec(gradIdx[uint(x1) * pc.gridH + uint(y0)]);
    vec2 g01 = gradVec(gradIdx[uint(x0) * pc.gridH + uint(y1)]);
    vec2 g11 = gradVec(gradIdx[uint(x1) * pc.gridH + uint(y1)]);

    float v00 = g00.x * tx + g00.y * ty;
    float v10 = g10.x * (tx - 1.0) + g10.y * ty;
    float v01 = g01.x * tx + g01.y * (ty - 1.0);
    float v11 = g11.x * (tx - 1.0) + g11.y * (ty - 1.0);

    // Smoothstep t*t*(3-2t) и Lerp a+(b-a)*t (GLSL mix — та же формула).
    float stx = tx * tx * (3.0 - 2.0 * tx);
    float sty = ty * ty * (3.0 - 2.0 * ty);

    float v0 = mix(v00, v10, stx);
    float v1 = mix(v01, v11, stx);
    float value = mix(v0, v1, sty);

    // Та же нормировка и вес, что CPU: (value*0.707+0.5)*weight, аккумуляция +=.
    values[id] += (value * 0.707 + 0.5) * pc.weight;
}
