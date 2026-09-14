// flowfield_vec.glsl — ОДИН диспатч после сходимости дистанций.
// Пункт 2: переводит готовое поле Dist в поле направлений Vec.
//
// Правило: для каждой проходимой клетки ищем соседа (8 окрестность,
// как выбор направления в FlowFieldManager) с минимальной дистанцией
// и пишем ненормализованный вектор шага к нему. Потребитель на CPU
// нормализует через Vector2.Normalize (там же, как локальный BFS-путь).
// Диагонали НЕ учитывались при подсчёте дистанций (манхэттен), но
// направление с диагоналями выбирается при чтении — зафиксировано.
//
// blocked / цель сама / INF-окружение (нет пути в радиусе покрытия) → (0,0).
// (0,0) на CPU трактуется как «нет данных» → false → fallback в локальный BFS.
#[compute]
#version 450

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

// binding 0: 1 = стена/вода/гора, 0 = проходимо.
layout(set = 0, binding = 0, std430) restrict readonly buffer Blocked {
    uint blocked[];
};

// binding 1: готовое поле дистанций (один из ping-pong буферов после чётного
// числа проходов — вызыватель передаёт Rid финального буфера).
layout(set = 0, binding = 1, std430) restrict readonly buffer Dist {
    float dist[];
};

// binding 2: выход — направление к соседу с мин. дистанцией (ненормализованное).
layout(set = 0, binding = 2, std430) writeonly buffer Vec {
    vec2 vecs[];
};

// Push-константы: mapW, mapH = 8 байт (< 128).
layout(push_constant) uniform PushConstants {
    uint mapW;
    uint mapH;
} pc;

const float INF = 1e30;

void main() {
    uint x = gl_GlobalInvocationID.x;
    uint y = gl_GlobalInvocationID.y;
    // Гард краевых тредов (карта не обязана быть кратна 16).
    if (x >= pc.mapW || y >= pc.mapH) {
        return;
    }
    uint id = y * pc.mapW + x;

    // Стена: направления нет.
    if (blocked[id] != 0u) {
        vecs[id] = vec2(0.0, 0.0);
        return;
    }

    float selfD = dist[id];

    // Цель сама (d=0 — минимум недостижим) или нет пути: стоять.
    if (selfD <= 0.5 || selfD >= INF * 0.5) {
        vecs[id] = vec2(0.0, 0.0);
        return;
    }

    // 8 соседей + строгий минимум (знак <: при равенстве держим первого,
    // порядок обхода совпадает с Neighbors CPU: 4 ортогональных, затем 4 диагонали).
    float best = selfD;
    vec2 bestStep = vec2(0.0, 0.0);

    for (int dy = -1; dy <= 1; dy++) {
        for (int dx = -1; dx <= 1; dx++) {
            if (dx == 0 && dy == 0) {
                continue;
            }
            // Знаковый сдвиг: проверка границ через int (беззнаковое сравнение
            // с приведением тоже годится, но явные if читаются проще в шейдере).
            int nx = int(x) + dx;
            int ny = int(y) + dy;
            if (nx < 0 || ny < 0 || nx >= int(pc.mapW) || ny >= int(pc.mapH)) {
                continue;
            }
            float nd = dist[uint(ny) * pc.mapW + uint(nx)];
            if (nd < best) {
                best = nd;
                bestStep = vec2(float(dx), float(dy));
            }
        }
    }

    vecs[id] = bestStep;
}
