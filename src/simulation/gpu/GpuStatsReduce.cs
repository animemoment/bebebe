using System;
using Godot;
using Game.Core;

namespace Game.Simulation.Gpu;

// GPU-редукция статистики агентов (пункт 5): НОВЫЕ метрики, которые на CPU
// дороги (полный проход по 10k агентов), а HUD показывает раз в секунду —
// средний голод, средний Mood, доля уставших (Fatigue > 80) и распределение
// по состояниям (5 корзин AgentState: Idle/MovingToSource/Working/
// MovingToTarget/Evacuating).
//
// Архитектура: существующий путь HUD RefreshStats (1 Гц, кэшированные тоталы
// total/unemployed/employed, Text-при-изменении) НЕ трогается и НЕ заменяется.
// Этот класс добавляет параллельный путь: один Dispatch1D + ДВА маленьких
// readback (Partial numGroups×vec4 + Hist 5×uint — байты, не мегабайты!),
// свёртка в средние на CPU, публикация заменой volatile-ссылки Last.
// CPU-кэш обновляется не чаще 1 раза в 2 сек wall-clock. Без GPU Last = null
// и HUD показывает старые строки бит-в-бит (null-safe чтение).
//
// TODO-точка вызова Tick: AgentSimulationThread после PushSnapshot (sim-поток,
// там _pool доступен напрямую). Local RenderingDevice потокобезопасен, у Tick
// свой Submit через GpuComputeContext. Из HUD Tick НЕ вызывается (у HUD нет
// доступа к pool) — HUD только ЧИТАЕТ Last.
//
// Fallback везде: GPU недоступен / шейдер битый / исключение → тихий return,
// Last остаётся старым (или null). Сим не роняем никогда.
public sealed class GpuStatsReduce
{
    public static GpuStatsReduce Instance { get; } = new();

    // Неизменяемый снапшот метрик: публикуется заменой ссылки (volatile),
    // читается из HUD без локов. Hist5 — копия на снапшот (5 int раз в 2 сек).
    public sealed class StatsSnapshot
    {
        public readonly float AvgHunger;
        public readonly float AvgMood;
        public readonly float TiredFrac;
        public readonly int[] Hist5;

        public StatsSnapshot(float avgHunger, float avgMood, float tiredFrac, int[] hist5)
        {
            AvgHunger = avgHunger;
            AvgMood = avgMood;
            TiredFrac = tiredFrac;
            Hist5 = hist5;
        }
    }

    public const string ReduceShaderPath = "res://src/simulation/gpu/stats_reduce.glsl";

    // Размер workgroup в шейдере — обязан совпадать с local_size (256,1,1).
    private const uint WorkgroupSize = 256;
    // Hist = 5 корзин AgentState × uint.
    private const int HistBins = 5;
    // Порог усталости — обязан совпадать с константой 80.0 в шейдере
    // (= AgentNeedsConfig.FatigueSlowThreshold / FatigueRestThreshold).
    private const float TiredThreshold = 80f;
    // Троттлинг CPU-кэша: редукция не чаще раза в 2 сек wall-clock.
    private const double ThrottleSec = 2.0;
    // Пауза перед повторной попыткой инициализации GPU после провала,
    // чтобы без GPU не дёргать CreateLocalRenderingDevice каждые 2 сек.
    private const double FailCooldownSec = 30.0;

    private readonly object _lock = new();
    private readonly GpuComputeContext _ctx = new();

    // Публикация — заменой ссылки, чтение — без лока.
    private volatile StatsSnapshot _last;
    public StatsSnapshot Last => _last;

    private double _lastTickWallSec = double.NegativeInfinity;
    private double _lastFailWallSec = double.NegativeInfinity;
    private bool _errorLogged;

