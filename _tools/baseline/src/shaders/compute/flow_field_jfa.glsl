#[compute]
#version 450

// Рабочая группа 16x16 = 256 потоков
layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

// Буфер 0: Карта стоимости проходимости (0 = стена, 1 = свободно, >1 = замедление)
layout(set = 0, binding = 0, std430) readonly buffer CostBuffer {
    uint cost[];
} in_cost;

// Буфер 1: Дистанции на входе
layout(set = 0, binding = 1, std430) readonly buffer DistInBuffer {
    float dist[];
} in_dist;

// Буфер 2: Дистанции на выходе
layout(set = 0, binding = 2, std430) writeonly buffer DistOutBuffer {
    float dist[];
} out_dist;

// Буфер 3: Вектора течения (vec2: (dirX, dirY))
layout(set = 0, binding = 3, std430) writeonly buffer FlowBuffer {
    vec2 flow[];
} out_flow;

// Буфер 4: Параметры симуляции (UBO std140, выравнивание 16 байт)
layout(set = 0, binding = 4, std140) uniform FlowParams {
    int width;
    int height;
    int stepSize;
    int passMode; // 0 = seed init, 1 = distance jump step, 2 = gradient flow vector
    int targetX;
    int targetY;
    int pad0;
    int pad1;
} params;

const float INF = 1e9;

void main() {
    ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
    int w = params.width;
    int h = params.height;

    if (coord.x >= w || coord.y >= h) {
        return;
    }

    int idx = coord.y * w + coord.x;
    uint c = in_cost.cost[idx];

    // Pass Mode 0: Инициализация целевых семян
    if (params.passMode == 0) {
        if (c == 0u) {
            out_dist.dist[idx] = INF;
        } else if (coord.x == params.targetX && coord.y == params.targetY) {
            out_dist.dist[idx] = 0.0;
        } else {
            out_dist.dist[idx] = INF;
        }
        return;
    }

    // Pass Mode 1: Распространение дистанции с шагом stepSize (Jump Flooding)
    if (params.passMode == 1) {
        if (c == 0u) {
            out_dist.dist[idx] = INF;
            return;
        }

        float best = in_dist.dist[idx];
        int step = params.stepSize;

        for (int dy = -1; dy <= 1; dy++) {
            for (int dx = -1; dx <= 1; dx++) {
                if (dx == 0 && dy == 0) continue;
                int nx = coord.x + dx * step;
                int ny = coord.y + dy * step;

                if (nx >= 0 && nx < w && ny >= 0 && ny < h) {
                    int nIdx = ny * w + nx;
                    uint nc = in_cost.cost[nIdx];
                    if (nc > 0u) {
                        float d = in_dist.dist[nIdx];
                        if (d < INF) {
                            float travelCost = float(c) * length(vec2(float(dx * step), float(dy * step)));
                            float cand = d + travelCost;
                            if (cand < best) {
                                best = cand;
                            }
                        }
                    }
                }
            }
        }
        out_dist.dist[idx] = best;
        return;
    }

    // Pass Mode 2: Расчет нормализованного градиента направления (Vector Field)
    if (params.passMode == 2) {
        if (c == 0u) {
            out_flow.flow[idx] = vec2(0.0, 0.0);
            return;
        }

        // В самой цели вектор равен 0
        if (coord.x == params.targetX && coord.y == params.targetY) {
            out_flow.flow[idx] = vec2(0.0, 0.0);
            return;
        }

        float currentDist = in_dist.dist[idx];
        float minNeighborDist = currentDist;
        vec2 bestDir = vec2(0.0, 0.0);

        // Поиск соседней клетки с минимальной дистанцией к цели
        for (int dy = -1; dy <= 1; dy++) {
            for (int dx = -1; dx <= 1; dx++) {
                if (dx == 0 && dy == 0) continue;
                int nx = coord.x + dx;
                int ny = coord.y + dy;

                if (nx >= 0 && nx < w && ny >= 0 && ny < h) {
                    int nIdx = ny * w + nx;
                    if (in_cost.cost[nIdx] > 0u) {
                        float nd = in_dist.dist[nIdx];
                        if (nd < minNeighborDist) {
                            minNeighborDist = nd;
                            bestDir = vec2(float(dx), float(dy));
                        }
                    }
                }
            }
        }

        if (length(bestDir) > 0.001) {
            out_flow.flow[idx] = normalize(bestDir);
        } else {
            out_flow.flow[idx] = vec2(0.0, 0.0);
        }
    }
}
