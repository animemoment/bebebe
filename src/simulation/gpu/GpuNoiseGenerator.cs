using System;
using Godot;
using Game.Core;

namespace Game.Simulation.Gpu;

// GPU-версия fBm-генерации карт (пункт 4 PLAN.md).
// Зеркалит NoiseGenerator.GenerateFbmMap: те же клампы, та же нормировка ampSum,
// тот же цикл scale=baseScale/freq, seed+o*1013, amp/gain, freq*lacunarity.
// Ридбэк (ReadbackSync) — ОДИН раз после всех октав, в этом выигрыш пункта 4.
// При недоступности GPU — прозрачный fallback на CPU-версию.
// Интеграция в MapGenerator НЕ делается здесь (отдельный шаг после тестов равенства).
public static class GpuNoiseGenerator
{
    // Путь к шейдеру октавы (рядом лежит agent_movement.glsl — редактор подхватит .glsl сам).
    public const string ShaderPath = "res://src/simulation/gpu/fbm_noise.glsl";

    // Размер workgroup в шейдере — должен совпадать с local_size (16,16,1).
    private const uint WorkgroupX = 16;
    private const uint WorkgroupY = 16;

    /// <summary>
    /// Генерирует fBm-карту на GPU (пооктавные диспатчи, один ридбэк в конце).
    /// При недоступном контексте возвращает CPU-результат NoiseGenerator.GenerateFbmMap.
    /// </summary>
    public static float[,] GenerateFbmMapGpu(
        GpuComputeContext ctx,
        int width, int height,
        uint seed, float baseScale,
        int octaves = 4, float lacunarity = 2.0f, float gain = 0.5f)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Ширина карты должна быть > 0.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Высота карты должна быть > 0.");

        // ТОЧНО те же клампы, что GenerateFbmMap (строки 45-48): иначе рассинхрон с CPU.
        octaves = Math.Max(1, Math.Min(6, octaves));
        baseScale = Math.Max(4f, baseScale);
        lacunarity = Math.Clamp(lacunarity, 1.5f, 3.0f);
        gain = Math.Clamp(gain, 0.3f, 0.7f);

        // Та же нормировка по сумме амплитуд, что CPU (результат уже в [0..1]).
        float ampSum = 0f;
        float a = 1f;
        for (int o = 0; o < octaves; o++)
        {
            ampSum += a;
            a *= gain;
        }

        // Fallback: контекст не готов / Compatibility-рендер / исключение device.
        if (ctx == null || !ctx.EnsureInitialized() || !ctx.IsAvailable)
            return NoiseGenerator.GenerateFbmMap(width, height, seed, baseScale, octaves, lacunarity, gain);

