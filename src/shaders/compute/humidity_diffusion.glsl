#[compute]
#version 450

// Рабочая группа 16x16 = 256 потоков
layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

// Буфер 0: Текущая влажность клеток (int[], размер W*H)
layout(set = 0, binding = 0, std430) readonly buffer InCurBuffer {
    int cur[];
} in_cur;

// Буфер 1: Новая влажность клеток (int[], размер W*H)
layout(set = 0, binding = 1, std430) writeonly buffer OutNxtBuffer {
    int nxt[];
} out_nxt;

// Буфер 2: Подпитка от воды (0..10 на клетку, упаковано в uint)
layout(set = 0, binding = 2, std430) readonly buffer FeedBuffer {
    uint feed[];
} in_feed;

// Буфер 3: Маска признаков клетки (bit 0 = water, bit 1 = high, bit 2 = forest, bit 3 = farm, bit 4 = downhill)
layout(set = 0, binding = 3, std430) readonly buffer FlagsBuffer {
    uint flags[];
} in_flags;

// Буфер 4: Параметры симуляции (UBO std140)
layout(set = 0, binding = 4, std140) uniform SimulationParams {
    int width;
    int height;
    int seasonDelta;
    int minMoisture;
} params;

void main() {
    ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
    int w = params.width;
    int h = params.height;

    if (coord.x >= w || coord.y >= h) {
        return;
    }

    int i = coord.y * w + coord.x;
    uint flag = in_flags.flags[i];

    // Бит 0: Вода — источник 200 (Dirichlet), не меняется
    if ((flag & 1u) != 0u) {
        out_nxt.nxt[i] = 200;
        return;
    }

    int me = in_cur.cur[i];

    // Соседи Von Neumann с клампом на границах
    int leftIdx  = coord.x > 0 ? (i - 1) : i;
    int rightIdx = coord.x + 1 < w ? (i + 1) : i;
    int upIdx    = coord.y > 0 ? (i - w) : i;
    int downIdx  = coord.y + 1 < h ? (i + w) : i;

    int left  = in_cur.cur[leftIdx];
    int right = in_cur.cur[rightIdx];
    int up    = in_cur.cur[upIdx];
    int down  = in_cur.cur[downIdx];

    bool high = (flag & 2u) != 0u;
    bool forest = (flag & 4u) != 0u;
    bool farm = (flag & 8u) != 0u;
    bool downhill = (flag & 16u) != 0u;

    int v;
    if (downhill) {
        // Анизотропия: влага стекает вниз (нижний сосед x2, верхний x0.5)
        int avg = (left + right + (down << 1) + (up >> 1) + me) / 5;
        v = me + ((avg - me) >> 4);
    } else {
        int avg = (left + right + up + down) >> 2;
        v = me + ((avg - me) >> 5);
    }

    // Подпитка от воды (BFS 10 клеток)
    uint f = in_feed.feed[i];
    if (f > 7u) {
        v += 3;
    } else if (f > 0u) {
        v += 1;
    }

    // Испарение (хеш-гейт клеток: ~1/4 сохнет на 1 за тик)
    int evap = 0;
    uint h1 = uint((coord.x * 73856093) ^ (coord.y * 19349663));
    if ((h1 % 4u) == 0u) {
        evap = 1;
    }

    if (high) {
        uint h2 = uint((coord.x * 83492791) ^ (coord.y * 2971215073u % 100000u));
        if ((h2 & 1u) == 0u) {
            evap += 1;
        }
    }

    if (forest) {
        evap = 0; // Лес удерживает влагу
    }

    // Транспирация вокруг леса
    if (!forest) {
        bool nearForest = ((in_flags.flags[leftIdx] & 4u) != 0u) ||
                          ((in_flags.flags[rightIdx] & 4u) != 0u) ||
                          ((in_flags.flags[upIdx] & 4u) != 0u) ||
                          ((in_flags.flags[downIdx] & 4u) != 0u);
        if (nearForest) {
            uint h3 = uint((coord.x * 314159) ^ (coord.y * 271828));
            if ((h3 & 7u) == 0u) {
                v += 1;
            }
        }
    }

    if (farm) {
        v -= 1; // Огород тянет влагу
    }

    v -= evap;
    v += params.seasonDelta;

    if (high) {
        v = me + ((v - me) >> 2); // Скалы удерживают
    }

    int minM = params.minMoisture;
    if (v < minM) {
        v = minM;
    }

    out_nxt.nxt[i] = v;
}
