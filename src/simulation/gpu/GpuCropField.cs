using System;
using Godot;
using Game.Core;

namespace Game.Simulation.Gpu;

// GPU-диффузия влажности почвы (пункт 2): ВТОРИЧНОЕ поле-бонус роста культур.
// CPU-источник истины (CropGrowthManager._crops: lock, стадии 1-4, GrowthTimer,
// RegisterHarvest) НЕ трогается — GPU считает только влажность (стенсил
// фон-Нейман, ping-pong moistA/moistB, вода = вечный источник 1.0,
// гора/стена = 0). CropGrowthManager.UpdateGrowth ускоряет GrowthTimer
// множителем (1 + GetBonus(x,y) * BonusScale); без GPU/свежего кэша бонус = 0
// и поведение бит-в-бит как раньше (регрессия невозможна по построению).
//
// Троттлинг: шаг диффузии — ОДИН Dispatch2D, не чаще раза в 5 сек игрового
// времени (влажность медленная). Readback — только вместе с шагом (тоже ≤1/5с),
// между readback'ами GetBonus читает CPU-кэш (float[]). Readback каждый тик НЕТ.
//
// Плодородие (Fert) отдельным буфером НЕ заливаем сознательно: оно статично,
// бонус = moist как есть. Отдельный ключ "crop_blocked" (не ff_blocked):
// у flowfield вода = блок (бинарно), здесь вода = источник (тернарно 0/1/2),
// жизненные циклы разные.
//
// Fallback везде: GPU недоступен / шейдер битый / исключение → Tick тихий
// no-op, IsFresh = false, GetBonus = 0. Сим не роняем никогда.
public sealed class GpuCropField
{
    public static GpuCropField Instance { get; } = new();

    // Шейдер рядом с остальными gpu-шейдерами (импорт RDShaderFile подхватит редактор).
    public const string DiffuseShaderPath = "res://src/simulation/gpu/crop_diffuse.glsl";

    // Макс. ускорение роста: на болоте (moist = 1) таймер идёт в 1.5 раза быстрее.
    public const float BonusScale = 0.5f;

    // Размер workgroup в шейдере — обязан совпадать с local_size (16,16,1).
    private const uint WorkgroupX = 16;
    private const uint WorkgroupY = 16;

    // Шаг диффузии + readback — не чаще раза в 5 сек игрового времени.
    private const float StepIntervalSec = 5.0f;
    // Пауза перед повторной попыткой инициализации GPU после провала (wall-clock),
    // чтобы при отсутствии GPU не дёргать CreateLocalRenderingDevice каждый вызов.
    private const double FailCooldownSec = 30.0;

    // Параметры стенсила (уходят в push-константы шейдера).
    private const float EvapRate = 0.999f;  // испарение за шаг
    private const float DiffuseRate = 0.2f; // доля среднего соседей в mix

    // Начальная влажность (EnsureMoisture при первой заливке): вода = 1.0,
    // клетка с водой в 4-окрестности = 0.5, иначе = 0.2. Дальше влажность живёт
    // только диффузией в шейдере (вода подпитывает как гран условие = 1.0).
    private const float MoistWater = 1.0f;
    private const float MoistNearWater = 0.5f;
    private const float MoistDry = 0.2f;

    private readonly object _lock = new();
    private readonly GpuComputeContext _ctx = new();

    // CPU-кэш последнего readback: публикация — заменой ссылки, чтение — без лока.
    private volatile float[] _cache; // null = не свежее
    private volatile int _mapW;
    private volatile int _mapH;

    private bool _parity; // false: A→B, true: B→A
    private bool _gpuUploaded; // blocked + moistA залиты под текущий размер
    private float _lastTickGameTime = float.NegativeInfinity;
    // Нижняя граница по wall-clock для шага диффузии (2 реальные секунды):
    // StepIntervalSec — игровой (5с), на 100x один проход = 8 игросек, троттлинг
    // проходил бы КАЖДЫЙ кадр → Dispatch + Sync-readback каждый кадр.
    // Game-условие остаётся верхним (оба должны пройти: gameOk && wallOk).
    private double _lastStepWallSec = double.NegativeInfinity;
    private const double StepWallMinIntervalSec = 2.0;
    private double _lastFailWallSec = double.NegativeInfinity;
    private bool _errorLogged;

    private GpuCropField()
    {
    }

    // true, если есть свежий кэш влажности (GetBonus отдаёт реальные данные).
    public bool IsFresh => _cache != null;

    // Карта сменилась (новая генерация / загрузка): кэш протухает, GPU-буферы
    // перезальются при следующем Tick. Кто зовёт: TODO — точки вызова Invalidate
    // при смене карты пока НЕ подключены (генератор карты / загрузка сейва должны
    // позвать Invalidate()); без вызова Tick сам детектит смену РАЗМЕРА и
    // сбрасывается, но при том же размере и другой карте кэш останется старым —
    // безопасно (бонус ≤ +50%, логика стадий/словаря не затронута), но не оптимально.
    public void Invalidate()
    {
        lock (_lock)
        {
            _cache = null;
            _gpuUploaded = false;
            _parity = false;
            _lastTickGameTime = float.NegativeInfinity;
            _lastStepWallSec = double.NegativeInfinity;
        }
    }

