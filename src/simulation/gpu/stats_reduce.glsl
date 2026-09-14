// stats_reduce.glsl — GPU-редукция статистики агентов (пункт 5).
// Считает НОВЫЕ метрики, которые на CPU дороги (O(N) каждый кадр), а HUD
// показывает раз в секунду: средний голод, средний Mood, долю уставших
// (Fatigue > 80) и распределение по состояниям (5 корзин AgentState).
//
// Схема: один Dispatch1D (256 тредов на группу), shared-редукция сумм внутри
// workgroup → тред 0 пишет vec4 Partial[groupId] (x=sumHunger, y=sumMood,
// z=countTired, w=countN). Гистограмма состояний — 5 глобальных atomicAdd
// (по одному на тред, contention низкий: всего 5 корзин). Hist-буфер CPU
// обнуляет заливкой перед каждым диспатчем (20 байт), отдельного clear-пасса нет.
// CPU делает ДВА маленьких readback (Partial + Hist, байты, не мегабайты!)
// и сворачивает в средние. Без GPU метрики недоступны (Last == null),
// HUD показывает старые строки без изменений.
#[compute]
#version 450

layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

// binding 0: голод (0 = сыт, 100 = голоден).
layout(set = 0, binding = 0, std430) restrict readonly buffer HungerBuf {
    float hunger[];
};

// binding 1: настроение (0 = депрессия, 100 = счастлив).
layout(set = 0, binding = 1, std430) restrict readonly buffer MoodBuf {
    float mood[];
};

// binding 2: усталость (0–100; порог 80 = AgentNeedsConfig.FatigueSlowThreshold).
layout(set = 0, binding = 2, std430) restrict readonly buffer FatigueBuf {
    float fatigue[];
};

// binding 3: состояние агента как uint (AgentState: byte → 0..4).
layout(set = 0, binding = 3, std430) restrict readonly buffer StateBuf {
    uint state[];
};

// binding 4: частичные суммы по workgroup (x=sumH, y=sumM, z=tired, w=n).
layout(set = 0, binding = 4, std430) writeonly buffer PartialBuf {
    vec4 partial[];
};

// binding 5: гистограмма 5 состояний (атомики; CPU обнуляет перед диспатчем).
layout(set = 0, binding = 5, std430) buffer HistBuf {
    uint hist[];
};

// Push-константы: count, numGroups = 8 байт (< 128).
// Порядок полей обязан совпадать с BuildPush в GpuStatsReduce.cs.
layout(push_constant) uniform PushConstants {
    uint count;
    uint numGroups;
} pc;

// Shared-память для редукции внутри workgroup (256 vec4 = 4 КБ — влезет везде).
shared vec4 s[256];

void main() {
    uint lid = gl_LocalInvocationID.x;
    uint gid = gl_GlobalInvocationID.x;
    uint group = gl_WorkGroupID.x;

    // Gated: лишние треды последней группы дают нулевой вклад (и не трогают hist).
    float h = 0.0;
    float m = 0.0;
    float t = 0.0;
    float n = 0.0;
    if (gid < pc.count) {
        h = hunger[gid];
        m = mood[gid];
        t = (fatigue[gid] > 80.0) ? 1.0 : 0.0;
        n = 1.0;
        // Один атомик на тред; кламп защищает от мусора (валидно только 0..4).
        atomicAdd(hist[min(state[gid], 4u)], 1u);
    }
    s[lid] = vec4(h, m, t, n);
    barrier();

    // Дерево-редукция: 128 → 64 → ... → 1. barrier() вызывают ВСЕ треды
    // (он вне if — рассинхрона нет), складывают только первые stride.
    for (uint stride = 128u; stride > 0u; stride >>= 1u) {
        if (lid < stride) {
            s[lid] += s[lid + stride];
        }
        barrier();
    }

    // Тред 0 пишет итог группы: x=sumH, y=sumM, z=tired, w=n.
    if (lid == 0u) {
        partial[group] = s[0];
    }
}
