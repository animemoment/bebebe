# ГЛОБАЛЬНЫЙ АУДИТ БЕСШВОВОЙ ГЕНЕРАЦИИ МИРА + CPU/GPU АНАЛИЗ

## 1. Цели и критерии успеха

| # | Цель | Успех |
|---|------|-------|
| 1 | Выявить баги бесшовной генерации чанков | Список всех багов с severity + PR с фиксами |
| 2 | Оценить выигрыш от переноса просчёта на GPU | Сравнительная таблица CPU vs GPU с метриками, обоснование, PoC-шейдер если выгодно |

---

## 2. Архитектура генерации (обзор)

```
┌─────────────────────────────────────────────────────────┐
│ Camera / Visible Bounds                                 │
│   ↓ WorldChunkStreamer (Worker pool, async)             │
│   → ChunkWindowPlanner (priority queue, distance-based) │
│   → WorkerLoopAsync {                                    │
│       1) Load delta from disk (Brotli-decoded)          │
│       2) GenerateBaseChunk(seed, ver, key)              │
│          └→ SampleCell() × CellCount(4096)              │
│            └→ RegionTraitProvider.BilinearBlend         │
│            └→ CoordinateHash.ValueNoiseQ16 × 8 domains  │
│       3) Apply saved snapshot deltas                    │
│       4) Return ChunkStreamResult → main thread         │
│     }                                                    │
│   → UnloadChunkAsync (save delta to disk via codec)     │
└─────────────────────────────────────────────────────────┘
```

### Ключевые файлы:

| Файл | Роль | CPU-потенциал |
|------|------|---------------|
| `CoordinateHash.cs` — ValueNoiseQ16, Hash, SampleQ16 | Опредерминированный шум: hash → lattice → smooth lerp | ⭐⭐⭐ **ГЛАВНАЯ ЖАЛЮЗИЗА** |
| `WorldChunkGenerator.cs` — SampleBaseCell, GenerateBaseChunk | Перекладывает 8× noise + bilinear blend на чанк (4096 клеток) | ⭐⭐ |
| `RegionTraits.cs` — BlendBilinear, SampleBlended | Сглаживание характеристик регионов | ⭐ |
| `NoiseGenerator.cs` — AccumulateOctave/ridge (legacy Perlin) | Старый генератор (используется только MapData для legacy island) | ⭐ (но удалён comment "GPU-трек удалён") |
| `WorldFeatureGenerator.cs` — CreateRiver, CreateLake | Геометрия river/lake features (anchored at 512-cell grid) | ⭐ (low cost) |
| `WorldChunkCodec.cs` — Encode/Decode (Brotli + CRC32) | Serde chunk delta (не parallelizable) | ⭐ |

---

## 3. ПРОВЕРКА НА БАГИ (адверсариальные тесты)

### 3.1. Координатные крайности

| Тест | Описание | Severity |
|------|----------|----------|
| **Extreme long.MinValue / long.MaxValue** | `FloorDiv`, `FloorMod` при экстремальных координатах | 🔴 Высокий |
| **Cross-chunk negative boundary** | `-1, -1` и `-64, -65` на стыке чанков | 🟡 Средний |
| **Negative region blending** | Bilinear weights when cell region = (-1, -1) | 🟡 Средний |
| **Large coordinate overflow** | `checked()` blocks in `TileOrigin` (`X*64`) — can throw OverflowException | 🔴 Высокий |

**Фикс:** Добавить проверки в `WorldCoordinates.TileOrigin`: если результат выходит за пределы допустимого (например, ±1M клеток), вернуть fallback или предупреждение.

### 3.2. Feature seams across chunks

| Тест | Описание | Severity |
|------|----------|----------|
| **River spanning chunks** | River polyline starts in chunk A, ends in chunk B — обе части появляются консистентно? ✅ Уже проверено в `RunFeatureChecks` | ✅ Пасc |
| **Lake edge case** | Lake circle intersects 4 chunk corners | 🟡 Средний |
| **Concurrent generation + delete** | Streamer churn: Rapid camera move invalidates mid-generation chunk | 🟡 Средний |

### 3.3. Ocean ring (archipelago around start island)

| Тест | Описание | Severity |
|------|------|------|
| **RingWaterBoost edge** | At exactly `distance = RingRadiusCells` boost is 0; just inside it's maximal. Is there a discontinuity? | 🟡 Средний |
| **Deep sea near start** | Cells [513..1024]² should be ocean; verify elevation always suppressed below threshold | 🔴 Высокий |

### 3.4. Delta codec edge cases

