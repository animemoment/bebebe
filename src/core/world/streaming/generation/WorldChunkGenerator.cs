using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Game.Core.WorldStreaming.Gpu;

namespace Game.Core.WorldStreaming;

/// <summary>
/// Изолированный генератор базового слоя. Семплы адресуются абсолютными long-координатами;
/// chunk API лишь пакетирует те же значения и не влияет на результат.
/// Пороги рельефа смещаются характеристиками региона (§23 шаг 2): характеристики
/// билинейно сглажены через границы регионов, поэтому тайловых швов нет.
/// 
/// GPU путь: dispatch compute shader через RenderingDevice. При любой ошибке — падение в CPU.
/// </summary>
public static class WorldChunkGenerator
{
    /// <summary>Использовать параллельную генерацию (Parallel.For) вместо single-thread loop.</summary>
    public static bool UseParallelGeneration { get; set; } = true;
    
    // GPU акселератор — ленивая инициализация при первом вызове GenerateBaseChunk.
    private static GpuChunkGenerator _gpuGen = null;
    private static readonly object _gpuLock = new();
    private static bool _gpuChecked = false;

    private const int Q16Center = 32768;
    private const int Q16Max = ushort.MaxValue;

    // Сила регионального смещения (в Q16-единицах) при крайнем значении характеристики.
    private const int ElevationStrength = 18000;     // региональные моря/нагорья
    private const int ElevationLocalStrength = 9000; // локальные гряды/озёра
    private const int MoistureStrength = 16000;
    private const int ForestStrength = 16000;
    private const int StoneStrength = 12000;
    private const int WaterStrength = 12000;         // базовый порог воды
    private const int WaterLowStrength = 6000;       // порог для низменных вод/болот

    private const uint CurrentGeneratorVersion = 3u;

    /// <summary>Сколько чанков успешно сгенерировано через GPU за сессию.</summary>
    public static long ChunksGpuCount => _gpuGen?.ChunksGeneratedGpu ?? 0;
    
    /// <summary>Статус GPU пути (для диагностики/HUD).</summary>
    public static string GpuStatus => _gpuChecked ? (_gpuGen?.Status ?? "N/A") : "Not yet checked";
    
    /// <summary>Генерация базового чанка: сначала GPU, при неудаче — CPU parallel/sequential.</summary>
    public static WorldChunk GenerateBaseChunk(ulong worldSeed, uint generatorVersion, ChunkKey key)
    {
        EnsureGpu();
        
        GeneratedCell[] cells;
        
        if (_gpuGen != null && _gpuGen.IsAvailable)
        {
            try
            {
                cells = _gpuGen.GenerateChunk(worldSeed, generatorVersion, key, CpuFallback);
                if (cells != null) return new WorldChunk(key, cells);
            }
            catch
            {
                // Fallback to CPU below
            }
        }
        
        cells = CpuFallback(worldSeed, generatorVersion, key);
        return new WorldChunk(key, cells);
    }
    
    private static GeneratedCell[] CpuFallback(ulong seed, uint genVer, ChunkKey key)
    {
        var cells = new GeneratedCell[WorldChunk.CellCount];
        var origin = TileOrigin(key);
        
        Stopwatch sw = Stopwatch.StartNew();
        
        if (UseParallelGeneration)
        {
            Parallel.For(0, WorldChunk.CellCount, i =>
            {
                int localX = i % WorldChunk.Side;
                int localY = i / WorldChunk.Side;
                long wx = checked(origin.X + localX);
                long wy = checked(origin.Y + localY);
                cells[i] = SampleCell(seed, genVer, wx, wy);
            });
        }
        else
        {
            for (int localY = 0; localY < WorldChunk.Side; localY++)
            {
                long worldY = checked(origin.Y + localY);
                int row = localY * WorldChunk.Side;
                for (int localX = 0; localX < WorldChunk.Side; localX++)
                {
                    long worldX = checked(origin.X + localX);
                    cells[row + localX] = SampleCell(seed, genVer, worldX, worldY);
                }
            }
        }
        
        sw.Stop();
        WorldChunkGeneratorTiming.Record(key, sw.ElapsedMilliseconds);
        return cells;
    }
    
    private static void EnsureGpu()
    {
        lock (_gpuLock)
        {
            if (_gpuChecked || _gpuGen != null) return;
            _gpuChecked = true;
            
            try
            {
                _gpuGen = new GpuChunkGenerator();
                _gpuGen.Init();
                Debug.WriteLine($"[WorldChunkGen] GPU init result: {_gpuGen.Status}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WorldChunkGen] GPU init failed: {ex.Message}");
                _gpuGen = null;
            }
        }
    }
    
    /// <summary>Мировой origin чанка по координатам X,Y.</summary>
    public static LocalCell TileOrigin(ChunkKey key)
        => WorldCoordinates.LocalForTile(key.X * WorldChunk.Side, key.Y * WorldChunk.Side);
    
    /// <summary>Семпл одной клетки мира без привязки к чанку.</summary>
    public static GeneratedCell SampleCell(ulong worldSeed, uint generatorVersion,
        long worldX, long worldY)
        => SampleBaseCell(worldSeed, generatorVersion, worldX, worldY,
            RegionTraitProvider.SampleBlended(worldSeed, generatorVersion, worldX, worldY));