    // Переиспользуемые буферы (grow-only, аллокаций в Tick нет кроме снапшота):
    // входные States→uint[] конверсия (10k, дёшево), byte-заливки через BlockCopy,
    // float-приёмник partial и uint-приёмник hist для свёртки.
    private uint[] _stateUint;
    private byte[] _hungerBytes;
    private byte[] _moodBytes;
    private byte[] _fatigueBytes;
    private byte[] _stateBytes;
    private float[] _partialFloats;
    private uint[] _histUint;
    // Нулевая заливка Hist (20 байт): отдельного clear-пасса в шейдере нет.
    private static readonly byte[] ZeroHist = new byte[HistBins * 4];

    private GpuStatsReduce()
    {
    }

    // Редукция статистики pool (троттлинг 2 сек wall-clock, один диспатч +
    // два маленьких readback). Вызывать из sim-потока (кандидат: рядом с
    // PushSnapshot в AgentSimulationThread). Без GPU — тихий no-op.
    public void Tick(AgentDataPool pool)
    {
        if (pool == null)
            return;
        int count = pool.Capacity;
        if (count <= 0)
            return;

        lock (_lock)
        {
            double wallSec = System.Environment.TickCount64 / 1000.0;
            if ((wallSec - _lastTickWallSec) < ThrottleSec)
                return; // троттлинг: 2 сек ещё не прошло
            if ((wallSec - _lastFailWallSec) < FailCooldownSec)
                return; // кулдаун провалов: GPU точно нет, не дёргаем device

            if (!_ctx.EnsureInitialized() || !_ctx.IsAvailable)
            {
                _lastFailWallSec = wallSec; // тихий CPU fallback, без спама
                return;
            }

            try
            {
                if (!DispatchReduce(pool, count))
                {
                    _lastFailWallSec = wallSec;
                    return;
                }
                _lastTickWallSec = wallSec;
            }
            catch (Exception ex)
            {
                // Любой сбой GPU — прозрачно в fallback, сим не роняем. Лог один раз.
                _lastFailWallSec = wallSec;
                LogErrorOnce($"GPU-путь упал ({ex.GetType().Name}: {ex.Message}). CPU fallback.");
            }
        }
    }