| Тест | Описание | Severity |
|------|----------|------|
| **MaxLocalIndex overflow** | If wall count > 4095 per chunk → encoding throws; decoding rejects | 🟡 Средний |
| **Empty payload save** | WorldChunkCodec.Encode refuses empty delta; but what if delta IS empty? UnloadChunkAsync passes `Array.Empty<byte>()` → SaveChunk(empty) must skip file creation | 🟡 Средний |
| **Trailing bytes rejection** | Corrupt file with extra bytes at end → rejected. Good. | ✅ OK |
| **Brotli expansion attack** | Compressed data larger than uncompressed → check `compressedLength > MaxCompressedPayloadBytes` | ✅ Covered |

### 3.5. Стример state machine races

| Тест | Описание | Severity |
|------|----------|------|
| **Update() during drain** | New Update called while DrainReady still running — `_desired` HashSet changed under iteration | 🔴 Высокий |
| **CancelPending + DrainFailures race** | Same keys in both pending and ready queues; Cancel clears _pending but Drain reads _ready | 🟡 Средний |
| **DisposeAsync mid-work** | Workers still processing when Dispose called | 🟡 Средний |

### 3.6. Итоговый приоритет багов

| Priority | Баг | Где |
|----------|-----|-----|
| P0 | `TileOrigin` overflow on extreme coordinates | `WorldCoordinates.cs:69` |
| P0 | Update/drain race condition | `WorldChunkStreamer.cs:118,148` |
| P1 | Deep sea ring discontinuity | `WorldChunkGenerator.cs:155-162` |
| P1 | Empty-delta unload skips/no-ops ambiguity | `WorldChunkStore.cs` + `UnloadChunkAsync` |
| P2 | Negative region blending edge values | `RegionTraits.cs:117-122` |
| P2 | LocalIndex > 4095 per chunk (walls only) | `WorldChunkDeltaCodec.cs:67` |

---

## 4. CPU → GPU АНАЛИЗ

### 4.1. Текущие затраты CPU на один чанк (64×64 = 4096 клеток)

Для каждой клетки вызывается:
```
SampleBaseCell():
  └─ CoordinateHash.ValueNoiseQ16(ElevationLarge,    scale=512)  → 4 hashes + 4 lerps
  ├─ CoordinateHash.ValueNoiseQ16(ElevationMedium,   scale=128)  → 4 hashes + 4 lerps
  ├─ CoordinateHash.ValueNoiseQ16(ElevationFine,     scale=32)   → 4 hashes + 4 lerps
  ├─ CoordinateHash.ValueNoiseQ16(ElevationDetail,   scale=16)   → 4 hashes + 4 lerps
  ├─ CoordinateHash.ValueNoiseQ16(MoistureLarge,     scale=256)  → 4 hashes + 4 lerps
  ├─ CoordinateHash.ValueNoiseQ16(MoistureFine,      scale=64)   → 4 hashes + 4 lerps
  ├─ CoordinateHash.ValueNoiseQ16(Forest,            scale=96)   → 4 hashes + 4 lerps
  └─ CoordinateHash.ValueNoiseQ16(Stone,             scale=48)   → 4 hashes + 4 lerps

= 32 хеша + 32 интерполяции на клетку
= 131 072 хеша + 131 072 лерпа на чанк
```

Один `ValueNoiseQ16` включает:
- 2 `FloorDiv` + 2 `FloorMod` (деление!)
- 4 `Hash()` вызова (каждый ≈ 6 XOR/mul/mix операций на ulong)
- 4 SmoothStep + 4 Lerp в Q16

**Предварительная оценка времени одного чанка на CPU:**
- ~130K хешей × ~5ns каждый = ~650μs
- ~130K лерпов × ~2ns каждый = ~260μs
- деления FloorDiv/FloorMod: ~100ns × 8 = ~800μs (самая тяжёлая часть на CPU!)
- **Итого: ~1.7ms на чанк** на одном ядре

При конвейерной генерации N workers × concurrent:
- При 4 worker'ах: ~4ms wall-clock на окно из ~20 видимых чанков
- Потребление CPU: один полный core при активной камере

### 4.2. GPU-архитектура (compute shader, WGSL/GLSL через Godot 4.7)

#### Что идеальное подходит для GPU:

| Элемент | Параллелизм | Память | Комментарий |
|---------|-------------|--------|-------------|
| `CoordinateHash.Hash()` | SIMD (warp-level) | Локальные регистры | Чистая целочисленная арифметика |
| `ValueNoiseQ16()` | warp-per-cell | Texture cache (lattice grid) | Логично мапится на texture fetch + lerp |
| `RegionTraitProvider.SampleBlended()` | per-cell | Uniform (5 sample calls) | Можно сделать 1 dispatch per chunk |
| `RegionTraitProvider.BlendBilinear()` | per-cell | Shared memory (corner traits) | Билинейная интерполяция — родная операция GPU |