    // Шаг диффузии (троттлинг 5 сек игрового времени) + readback в CPU-кэш.
    // Вызывать редко (влажность медленная). Кто зовёт: TODO — точка вызова Tick
    // пока НЕ подключена; кандидат — рядом с UpdateGrowth в AgentSimulationThread
    // (там же, где _cropGrowthTimer, передавать GameTimeSeconds).
    // gameDt — игровое время в секундах (монотонное; при откате времени назад
    // шаг выполняется вне очереди, а не виснет в троттлинге).
    public void Tick(float gameDt, SimulationContext ctx)
    {
        if (ctx == null)
            return;
        int mapW = ctx.MapWidth;
        int mapH = ctx.MapHeight;
        if (mapW <= 0 || mapH <= 0)
            return;

        // Весь Tick под _lock: Tick редкий (≤1/5с), зато parity, заливка
        // и публикация кэша атомарны без дополнительных флагов.
        // GpuComputeContext потокобезопасен сам; обратного порядка локов
        // (его lock → наш) нигде нет — дедлока нет.
        lock (_lock)
        {
            if (mapW != _mapW || mapH != _mapH)
            {
                // Размер сменился без Invalidate — сбрасываемся сами.
                _cache = null;
                _mapW = mapW;
                _mapH = mapH;
                _gpuUploaded = false;
                _parity = false;
                _lastTickGameTime = float.NegativeInfinity;
                _lastStepWallSec = double.NegativeInfinity;
            }
            else if (_lastTickGameTime != float.NegativeInfinity
                && gameDt >= _lastTickGameTime
                && (gameDt - _lastTickGameTime) < StepIntervalSec)
            {
                return; // троттлинг: 5 сек ещё не прошло
            }

            // Нижняя граница по wall-clock: шаг не чаще раза в 2 реальные секунды
            // (game-условие выше — верхнее, оба должны пройти). На 100x игровой
            // таймер проходит каждый кадр, без wall-гейта — Dispatch каждый кадр.
            double stepWallSec = System.Environment.TickCount64 / 1000.0;
            if ((stepWallSec - _lastStepWallSec) < StepWallMinIntervalSec)
                return;

            // Кулдаун провалов (wall-clock): при отсутствии GPU не дёргаем
            // CreateLocalRenderingDevice каждый вызов.
            double wallSec = stepWallSec;
            if ((wallSec - _lastFailWallSec) < FailCooldownSec)
                return;

            if (!_ctx.EnsureInitialized() || !_ctx.IsAvailable)
            {
                _lastFailWallSec = wallSec; // тихий CPU fallback, без спама
                return;
            }

            try
            {
                if (!DispatchStep(ctx, mapW, mapH))
                {
                    _lastFailWallSec = wallSec;
                    return;
                }
                _lastTickGameTime = gameDt;
                _lastStepWallSec = stepWallSec;
            }
            catch (Exception ex)
            {
                // Любой сбой GPU — прозрачно в fallback, сим не роняем. Лог один раз.
                _lastFailWallSec = wallSec;
                LogErrorOnce($"GPU-путь упал ({ex.GetType().Name}: {ex.Message}). CPU fallback.");
            }
        }
    }

    // Бонус роста из CPU-кэша (GPU на каждый запрос НЕ дёргается).
    // OOB / нет кэша → 0 (поведение роста = старое). Lock-free: только
    // volatile-чтения; вызывается из-под lock'а CropGrowthManager — своих
    // локов не берёт, порядка локов не нарушает. Кламп [0,1] — защита от
    // мусора при гонке публикации (по построению шейдер уже клампит).
    public float GetBonus(int x, int y)
    {
        float[] cache = _cache; // атомарное чтение ссылки, без лока
        if (cache == null)
            return 0f;
        int w = _mapW;
        int h = _mapH;
        if ((uint)x >= (uint)w || (uint)y >= (uint)h)
            return 0f;
        int idx = y * w + x;
        if ((uint)idx >= (uint)cache.Length)
            return 0f; // гонка смены размера — отдаём 0, следующий Tick пересчитает
        float v = cache[idx];
        if (v < 0f)
            return 0f;
        if (v > 1f)
            return 1f;
        return v;
    }

