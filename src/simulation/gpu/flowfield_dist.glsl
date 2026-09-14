// flowfield_dist.glsl — один проход multi-source BFS-волны (Якоби-релаксация дистанций).
// Пункт 1: глобальное поле дистанций 512x512 до БЛИЖАЙШЕЙ цели из набора.
//
// Схема: CPU заливает битмаску целей (IsTarget) и карту блоков (Blocked),
// DistA инициализируется (цель ? 0 : INF), затем maxIters ping-pong проходов
// A→B / B→A. Направление прохода выбирает push-константа passDir.
// Барьеры между проходами НЕ нужны: GpuComputeContext.Dispatch2D делает Submit
// на каждый вызов, порядок диспатчей в очереди гарантирован, данные гонятся
// через память. Зафиксировано как архитектурное решение.
//
// Связность дистанций — 4 (фон-Нейман, манхэттен), как BFS на CPU
// (FlowFieldManager считает дистанции по 4 соседям, а 8 соседей использует
// только при выборе направления — там же, в vec-шейдере).
// Диагонали здесь НЕ учитываются сознательно.
#[compute]
#version 450

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

// binding 0: 1 = стена/вода/гора (непроходимо), 0 = проходимо.
layout(set = 0, binding = 0, std430) restrict readonly buffer Blocked {
    uint blocked[];
};

// bindings 1-2: ping-pong дистанций (float; INF = 1e30).
// Оба объявлены read-write сознательно: направление прохода (A→B или B→A)
// задаётся push-константой passDir в рантайме, а readonly-атрибут зашивается
// в шейдер статически — разделение in/out потребовало бы два пайплайна
// с дублированным кодом при нулевом выигрыше.
layout(set = 0, binding = 1, std430) buffer DistA {
    float distA[];
};

layout(set = 0, binding = 2, std430) buffer DistB {
    float distB[];
};

// binding 3: 1 = клетка-цель (работа/склад/ферма — что угодно, multi-source).
layout(set = 0, binding = 3, std430) restrict readonly buffer IsTarget {
    uint isTarget[];
};

// Push-константы: mapW, mapH, passDir (0: A→B, 1: B→A), unused = 16 байт (< 128).
layout(push_constant) uniform PushConstants {
    uint mapW;
    uint mapH;
    uint passDir;
    float unused;
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

    // Стена/вода/гора: всегда INF, волну не проводят и не принимают.
    if (blocked[id] != 0u) {
        if (pc.passDir == 0u) {
            distB[id] = INF;
        } else {
            distA[id] = INF;
        }
        return;
    }

    // Читаем ВХОДНОЙ буфер прохода в локалы (ветвление когерентно —
    // весь диспатч идёт в одну сторону, дивергенции варпов нет).
    float self;
    float left;
    float right;
    float up;
    float down;
    if (pc.passDir == 0u) {
        self = distA[id];
        left = (x > 0u) ? distA[id - 1u] : INF;
        right = (x + 1u < pc.mapW) ? distA[id + 1u] : INF;
        up = (y > 0u) ? distA[id - pc.mapW] : INF;
        down = (y + 1u < pc.mapH) ? distA[id + pc.mapW] : INF;
    } else {
        self = distB[id];
        left = (x > 0u) ? distB[id - 1u] : INF;
        right = (x + 1u < pc.mapW) ? distB[id + 1u] : INF;
        up = (y > 0u) ? distB[id - pc.mapW] : INF;
        down = (y + 1u < pc.mapH) ? distB[id + pc.mapW] : INF;
    }

    // Заблокированные соседи уже INF во входном буфере (init на CPU ставит
    // blocked-клеткам INF даже при isTarget=1, а каждый проход перезаливает
    // их INF), так что отдельной проверки blocked[] у соседей не нужно —
    // min() их естественно игнорирует.
    float best = (isTarget[id] != 0u) ? 0.0 : INF;
    best = min(best, self);
    best = min(best, left + 1.0);
    best = min(best, right + 1.0);
    best = min(best, up + 1.0);
    best = min(best, down + 1.0);

    // Пишем в ВЫХОДНОЙ буфер прохода. Каждый тред — только свой id, гонок нет.
    if (pc.passDir == 0u) {
        distB[id] = best;
    } else {
        distA[id] = best;
    }
}