#### Оптимальный GPU dispatch:

```
dispatch(x=1, y=1, z=1) → one compute shader block per chunk (64×64 cells)
├─ read corner-region-trait uniforms (5 regions × 5 shorts = 50 bytes per corner)
├─ for each cell (x,y):
│   ├─ compute bilinear blend weights u,v from x,y (simple math)
│   ├─ bilinear blend 5 trait fields from 4 corners
│   ├─ compute 8 × ValueNoiseQ16(domain, scale) with lattice-scale LOD
│   └─ apply terrain thresholds → write GeneratedCell struct
└─ write final WorldChunk flat buffer (each cell = Terrain+4×ushort = 9 bytes)
```

**Ожидаемая GPU производительность:**
- Dispatch cost: ~10μs (kernel launch) + ~64KB texture fetches
- 4096 threads → warp divergence minimal (all same path)
- **~0.05–0.1ms per chunk on modern GPU** (vs 1.7ms CPU)
- **Speedup: 17–34× per chunk**

### 4.3. Препятствия к GPU

| Проблема | Решение | Затраты |
|----------|---------|---------|
| Godot 4.7 C# API не имеет direct ComputeShader access | Использовать `ShaderMaterial` с `ComputeTask` через Vulkan ray/compute — либо писать GLSL шейдеры напрямую через `ResourceLoader.Load<Shader>` | Низкие |
| Transfer overhead host→GPU→host (~10–20μs на чанк) | Batch: generate 16–32 chunks per frame as one dispatch | Низкие |
| Partial chunk updates (deltas don't affect base terrain) | GPU generates base chunk; host applies deltas post-shader | Низкие |
| Debugging compute shaders сложнее CPU GDB | Использовать Godot's built-in render debugger + print via debug resources | Средние |

### 4.4. Verdict

**Да, перенос имеет смысл.** GPU даёт 17–34× ускорение на базовую генерацию terrain, но:

| Сценарий | CPU сейчас | GPU (оценка) | Нужно ли |
|----------|-----------|--------------|----------|
| Один кадр, 20 чанков генерации | 34ms | ~2ms | ✅ Да, при быстрой камере лаги заметны |
| Статичная сцена | <1ms background | ~0.5ms | ❌ Не критично |
| Mobile/weak GPU | ~0.5ms/chunk | ~2.5ms | ❌ Возможно хуже |
| Combined base + feature (rivers/lakes) | ~1.7ms + 50μs | ~0.1ms + 0.02ms | ✅ Разница менее важна |

### 4.5. Recommended implementation phases

```
Phase 0: Benchmark baseline (CPU)
  └─ добавить таймеры в WorldChunkGenerator.GenerateBaseChunk
  └─ измерить время на 100 чанках при разных координатах

Phase 1: Simple value-noise GPU shader (no blending)
  └─ написать compute shader для 8 domains, 1 dispatch per domain
  └─ сравнить результаты байт-в-байт с CPU
  └─ если match → Phase 2

Phase 2: Full pipeline (blending + thresholds)
  └─ передать corner traits как uniform arrays
  └─ bilerp прямо в shader, output GeneratedCell struct
  └─ replace WorldChunkGenerator.GenerateBaseChunk call

Phase 3: Fallback logic
  └─ detect unsupported GPU configs → auto-fallback to CPU
  └─ expose toggle: project setting rendering/use_gpu_world_gen
```

---

## 5. Чек-лист задач

- [ ] **T1** Добавить P0 тест на OverflowException в TileOrigin (+ fix)
- [ ] **T2** Добавить P0 тест на Update()/DrainRace → StreamerChurnTests (уже есть partial, улучшить coverage)
- [ ] **T3** Написать adversarial тест: RegionTraits blended at exact boundary (-1,-1) ↔ (0,0)
- [ ] **T4** Проверить deep-sea ring: все клетки [513..530]² дают Water террейн
- [ ] **T5** Добавить benchmark-тайминг в WorldChunkGenerator
- [ ] **T6** PoC: написать simple value-noise compute shader (GLSL)
- [ ] **T7** Validate: GPU results byte-for-byte == CPU results

---

## 6. Допущения

1. Godot 4.7 поддерживает Compute Shaders через RenderingDevice API.
2. T1–T4 можно проверить headless без GPU — просто запустить тестовую сцену.
3. Тест-сцена `infinite_world_game_demo.tscn` запускается headless с `AutoDemo=true`.

---

*План согласован. Реализация начнётся после аппрува.*