    // Один шаг: при необходимости залить blocked + moistA, Dispatch2D
    // ping-pong (A→B / B→A), readback приёмника в CPU-кэш.
    // Возврат false = тихий fallback. Вызывается под _lock из Tick.
    private bool DispatchStep(SimulationContext ctx, int mapW, int mapH)
    {
        Rid pipeline = _ctx.GetOrCreatePipeline(DiffuseShaderPath, out Rid shader);
        if (!pipeline.IsValid || !shader.IsValid)
            return false; // шейдер битый — ошибка уже залогирована в контексте

        int cellCount = mapW * mapH;
        uint byteSize = (uint)(cellCount * 4);

        Rid moistA = _ctx.GetOrCreateBuffer("crop_moistA", byteSize);
        Rid moistB = _ctx.GetOrCreateBuffer("crop_moistB", byteSize);
        Rid blockedBuf = _ctx.GetOrCreateBuffer("crop_blocked", byteSize);

        if (!_gpuUploaded)
        {
            _ctx.BufferUpdate(blockedBuf, BuildBlockedBytes(ctx, mapW, mapH));
            _ctx.BufferUpdate(moistA, BuildInitialMoistureBytes(ctx, mapW, mapH));
            // moistB инициализировать не нужно: каждый проход перезаписывает ВСЕ
            // клетки (blocked → 0, вода → 1.0, остальные → формула), мусора нет.
            _gpuUploaded = true;
            _parity = false;
        }

        Rid src = _parity ? moistB : moistA;
        Rid dst = _parity ? moistA : moistB;
        // UniformSet на шаг свой (трекается в контексте до ReleaseAll — тот же
        // паттерн, что GpuFlowField/GpuNoiseGenerator). Барьеры между шагами НЕ
        // нужны: Dispatch2D делает Submit, 5-сек интервал исключает гонки.
        Rid set = _ctx.CreateUniformSet(shader, (0u, src), (1u, dst), (2u, blockedBuf));

        uint xGroups = ((uint)mapW + WorkgroupX - 1u) / WorkgroupX;
        uint yGroups = ((uint)mapH + WorkgroupY - 1u) / WorkgroupY;
        _ctx.Dispatch2D(pipeline, set, BuildPush((uint)mapW, (uint)mapH), xGroups, yGroups);

        // ЕДИНСТВЕННЫЙ readback за шаг (Sync + копия) — дальше CPU читает кэш.
        byte[] data = _ctx.ReadbackSync(dst);
        var cache = new float[cellCount];
        Buffer.BlockCopy(data, 0, cache, 0, Math.Min(data.Length, cellCount * 4));
        _cache = cache; // публикация заменой ссылки (volatile)
        _parity = !_parity;
        return true;
    }

    // Blocked-карта: вода = 2 (источник), гора/стена = 1 (блок), иначе 0.
    // Деревья (TreeOnGrass) НЕ блок: влага под кронами держится (как и проход
    // агентов в GpuFlowField — деревья проходимы).
    private static byte[] BuildBlockedBytes(SimulationContext ctx, int mapW, int mapH)
    {
        var flat = new uint[mapW * mapH];
        for (int y = 0; y < mapH; y++)
        {
            for (int x = 0; x < mapW; x++)
            {
                int idx = y * mapW + x;
                TileType t = ctx.Ground[x, y];
                if (t == TileType.Water)
                    flat[idx] = 2u;
                else if (t == TileType.Mountain || ctx.SolidWalls[x, y])
                    flat[idx] = 1u;
                else
                    flat[idx] = 0u;
            }
        }
        byte[] bytes = new byte[flat.Length * 4];
        Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    // Начальная влажность (EnsureMoisture): вода = 1.0, клетка с водой
    // в 4-окрестности = 0.5, иначе = 0.2.
    private static byte[] BuildInitialMoistureBytes(SimulationContext ctx, int mapW, int mapH)
    {
        var moist = new float[mapW * mapH];
        for (int y = 0; y < mapH; y++)
        {
            for (int x = 0; x < mapW; x++)
            {
                int idx = y * mapW + x;
                if (ctx.Ground[x, y] == TileType.Water)
                {
                    moist[idx] = MoistWater;
                    continue;
                }
                bool nearWater =
                    (x > 0 && ctx.Ground[x - 1, y] == TileType.Water) ||
                    (x + 1 < mapW && ctx.Ground[x + 1, y] == TileType.Water) ||
                    (y > 0 && ctx.Ground[x, y - 1] == TileType.Water) ||
                    (y + 1 < mapH && ctx.Ground[x, y + 1] == TileType.Water);
                moist[idx] = nearWater ? MoistNearWater : MoistDry;
            }
        }
        byte[] bytes = new byte[moist.Length * 4];
        Buffer.BlockCopy(moist, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    // Push crop_diffuse: mapW, mapH, evap, diffuse = 16 байт (< 128).
    // Порядок полей обязан совпадать с layout(push_constant) в шейдере.
    private static byte[] BuildPush(uint mapW, uint mapH)
    {
        byte[] bytes = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes(mapW), 0, bytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(mapH), 0, bytes, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(EvapRate), 0, bytes, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(DiffuseRate), 0, bytes, 12, 4);
        return bytes;
    }

    private void LogErrorOnce(string reason)
    {
        lock (_lock)
        {
            if (_errorLogged)
                return;
            _errorLogged = true;
        }
        GD.PrintErr($"[GpuCropField] {reason}");
    }
}
