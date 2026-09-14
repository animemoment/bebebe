using System;
using System.Collections.Generic;
using Godot;
using Game.Core;

namespace Game.Simulation.Gpu;

// Глобальное поле направлений 512x512 на GPU (multi-source BFS-волна).
// НЕ заменяет локальный BFS 33x33 в FlowFieldManager (он дешёвый, решение архитектора):
// потребитель FlowFieldManager.GetDirection сначала пробует TryGetDirection,
// при false идёт старым путём (ранний выход ≤5, затем локальный BFS).
//
// Схема: CPU заливает битмаску целей + карту блоков, GPU считает дистанции
// ping-pong (flowfield_dist.glsl), затем один диспатч векторов (flowfield_vec.glsl).
// Readback Vec делается ОДИН раз после TryCompute и кэшируется в float[] —
// TryGetDirection читает только CPU-кэш, GPU на каждый запрос НЕ дёргается.
//
// Fallback везде: GPU недоступен / поле не свежее / INF → false, вызыватель
// использует старый путь. Исключения GPU → false + GD.PrintErr один раз.
public sealed class GpuFlowField
{
    public static GpuFlowField Instance { get; } = new();

    // Шейдеры рядом с остальными gpu-шейдерами (импорт RDShaderFile подхватит редактор).
    public const string DistShaderPath = "res://src/simulation/gpu/flowfield_dist.glsl";
    public const string VecShaderPath = "res://src/simulation/gpu/flowfield_vec.glsl";

    // Размер workgroup в обоих шейдерах — обязан совпадать с local_size (16,16,1).
    private const uint WorkgroupX = 16;
    private const uint WorkgroupY = 16;

    // Честное ограничение: итераций по умолчанию хватает на радиус ~128 тайлов
    // от целей (волна идёт на 1 клетку за проход по 4-связности). Дальние регионы
    // останутся INF → TryGetDirection вернёт false → CPU-fallback локальным BFS.
    // Полное покрытие 512x512 от одной цели потребовало бы ~512 итераций × диспатч —
    // дорого, поэтому кап сознательный. Multi-source (много целей по карте) на
    // практике покрывает заметно больше: важна дистанция до БЛИЖАЙШЕЙ цели.
    private const int DefaultMaxIters = 128;

    // Троттлинг пересчёта: не чаще 1 раза в 2 сек игрового времени.
    private const float MinRecomputeIntervalSec = 2.0f;

    private const float Inf = 1e30f;

    // Снапшот для lock-free чтения из горячего пути (10k агентов):
    // публикация — заменой ссылки, чтение — без лока.
    private sealed class Snapshot
    {
        public readonly int MapW;
        public readonly int MapH;
        public readonly float[] Vec; // flat 2*W*H: [dx0,dy0,dx1,dy1,...]

        public Snapshot(int mapW, int mapH, float[] vec)
        {
            MapW = mapW;
            MapH = mapH;
            Vec = vec;
        }
    }

    private readonly object _lock = new();
    private readonly GpuComputeContext _ctx = new();
    private volatile Snapshot _snapshot; // null = не свежее
    private uint[] _targetMask; // flat W*H, 1 = цель
    private int _targetW;
    private int _targetH;
    private bool _hasTargets;
    private float _lastComputeGameTime = float.NegativeInfinity;
    private bool _errorLogged;

    private GpuFlowField()
    {
    }

    // true, если есть свежий кэш Vec (можно звать TryGetDirection).
    public bool IsFresh => _snapshot != null;

    // Стены/цели сменились (стена построена/снесена, набор целей другой):
    // поле протухает, следующий TryCompute посчитает заново.
    // Кто зовёт: TODO — точки вызова Invalidate при изменении SolidWalls
    // (постройка/снос стены) и при смене набора целей пока НЕ подключены;
    // диспетчер/строительство должны позвать Invalidate() (стены) или
    // SetTargets() (цели — он сам инвалидирует). Без вызова поле просто
    // останется свежим и будет отдавать направления по старой карте стен —
    // безопасно (fallback BFS всё равно объезжает), но не оптимально.
    public void Invalidate()
    {
        _snapshot = null;
    }

    // Строит битмаску целей на CPU и заливает во внутреннее хранилище.
    // Пустой набор (или все клетки вне карты) → IsFresh = false.
    // Сама смена целей инвалидирует поле (пересчёт — в следующем TryCompute).
    public void SetTargets(IEnumerable<(int X, int Y)> cells, int mapW, int mapH)
    {
        if (cells == null)
            throw new ArgumentNullException(nameof(cells));
        if (mapW <= 0)
            throw new ArgumentOutOfRangeException(nameof(mapW), "Ширина карты должна быть > 0.");
        if (mapH <= 0)
            throw new ArgumentOutOfRangeException(nameof(mapH), "Высота карты должна быть > 0.");

        var mask = new uint[mapW * mapH];
        int valid = 0;
        foreach (var (x, y) in cells)
        {
            if ((uint)x < (uint)mapW && (uint)y < (uint)mapH)
            {
                int idx = y * mapW + x;
                if (mask[idx] == 0u)
                {
                    mask[idx] = 1u;
                    valid++;
                }
            }
            // OOB-цели молча пропускаем: диспетчер может отдать точку за краем —
            // это не повод ронять сим, просто такая цель не участвует в волне.
        }

        lock (_lock)
        {
            _targetMask = mask;
            _targetW = mapW;
            _targetH = mapH;
            _hasTargets = valid > 0;
            _snapshot = null; // набор сменился — поле протухло
        }
    }