        try
        {
            return DispatchAllOctaves(ctx, width, height, seed, baseScale, octaves, lacunarity, gain, ampSum);
        }
        catch (Exception ex)
        {
            // Любой сбой GPU (битый шейдер, невалидный Rid) — прозрачно уходим на CPU.
            GD.PrintErr($"[GpuNoiseGenerator] GPU-путь упал ({ex.GetType().Name}: {ex.Message}). CPU fallback.");
            return NoiseGenerator.GenerateFbmMap(width, height, seed, baseScale, octaves, lacunarity, gain);
        }
    }

    // Все октавные диспатчи + единственный ридбэк в конце.
    private static float[,] DispatchAllOctaves(
        GpuComputeContext ctx,
        int width, int height,
        uint seed, float baseScale,
        int octaves, float lacunarity, float gain,
        float ampSum)
    {
        Rid pipeline = ctx.GetOrCreatePipeline(ShaderPath, out Rid shader);
        if (!pipeline.IsValid || !shader.IsValid)
            return NoiseGenerator.GenerateFbmMap(width, height, seed, baseScale, octaves, lacunarity, gain);

        int cellCount = width * height;
        uint outBytes = (uint)(cellCount * 4);

        // Персистентный выходной буфер: перед ПЕРВОЙ октавой один раз заполняем нулями
        // (шейдер аккумулирует += поверх, как CPU делает map[x,y] += ...).
        // Хвост переиспользованного большего буфера (при уменьшении карты) не читается:
        // шейдер пишет/читает только id < width*height (гарды по mapW/mapH), ридбэк
        // разбирает только первые width*height float — поэтому заливаем нулями ровно outBytes.
        Rid outBuf = ctx.GetOrCreateBuffer("fbm_out", outBytes);
        ctx.BufferUpdate(outBuf, new byte[outBytes]);

        // Персистентный буфер решётки: один ключ на все октавы (GetOrCreateBuffer сам
        // пересоздаст при росте размера; при уменьшении переиспользует больший — ок).
        // UniformSet на октаву свой (трекается в контексте, чистится в ReleaseAll).
        float amp = 1f;
        float freq = 1f;
        for (int o = 0; o < octaves; o++)
        {
            // CPU считает scale=baseScale/freq, а внутри AccumulateOctave клампит scale>=2.
            // Повторяем оба шага, иначе решётка/координаты разъедутся с CPU-путём.
            float rawScale = baseScale / freq;
            float scale = Math.Max(2f, rawScale);
            uint octaveSeed = seed + (uint)(o * 1013);
            float weight = amp / ampSum;

            int gridW = (int)MathF.Ceiling(width / scale) + 2;
            int gridH = (int)MathF.Ceiling(height / scale) + 2;
            byte[] gridBytes = BuildGradGridBytes(octaveSeed, gridW, gridH);
            Rid gridBuf = ctx.GetOrCreateBuffer("fbm_grad", (uint)gridBytes.Length);
            ctx.BufferUpdate(gridBuf, gridBytes);

            Rid set = ctx.CreateUniformSet(shader, (0u, outBuf), (1u, gridBuf));
            byte[] push = BuildPushConstants((uint)width, (uint)height, (uint)gridW, (uint)gridH, scale, weight);

            uint xGroups = ((uint)width + WorkgroupX - 1u) / WorkgroupX;
            uint yGroups = ((uint)height + WorkgroupY - 1u) / WorkgroupY;
            ctx.Dispatch2D(pipeline, set, push, xGroups, yGroups);

            amp *= gain;
            freq *= lacunarity;
        }

        // Единственный Sync+readback на всю генерацию (не на октаву!).
        byte[] data = ctx.ReadbackSync(outBuf);
        var map = new float[width, height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                map[x, y] = BitConverter.ToSingle(data, (y * width + x) * 4);

        // Fail-safe от ЛЮБОГО расхождения GPU vs CPU (битая таблица градиентов,
        // драйверные FMA-округления, transpose-раскладка, мусор в хвосте буфера):
        // здоровая fBm-карта имеет среднее около 0.5 и заметный разброс.
        // Плоская/утопленная карта (water-world сигнатура) — повод откатиться на CPU.
        // Пересчёт по CPU чуть дольше, но карта всегда нормальная.
        if (!IsMapHealthy(map, width, height, out float mean, out float min, out float max))
        {
            GD.PrintErr($"[GpuNoiseGenerator] GPU-карта невалидна " +
                $"(mean={mean:F3} min={min:F3} max={max:F3}, {width}x{height} seed={seed}). CPU fallback.");
            return NoiseGenerator.GenerateFbmMap(width, height, seed, baseScale, octaves, lacunarity, gain);
        }
        return map;
    }

    // Валидация fBm-карты: mean в [0.30..0.70], размах max-min >= 0.15.
    // Пороги с запасом: у здоровой fBm mean ≈ 0.5, размах ≈ 0.4-0.6.
    private static bool IsMapHealthy(float[,] map, int width, int height, out float mean, out float min, out float max)
    {
        double sum = 0.0;
        min = float.MaxValue;
        max = float.MinValue;
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                float v = map[x, y];
                // NaN/Infinity от битого GPU-буфера — сразу невалидно.
                if (float.IsNaN(v) || float.IsInfinity(v))
                {
                    mean = float.NaN;
                    min = float.NaN;
                    max = float.NaN;
                    return false;
                }
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }
        mean = (float)(sum / (width * height));
        if (mean < 0.30f || mean > 0.70f)
            return false;
        if (max - min < 0.15f)
            return false;
        return true;
    }

    // Решётка индексов градиентов ТЕМ ЖЕ генератором, что CPU-путь:
    // new Random((int)seed), внешний цикл gx, внутренний gy, rng.Next(8).
    // Раскладка — ТОЧНАЯ копия памяти C# int[gridW,gridH]: flat[gx*gridH+gy]
    // (многомерный массив в C# — row-major по ПЕРВОМУ индексу).
    // Шейдер читает gradIdx[x*gridH+y] и получает ровно то же значение,
    // что CPU gradientIndices[x,y]. Старый вариант flat[gy*gridW+gx] давал
    // ТРАНСПОНИРОВАННУЮ решётку (слот [a,b] ↔ [b,a]) — валидный, но другой шум.
    // Дублирует ~10 строк AccumulateOctave сознательно: поведение CPU не меняем.
    private static byte[] BuildGradGridBytes(uint seed, int gridW, int gridH)
    {
        var rng = new Random(unchecked((int)seed));
        uint[] flat = new uint[gridW * gridH];
        for (int gx = 0; gx < gridW; gx++)
            for (int gy = 0; gy < gridH; gy++)
                flat[gx * gridH + gy] = (uint)rng.Next(8);
        byte[] bytes = new byte[flat.Length * 4];
        Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    // Push-константы шейдера: mapW, mapH, gridW, gridH, scale, weight = 24 байта (< 128).
    // Порядок полей обязан совпадать с layout(push_constant) в fbm_noise.glsl.
    private static byte[] BuildPushConstants(uint mapW, uint mapH, uint gridW, uint gridH, float scale, float weight)
    {
        byte[] bytes = new byte[24];
        Buffer.BlockCopy(BitConverter.GetBytes(mapW), 0, bytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(mapH), 0, bytes, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(gridW), 0, bytes, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(gridH), 0, bytes, 12, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(scale), 0, bytes, 16, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(weight), 0, bytes, 20, 4);
        return bytes;
    }
}