    private static GeneratedCell SampleBaseCell(ulong worldSeed, uint generatorVersion,
        long worldX, long worldY, RegionTraits traits)
    {
        ushort elevationLarge = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.ElevationLarge, worldX, worldY, 512);
        ushort elevationMedium = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.ElevationMedium, worldX, worldY, 128);
        ushort elevationFine = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.ElevationFine, worldX, worldY, 32);
        ushort elevationDetail = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.ElevationDetail, worldX, worldY, 16);

        ushort regional = WeightedQ16(elevationLarge, 50, elevationMedium, 50);
        ushort local = WeightedQ16(elevationFine, 55, elevationDetail, 45);

        regional = ApplyBias(regional, traits.ElevationQ16, ElevationStrength);
        local = ApplyBias(local, traits.ElevationQ16, ElevationLocalStrength);
        ushort elevation = WeightedQ16(regional, 50, local, 50);

        ushort moistureLarge = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.MoistureLarge, worldX, worldY, 256);
        ushort moistureFine = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.MoistureFine, worldX, worldY, 64);
        ushort moisture = WeightedQ16(moistureLarge, 68, moistureFine, 32);
        moisture = ApplyBias(moisture, traits.MoistureQ16, MoistureStrength);
        ushort forest = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.Forest, worldX, worldY, 96);
        forest = ApplyBias(forest, traits.ForestQ16, ForestStrength);
        ushort stone = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.Stone, worldX, worldY, 48);
        stone = ApplyBias(stone, traits.StoneQ16, StoneStrength);

        // Температура: широтный градиент + региональный bias.
        // Северные регионы холоднее, южные теплее; тропики в середине мира.
        // Bias от RegionTrait.Temperature позволяет континентам иметь свой климат.
        ushort latitudeBias = LatitudeTemperatureBias(worldY);
        ushort temperatureNoise = CoordinateHash.ValueNoiseQ16(worldSeed, generatorVersion,
            GenerationDomain.Temperature, worldX, worldY, 256);
        ushort temperature = WeightedQ16(latitudeBias, 60, temperatureNoise, 40);
        temperature = ApplyBias(temperature, traits.TemperatureQ16, 14000);

        long waterDelta = BiasDelta(traits.WaterQ16, WaterStrength);
        long waterLowDelta = BiasDelta(traits.WaterQ16, WaterLowStrength);

        BaseTerrainKind terrain;
        if (regional < 20_000 + waterDelta)
            terrain = BaseTerrainKind.Water;
        else if (regional > 46_000)
            terrain = BaseTerrainKind.Mountain;
        else if (regional < 31_000 + waterLowDelta && local < 12_000)
            terrain = BaseTerrainKind.Water;
        else if (local > 52_000)
            terrain = BaseTerrainKind.Mountain;
        else
            terrain = BaseTerrainKind.Grass;
        return new GeneratedCell(terrain, elevation, moisture, forest, stone, temperature);
    }

    public static GeneratedCell SampleBaseCell(ulong worldSeed, uint generatorVersion,
        long worldX, long worldY)
        => SampleBaseCell(worldSeed, generatorVersion, worldX, worldY,
            RegionTraitProvider.SampleBlended(worldSeed, generatorVersion, worldX, worldY));

    private static long BiasDelta(ushort bias, int strength)
        => ((long)bias - Q16Center) * strength / Q16Center;

    private static ushort ApplyBias(ushort value, ushort bias, int strength)
    {
        long shifted = (long)value + BiasDelta(bias, strength);
        return (ushort)Math.Clamp(shifted, 0, Q16Max);
    }

    private static ushort WeightedQ16(ushort a, int weightA, ushort b, int weightB)
    {
        long sum = (long)a * weightA + (long)b * weightB;
        return (ushort)((sum + (weightA + weightB) / 2) / (weightA + weightB));
    }

    private static ushort WeightedQ16(ushort a, int weightA, ushort b, int weightB,
        ushort c, int weightC)
    {
        long sum = (long)a * weightA + (long)b * weightB + (long)c * weightC;
        return (ushort)((sum + (weightA + weightB + weightC) / 2) / (weightA + weightB + weightC));
    }

    /// <summary>
    /// Широтный температурный bias: полюса = холод (низкое Q16), экватор = жара (высокое Q16).
    //  Масштаб: ±Y = ±30° — 45° при ~7800 cells per degree (1 ChunkSize × 15 ≈ 960, но масштаб глобальный)
    //  Используем Y / 10_000 как грубую нормализацию широты: |y| > 80_000 → полярная зона.
    /// </summary>
    private static ushort LatitudeTemperatureBias(long worldY)
    {
        // Нормализованная "широта": -1.0 (южный полюс) .. 0.0 (экватор) .. 1.0 (северный полюс)
        float lat = Math.Clamp((float)worldY / 100_000f, -1f, 1f);

        // Климатические зоны:
        // Полюса (|lat| > 0.7): Ice/Cold — TempQ16 ≈ 0..12000
        // Умеренные (0.3 < |lat| ≤ 0.7): Temperate — TempQ16 ≈ 20000..40000  
        // Тропики (|lat| ≤ 0.3): Hot — TempQ16 ≈ 40000..60000
        if (Math.Abs(lat) > 0.7f)
        {
            // Полярная зона: линейный градиент к нулю
            float poleFactor = Lerp(1f, 0f, (Math.Abs(lat) - 0.7f) / 0.3f);
            return (ushort)Lerp(12000, 6000, poleFactor);
        }
        if (Math.Abs(lat) > 0.3f)
        {
            // Умеренная зона: плавный переход от полюсов/тропиков
            float warmFactor = InverseLerp(0.3f, 0.7f, 1f - Math.Abs(lat));
            return (ushort)Lerp(20000, 40000, warmFactor);
        }
        // Тропическая зона: жарко и влажно
        return (ushort)Lerp(40000, 60000, Math.Abs(lat) / 0.3f);
    }

    // Вспомогательные функции для чистого C# (без Godot API)
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    
    private static float InverseLerp(float a, float b, float v)
        => b != a ? (v - a) / (b - a) : 0f;
    
    private const uint Q16One = 65535;
}
