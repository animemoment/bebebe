// crop_diffuse.glsl — диффузия влажности почвы (стенсил фон-Нейман, Якоби-релаксация).
// Пункт 2: ВТОРИЧНОЕ поле-бонус роста культур. CPU-источник истины (стадии,
// словарь _crops, lock-протокол в CropGrowthManager) НЕ трогается — GPU считает
// только влажность, CPU ускоряет GrowthTimer множителем (1 + moist*BonusScale).
//
// Схема: два ping-pong буфера moistA/moistB, ОДИН Dispatch2D за шаг, шаг не чаще
// раза в 5 сек игрового времени (влажность медленная). Readback — только вместе
// с шагом; между readback'ами CPU читает свой кэш (GpuCropField.GetBonus).
// Без GPU/свежего кэша бонус = 0, поведение роста = старое бит-в-бит.
//
// Кодировка Blocked (binding 2): 0 = свободна (трава без стены), 1 = блок
// (гора/стена — влагу не держит), 2 = вода (вечный источник, out = 1.0).
// Отдельный ключ буфера "crop_blocked" (НЕ переиспользуем ff_blocked из
// flowfield): там бинарная семантика (вода = блок), здесь вода = источник,
// т.е. тернарная. Жизненные циклы тоже разные (flowfield — по набору целей,
// влажность — по карте). Отдельный ключ проще и честнее.
//
// Плодородие (Fert) в шейдере НЕ участвует сознательно: оно статично, CPU
// домножит при чтении бонуса (сейчас бонус = moist как есть, см. GpuCropField).
// Источники влаги готовит CPU при заливке (вода = 1.0, рядом с водой = 0.5),
// шейдер только диффундирует + испаряет.
#[compute]
#version 450

layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

// binding 0: вход влажности (ping-pong источник текущего прохода).
layout(set = 0, binding = 0, std430) restrict readonly buffer Moist {
    float m[];
};

// binding 1: выход влажности (ping-pong приёмник, только запись —
// каждый тред пишет только свой id, гонок нет).
layout(set = 0, binding = 1, std430) writeonly buffer MoistOut {
    float o[];
};

// binding 2: 0 = свободна, 1 = блок (гора/стена), 2 = вода-источник.
layout(set = 0, binding = 2, std430) restrict readonly buffer Blocked {
    uint v[];
};

// Push-константы: mapW, mapH, evap (~0.999), diffuse (~0.2) = 16 байт (< 128).
// Порядок полей обязан совпадать с BuildPush в GpuCropField.cs.
layout(push_constant) uniform PushConstants {
    uint mapW;
    uint mapH;
    float evap;
    float diffuse;
} pc;

void main() {
    uint x = gl_GlobalInvocationID.x;
    uint y = gl_GlobalInvocationID.y;
    // Гард краевых тредов (карта не обязана быть кратна 16).
    if (x >= pc.mapW || y >= pc.mapH) {
        return;
    }
    uint id = y * pc.mapW + x;
    uint b = v[id];

    // Вода — вечный источник: всегда 1.0 (не испаряется, не размывается).
    if (b == 2u) {
        o[id] = 1.0;
        return;
    }
    // Блок (гора/стена) влагу не держит: всегда 0.
    if (b == 1u) {
        o[id] = 0.0;
        return;
    }

    float self = m[id];

    // Соседи фон-Нейман. OOB = self (нулевой поток через край карты).
    // Сосед-блок (1) = self (потока сквозь стену нет); сосед-вода (2) = 1.0
    // (влага течёт от воды на берег и дальше диффундирует вглубь).
    float left = self;
    float right = self;
    float up = self;
    float down = self;
    if (x > 0u) {
        uint nb = v[id - 1u];
        left = (nb == 1u) ? self : ((nb == 2u) ? 1.0 : m[id - 1u]);
    }
    if (x + 1u < pc.mapW) {
        uint nb = v[id + 1u];
        right = (nb == 1u) ? self : ((nb == 2u) ? 1.0 : m[id + 1u]);
    }
    if (y > 0u) {
        uint nb = v[id - pc.mapW];
        up = (nb == 1u) ? self : ((nb == 2u) ? 1.0 : m[id - pc.mapW]);
    }
    if (y + 1u < pc.mapH) {
        uint nb = v[id + pc.mapW];
        down = (nb == 1u) ? self : ((nb == 2u) ? 1.0 : m[id + pc.mapW]);
    }
    float avg4 = (left + right + up + down) * 0.25;

    // Якоби-релаксация к среднему соседей + испарение. Формула простая:
    // mix(self, avg4, diffuse) * evap, кламп в [0, 1].
    o[id] = clamp(mix(self, avg4, pc.diffuse) * pc.evap, 0.0, 1.0);
}