    // Один диспатч + два маленьких readback + свёртка на CPU + публикация.
    // Возврат false = тихий fallback. Вызывается под _lock из Tick.
    private bool DispatchReduce(AgentDataPool pool, int count)
    {
        Rid pipeline = _ctx.GetOrCreatePipeline(ReduceShaderPath, out Rid shader);
        if (!pipeline.IsValid || !shader.IsValid)
            return false; // шейдер битый — ошибка уже залогирована в контексте

        uint numGroups = (uint)((count + (int)WorkgroupSize - 1) / (int)WorkgroupSize);
        uint inByteSize = (uint)(count * 4);
        uint partialByteSize = numGroups * 16u; // numGroups × vec4
        uint histByteSize = HistBins * 4u; // 5 × uint = 20 байт

        Rid hungerBuf = _ctx.GetOrCreateBuffer("stats_hunger", inByteSize);
        Rid moodBuf = _ctx.GetOrCreateBuffer("stats_mood", inByteSize);
        Rid fatigueBuf = _ctx.GetOrCreateBuffer("stats_fatigue", inByteSize);
        Rid stateBuf = _ctx.GetOrCreateBuffer("stats_state", inByteSize);
        Rid partialBuf = _ctx.GetOrCreateBuffer("stats_partial", partialByteSize);
        Rid histBuf = _ctx.GetOrCreateBuffer("stats_hist", histByteSize);

        // Заливка входов: float[] → byte[] через BlockCopy (как делает
        // AgentGpuComputeService); States (byte-enum) → uint[] конверсия на CPU.
        EnsureByteSize(ref _hungerBytes, (int)inByteSize);
        EnsureByteSize(ref _moodBytes, (int)inByteSize);
        EnsureByteSize(ref _fatigueBytes, (int)inByteSize);
        EnsureByteSize(ref _stateBytes, (int)inByteSize);
        Buffer.BlockCopy(pool.Hunger, 0, _hungerBytes, 0, (int)inByteSize);
        Buffer.BlockCopy(pool.Mood, 0, _moodBytes, 0, (int)inByteSize);
        Buffer.BlockCopy(pool.Fatigue, 0, _fatigueBytes, 0, (int)inByteSize);
        if (_stateUint == null || _stateUint.Length < count)
            _stateUint = new uint[count];
        var states = pool.States;
        for (int i = 0; i < count; i++)
            _stateUint[i] = (uint)states[i]; // AgentState : byte → 0..4
        Buffer.BlockCopy(_stateUint, 0, _stateBytes, 0, (int)inByteSize);

        _ctx.BufferUpdate(hungerBuf, _hungerBytes);
        _ctx.BufferUpdate(moodBuf, _moodBytes);
        _ctx.BufferUpdate(fatigueBuf, _fatigueBytes);
        _ctx.BufferUpdate(stateBuf, _stateBytes);
        _ctx.BufferUpdate(histBuf, ZeroHist); // обнулить гистограмму (атомики +=)

        // UniformSet на шаг свой (трекается в контексте до ReleaseAll — тот же
        // паттерн, что GpuCropField/GpuFlowField). Барьеры не нужны: Dispatch1D
        // делает Submit, 2-сек интервал исключает гонки.
        Rid set = _ctx.CreateUniformSet(shader,
            (0u, hungerBuf), (1u, moodBuf), (2u, fatigueBuf),
            (3u, stateBuf), (4u, partialBuf), (5u, histBuf));

        _ctx.Dispatch1D(pipeline, set, BuildPush((uint)count, numGroups), numGroups);

        // ДВА маленьких readback (Sync + копия): Partial (numGroups vec4) и
        // Hist (5 uint). При 10k агентов: 40 групп × 16 Б = 640 Б + 20 Б.
        byte[] partialData = _ctx.ReadbackSync(partialBuf);
        byte[] histData = _ctx.ReadbackSync(histBuf);

        int groups = (int)numGroups;
        if (_partialFloats == null || _partialFloats.Length < groups * 4)
            _partialFloats = new float[groups * 4];
        Buffer.BlockCopy(partialData, 0, _partialFloats, 0,
            Math.Min(partialData.Length, groups * 16));
        double sumH = 0.0, sumM = 0.0, sumT = 0.0, sumN = 0.0;
        for (int g = 0; g < groups; g++)
        {
            sumH += _partialFloats[g * 4];
            sumM += _partialFloats[g * 4 + 1];
            sumT += _partialFloats[g * 4 + 2];
            sumN += _partialFloats[g * 4 + 3];
        }
        if (sumN <= 0.0)
            return false; // пустой проход — снапшот не публикуем

        if (_histUint == null || _histUint.Length < HistBins)
            _histUint = new uint[HistBins];
        Buffer.BlockCopy(histData, 0, _histUint, 0,
            Math.Min(histData.Length, HistBins * 4));
        var hist = new int[HistBins]; // копия на снапшот (иммутабельность)
        for (int b = 0; b < HistBins; b++)
            hist[b] = (int)_histUint[b];

        // Публикация заменой volatile-ссылки (HUD читает без лока).
        _last = new StatsSnapshot(
            (float)(sumH / sumN),
            (float)(sumM / sumN),
            (float)(sumT / sumN),
            hist);
        return true;
    }

    // Push stats_reduce: count, numGroups = 8 байт (< 128).
    // Порядок полей обязан совпадать с layout(push_constant) в шейдере.
    private static byte[] BuildPush(uint count, uint numGroups)
    {
        byte[] bytes = new byte[8];
        Buffer.BlockCopy(BitConverter.GetBytes(count), 0, bytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(numGroups), 0, bytes, 4, 4);
        return bytes;
    }

    private static void EnsureByteSize(ref byte[] buf, int size)
    {
        if (buf == null || buf.Length < size)
            buf = new byte[size];
    }

    private void LogErrorOnce(string reason)
    {
        lock (_lock)
        {
            if (_errorLogged)
                return;
            _errorLogged = true;
        }
        GD.PrintErr($"[GpuStatsReduce] {reason}");
    }
}