    // Совместимая перегрузка без игрового времени: троттлинг идёт по wall-clock
    // (Environment.TickCount64) — при паузе игры тики всё равно идут, это
    // честно зафиксировано; для точного игрового троттлинга звать перегрузку
    // с gameTimeSec из игрового цикла.
    public bool TryCompute(SimulationContext ctx, int maxIters = DefaultMaxIters)
    {
        float wallSec = (float)(System.Environment.TickCount64 / 1000.0);
        return TryCompute(ctx, wallSec, maxIters, false);
    }

    // Полный цикл: залить Blocked + DistA-init, maxIters ping-pong проходов,
    // один Vec-диспатч, один readback Vec в CPU-кэш. true + IsFresh = true при успехе.
    public bool TryCompute(SimulationContext ctx, float gameTimeSec, int maxIters = DefaultMaxIters, bool force = false)
    {
        if (ctx == null)
            return false;
        if (_snapshot != null)
            return true; // уже свежее — работы нет
        if (maxIters <= 0)
            return false;

        uint[] mask;
        int tw, th;
        float lastCompute;
        lock (_lock)
        {
            if (_snapshot != null)
                return true;
            if (!_hasTargets || _targetMask == null)
                return false; // целей нет — считать нечего, вызыватель идёт в BFS
            if (_targetW != ctx.MapWidth || _targetH != ctx.MapHeight)
                return false; // размер целей не совпадает с картой — не считаем
            if (!force && (gameTimeSec - _lastComputeGameTime) < MinRecomputeIntervalSec)
                return false; // троттлинг: пересчёт не чаще 1 раза в 2 сек
            mask = _targetMask;
            tw = _targetW;
            th = _targetH;
            lastCompute = _lastComputeGameTime;
        }
        _ = lastCompute;

        if (!_ctx.EnsureInitialized() || !_ctx.IsAvailable)
            return false; // GPU нет (или Compatibility-рендер) — тихий CPU fallback

        try
        {
            return DispatchFullCycle(ctx, mask!, tw, th, gameTimeSec, maxIters);
        }
        catch (Exception ex)
        {
            // Любой сбой GPU — прозрачно в fallback, сим не роняем. Лог один раз,
            // чтобы не спамить каждый тик (TryCompute зовут часто).
            LogErrorOnce($"GPU-путь упал ({ex.GetType().Name}: {ex.Message}). CPU fallback.");
            return false;
        }
    }

    // Чтение из CPU-кэша Vec (GPU на каждый запрос НЕ дёргается).
    // false = нет поля / вне карты / (0,0) / INF-регион → вызыватель идёт в локальный BFS.
    public bool TryGetDirection(int startX, int startY, out (float dx, float dy) dir)
    {
        dir = (0f, 0f);
        Snapshot snap = _snapshot; // атомарное чтение ссылки, без лока
        if (snap == null)
            return false;
        if ((uint)startX >= (uint)snap.MapW || (uint)startY >= (uint)snap.MapH)
            return false;
        int idx = startY * snap.MapW + startX;
        float dx = snap.Vec[idx * 2];
        float dy = snap.Vec[idx * 2 + 1];
        if (dx == 0f && dy == 0f)
            return false;
        dir = (dx, dy);
        return true;
    }

