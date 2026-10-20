#[compute]
#version 450

// Рабочая группа 256 потоков (для 100,000 агентов = 391 группа)
layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

// Буфер 0: Позиции X агентов (чтение и запись на месте)
layout(set = 0, binding = 0, std430) buffer PosXBuffer {
    float posX[];
} io_posX;

// Буфер 1: Позиции Y агентов (чтение и запись на месте)
layout(set = 0, binding = 1, std430) buffer PosYBuffer {
    float posY[];
} io_posY;

// Буфер 2: Кэшированные промежуточные цели X (StepTargetX)
layout(set = 0, binding = 2, std430) readonly buffer StepTargetXBuffer {
    float stepX[];
} in_stepX;

// Буфер 3: Кэшированные промежуточные цели Y (StepTargetY)
layout(set = 0, binding = 3, std430) readonly buffer StepTargetYBuffer {
    float stepY[];
} in_stepY;

// Буфер 4: Компактная битовая маска прибытия (1 бит на агента = 12.5 КБ на 100k)
layout(set = 0, binding = 4, std430) buffer ReachedMaskBuffer {
    uint mask[];
} out_mask;

// Буфер 5: Параметры шага симуляции (UBO std140, 16 байт)
layout(set = 0, binding = 5, std140) uniform TransformParams {
    int agentCount;
    float stepLen;
    float reachSq;
    int pad0;
} params;

void main() {
    uint idx = gl_GlobalInvocationID.x;
    if (idx >= uint(params.agentCount)) {
        return;
    }

    float cx = io_posX.posX[idx];
    float cy = io_posY.posY[idx];
    float tx = in_stepX.stepX[idx];
    float ty = in_stepY.stepY[idx];

    float dx = tx - cx;
    float dy = ty - cy;
    float dsq = dx * dx + dy * dy;

    if (dsq > params.reachSq) {
        float inv = inversesqrt(dsq) * params.stepLen;
        io_posX.posX[idx] = cx + dx * inv;
        io_posY.posY[idx] = cy + dy * inv;
    } else {
        // Атомарная установка флага прибытия к текущей путевой точке
        uint wordIdx = idx >> 5u;
        uint bitMask = 1u << (idx & 31u);
        atomicOr(out_mask.mask[wordIdx], bitMask);
    }
}