    // Весь GPU-цикл под одним локом контекста не нужен: GpuComputeContext
    // потокобезопасен сам (внутренний _lock). Здесь защищаем только публикацию.
    private bool DispatchFullCycle(
        SimulationContext ctx, uint[] mask, int mapW, int mapH,
        float gameTimeSec, int maxIters)
    {
        int cellCount = mapW * mapH;
        uint distBytes = (uint)(cellCount * 4);

        // Blocked: Grass && !SolidWalls ? 0 : 1. Гора и вода — тоже блок
        // (см. AgentMovementService.IsTileBlocked: Mountain == стена).
        // Деревья (TreeOnGrass) НЕ блок: агенты ходят под ними.
        var blocked = new uint[cellCount];
        for (int y = 0; y < mapH; y++)
        {
            for (int x = 0; x < mapW; x++)
            {
                int idx = y * mapW + x;
                bool passable = ctx.Ground[x, y] == TileType.Grass && !ctx.SolidWalls[x, y];
                blocked[idx] = passable ? 0u : 1u;
            }
        }
        byte[] blockedBytes = new byte[distBytes];
        Buffer.BlockCopy(blocked, 0, blockedBytes, 0, blockedBytes.Length);

        // DistA-init: цель ? 0 : INF. Цель внутри стены — INF (стоять в стене нельзя).
        var init = new float[cellCount];
        for (int i = 0; i < cellCount; i++)
            init[i] = (mask[i] != 0u && blocked[i] == 0u) ? 0f : Inf;
        byte[] initBytes = new byte[distBytes];
        Buffer.BlockCopy(init, 0, initBytes, 0, initBytes.Length);

        byte[] maskBytes = new byte[cellCount * 4];
        Buffer.BlockCopy(mask, 0, maskBytes, 0, maskBytes.Length);

        Rid distPipe = _ctx.GetOrCreatePipeline(DistShaderPath, out Rid distShader);
        if (!distPipe.IsValid || !distShader.IsValid)
            return false; // шейдер битый — тихий fallback (ошибка уже залогирована в контексте)
        Rid vecPipe = _ctx.GetOrCreatePipeline(VecShaderPath, out Rid vecShader);
        if (!vecPipe.IsValid || !vecShader.IsValid)
            return false;

        Rid blockedBuf = _ctx.GetOrCreateBuffer("ff_blocked", distBytes);
        Rid distABuf = _ctx.GetOrCreateBuffer("ff_distA", distBytes);
        Rid distBBuf = _ctx.GetOrCreateBuffer("ff_distB", distBytes);
        Rid targetBuf = _ctx.GetOrCreateBuffer("ff_target", distBytes);
        Rid vecBuf = _ctx.GetOrCreateBuffer("ff_vec", (uint)(cellCount * 8));

        _ctx.BufferUpdate(blockedBuf, blockedBytes);
        _ctx.BufferUpdate(targetBuf, maskBytes);
        _ctx.BufferUpdate(distABuf, initBytes);
        // DistB инициализировать не нужно: каждый проход перезаписывает ВСЕ
        // клетки (включая blocked → INF и OOB-гарды), мусора не остаётся.

        // UniformSet'ы: трекаются в контексте до ReleaseAll (тот же паттерн,
        // что GpuNoiseGenerator — по одному сету на шейдер за пересчёт).
        Rid distSet = _ctx.CreateUniformSet(distShader,
            (0u, blockedBuf), (1u, distABuf), (2u, distBBuf), (3u, targetBuf));

        uint xGroups = ((uint)mapW + WorkgroupX - 1u) / WorkgroupX;
        uint yGroups = ((uint)mapH + WorkgroupY - 1u) / WorkgroupY;

        // Ping-pong: passDir 0 = A→B, 1 = B→A. Барьеры между диспатчами НЕ нужны:
        // Dispatch2D делает Submit на каждый вызов, порядок в очереди гарантирован.
        for (int p = 0; p < maxIters; p++)
        {
            byte[] push = BuildDistPush((uint)mapW, (uint)mapH, (uint)(p & 1));
            _ctx.Dispatch2D(distPipe, distSet, push, xGroups, yGroups);
        }

        // Финальный буфер — после чётного числа проходов это A, после нечётного B.
        Rid finalDistBuf = ((maxIters & 1) == 0) ? distABuf : distBBuf;
        Rid vecSet = _ctx.CreateUniformSet(vecShader,
            (0u, blockedBuf), (1u, finalDistBuf), (2u, vecBuf));
        _ctx.Dispatch2D(vecPipe, vecSet, BuildVecPush((uint)mapW, (uint)mapH), xGroups, yGroups);

        // ЕДИНСТВЕННЫЙ readback за весь пересчёт (Sync + копия) — дальше читаем кэш.
        byte[] data = _ctx.ReadbackSync(vecBuf);
        var vec = new float[cellCount * 2];
        Buffer.BlockCopy(data, 0, vec, 0, Math.Min(data.Length, vec.Length * 4));

        lock (_lock)
        {
            _snapshot = new Snapshot(mapW, mapH, vec);
            _lastComputeGameTime = gameTimeSec;
        }
        return true;
    }

    // Push flowfield_dist: mapW, mapH, passDir, unused(float 0) = 16 байт (< 128).
    // Порядок полей обязан совпадать с layout(push_constant) в шейдере.
    private static byte[] BuildDistPush(uint mapW, uint mapH, uint passDir)
    {
        byte[] bytes = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes(mapW), 0, bytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(mapH), 0, bytes, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(passDir), 0, bytes, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(0f), 0, bytes, 12, 4);
        return bytes;
    }

    // Push flowfield_vec: mapW, mapH = 8 байт (< 128).
    private static byte[] BuildVecPush(uint mapW, uint mapH)
    {
        byte[] bytes = new byte[8];
        Buffer.BlockCopy(BitConverter.GetBytes(mapW), 0, bytes, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(mapH), 0, bytes, 4, 4);
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
        GD.PrintErr($"[GpuFlowField] {reason}");
    }
}
