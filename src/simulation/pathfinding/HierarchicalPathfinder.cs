using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Иерархический поиск пути (упрощённый HPA*): карта делится на регионы
/// (по умолчанию 16x16 тайлов), путь строится сначала ГРУБО через граф
/// регионов (A* по регионам), а затем ДЕТАЛИЗИРУЕТСЯ полноценным A* по
/// клеткам в узком «окне» из 1-3 соседних регионов — от входа условной
/// зоны к её выходу для входа в следующую зону.
///
/// Особенности:
///  - Граничные «порталы» — непрерывные проходимые отрезки общей границы
///    двух регионов; якорь портала — центральная клетка отрезка.
///  - A* на детальном уровне работает по локальному окну (клетки лишь
///    нескольких регионов), что даёт копеечную стоимость даже на 512x512.
///  - Кэш: грубые пути (регион->регион) и детальные сегменты
///    (клетка->клетка) кэшируются; инвалидируется при постройке стен.
///  - Потокобезопасен: построение оверлея под lock, scratch-буферы A*
///    ThreadLocal, кэш-словари под отдельным lock'ом.
///  - Не зависит от Godot API (чистый C#).
/// </summary>
public sealed class HierarchicalPathfinder
{
    public static HierarchicalPathfinder Instance { get; } = new();

    /// <summary>Размер региона в тайлах (степень двойки: 4 -> 16x16 тайлов).</summary>
    private const int RegionShift = 4;
    private const int RegionSize = 1 << RegionShift;

    // Стоимость прохождения клетки: 0 — блокирована (стена), 1 — трава.
    // #13: CostWater=6 — это ФЛАГ типа клетки («вода»), а НЕ множитель пути.
    // Реальный штраф воды — ×3 к шагу (step *= 3 ниже: 10→30 / 14→42).
    // _cost[] как вес напрямую использовать нельзя (дало бы ×6 вместо ×3).
    private const byte CostBlocked = 0;
    private const byte CostGrass = 1;
    private const byte CostWater = 6;
    // Сколько водных тайлов подряд LOS прощает напрямик (мелкая лужа/брод).
    // Длиннее — строим иерархический путь с водным штрафом (обход).
    private const int MaxDirectWaterTiles = 3;

    // Лимиты кэша (простая политика: при переполнении — полная очистка).
    private const int MaxRegionPathCache = 32768;
    private const int MaxSegmentCache = 65536;

    // Детальное окно: до 3x3 регионов = (3*16)^2 = 2304 клетки.
    private const int LocalCapacity = (3 * RegionSize) * (3 * RegionSize);

    // FIX #3: оверлей свёрнут в неизменяемый снапшот. Публикация — одна
    // volatile-запись ссылки по завершении построения; читатели берут ссылку
    // ОДИН раз и работают с согласованным набором данных. Это снимает класс
    // гонок «новый _cost + старые _portals/_mapWidth» на weak-memory (ARM)
    // и ABA при смене карты (старый массив больше не смешивается со свежими
    // размерами). Снапшот никогда не мутируется после публикации.
    private sealed class OverlaySnapshot
    {
        public readonly byte[] Cost;       // [W*H]
        public readonly int W, H;
        public readonly int DimX, DimY;    // размеры региональной сетки
        public readonly int[] EdgeStart;   // [regions*4] стартовый индекс в Portals
        public readonly int[] EdgeCount;   // [regions*4] число порталов у (регион, направление)
        public readonly int[] Portals;     // плоский массив якорей порталов (packed cell)

        public OverlaySnapshot(byte[] cost, int w, int h, int dimX, int dimY,
            int[] edgeStart, int[] edgeCount, int[] portals)
        {
            Cost = cost; W = w; H = h; DimX = dimX; DimY = dimY;
            EdgeStart = edgeStart; EdgeCount = edgeCount; Portals = portals;
        }
    }

    private volatile OverlaySnapshot _overlay;
    private int _buildVersion;

    private SimulationContext _ctx;

    private readonly object _buildLock = new();
    // P-баланс: кэши — ConcurrentDictionary вместо Dictionary+_cacheLock.
    // _cacheLock сериализовал все 16 потоков Parallel-фаз на КАЖДОМ запросе пути:
    // один «тяжёлый» ComputeLocalSegment держал lock, остальные 15 ждали —
    // классический straggler при низкой средней загрузке CPU.
    private readonly ConcurrentDictionary<int, int[]> _regionPathCache = new(4, 1024);
    private readonly ConcurrentDictionary<ulong, int[]> _segmentCache = new(4, 4096);

    private sealed class SearchBuffers
    {
        // Детальный A* (локальное окно).
        public readonly int[] G = new int[LocalCapacity + 1];
        public readonly int[] Parent = new int[LocalCapacity + 1];
        public readonly int[] Open = new int[LocalCapacity + 1];
        public readonly bool[] Closed = new bool[LocalCapacity + 1];
        public readonly int[] Value = new int[LocalCapacity + 1];

        // Грубый A* (граф регионов). Динамические: на карте >64M тайлов
        // (регионов > 4096) фиксированные буферы молча отказывали в пути
        // (FIX #5). Grow-до-RegionCap поднимает ёмкость под фактическое
        // число регионов карты (с запасом), ReadIndex гарантирует
        // корректную публикацию grow'а другим потокам этого же A*.
        public readonly IndexableBuffer<int> RG = new(4096);
        public readonly IndexableBuffer<int> RParent = new(4096);
        public readonly IndexableBuffer<int> ROpen = new(4096);
        public readonly IndexableBuffer<bool> RClosed = new(4096);
        public readonly IndexableBuffer<int> RValue = new(4096);

        // PERF F4: scratch для TryFindPath/SmoothPath. Переиспользуемые буферы
        // вместо new int[]/List<int>/ToArray в горячем пути. Сборка пути и
        // сглаживание ≤ 4096 точек; якоря — динамически до числа переходов
        // регионов (FIX #5: потолок 256 пар убран, змеистый путь на 512×512
        // раньше молча не строился).
        public readonly IndexableBuffer<int> Anchor = new(512);
        public readonly int[] PathScratch = new int[4096];
        public readonly int[] SmoothScratch = new int[4096];
        public readonly int[] RevScratch = new int[4096];
    }

    /// <summary>
    /// Растущий индексный буфер для ThreadLocal scratch A*. При EnsureCapacity
    /// старый массив копируется в новый (данные сохранены), а Volatile.Write
    /// корректно публикует ссылку. Все индексы в пределах Capacity валидны.
    /// </summary>
    private sealed class IndexableBuffer<T>
    {
        private T[] _data;

        public IndexableBuffer(int capacity) => _data = new T[capacity];

        public T[] Data => _data;

        /// <summary>Текущая ёмкость (для проверок лимитов).</summary>
        public int Capacity => Volatile.Read(ref _data).Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T this[int index] => ref Volatile.Read(ref _data)[index];

        public void EnsureCapacity(int required)
        {
            var cur = Volatile.Read(ref _data);
            if (cur.Length >= required)
                return;
            int cap = cur.Length;
            while (cap < required)
                cap *= 2;
            var fresh = new T[cap];
            Array.Copy(cur, fresh, cur.Length);
            Volatile.Write(ref _data, fresh);
        }
    }

    private readonly ThreadLocal<SearchBuffers> _buffers = new(() => new SearchBuffers());

    private enum Dir : byte
    {
        North = 0,
        East = 1,
        South = 2,
        West = 3,
        None = 4
    }

    // ------------------------------------------------------------------
    // Публичный API
    // ------------------------------------------------------------------

    /// <summary>
    /// Устанавливает контекст симуляции (источник данных карты). Вызывается
    /// один раз при старте симуляции. Сброс оверлея — через Invalidate().
    /// </summary>
    public void Initialize(SimulationContext ctx)
    {
        lock (_buildLock)
        {
            _ctx = ctx;
            _overlay = null;
            _buildVersion++;
            // FIX #2: кэши ОБЯЗАТЕЛЬНО чистятся. Ключи сегментов — упакованные
            // координаты под СТАРЫЕ размеры карты: при перезапуске на карте
            // другого размера устаревшие сегменты дали бы маршруты «в никуда»
            // или IndexOutOfRange. Регион-пути привязаны к старой региональной
            // сетке — тоже протухают.
            _regionPathCache.Clear();
            _segmentCache.Clear();
        }
    }

    /// <summary>
    /// Сбрасывает кэши и помечает оверлей на перестройку (при постройке стен).
    /// Дебаунс по wall-clock: 100 завершений стройки в один тик раньше давали
    /// 100 последовательных Clear (каждый — снос 65k entries под _buildLock).
    /// Версия в ключе кэша не нужна: Clear под гейтом достаточно редок.
    /// </summary>
    private long _lastInvalidateMs;

    public void Invalidate()
    {
        long now = System.Environment.TickCount64;
        if (now - Volatile.Read(ref _lastInvalidateMs) < 200)
            return;
        lock (_buildLock)
        {
            now = System.Environment.TickCount64;
            if (now - _lastInvalidateMs < 200)
                return;
            _lastInvalidateMs = now;
            _buildVersion++;
            _overlay = null;
            _regionPathCache.Clear();
            _segmentCache.Clear();
        }
    }

    /// <summary>
    /// Строит иерархический путь от тайла (sx, sy) к (tx, ty).
    /// При успехе возвращает true; если прямая видимость есть — true с
    /// count == 0 (двигаться напрямую). Точки (без стартовой клетки)
    /// записываются в <paramref name="outPath"/> как int-коды y*mapWidth+x,
    /// количество — в <paramref name="count"/>.
    /// </summary>
    public bool TryFindPath(int sx, int sy, int tx, int ty, out int[] outPath, out int count)
    {
        outPath = null;
        count = 0;

        if (sx == tx && sy == ty)
            return true;

        // FIX #3: ссылка на оверлей читается ОДИН раз — весь проход работает
        // с согласованным неизменяемым снапшотом, даже если параллельный
        // Invalidate()/rebuild подменит _overlay.
        OverlaySnapshot ov = EnsureOverlay();
        if (ov == null)
            return false; // симуляция не инициализирована / карта недоступна

        // Прямая видимость — дешёвый путь без построения.
        // НО: вода в LOS раньше игнорировалась — агент шёл напрямик через
        // озеро/реку (только ×0.45 к скорости) вместо обхода. Если водный
        // отрезок длиннее порога — идём в иерархический A* (у него вода
        // стоит ×3 и маршрут обогнёт водоём, где это выгодно).
        // #14: один проход Брезенхема вместо двух (HasLineOfSight +
        // WaterCrossingLength дублировали обход) — LineOfSightWithWater
        // возвращает и флаг стен, и длину водного отрезка сразу.
        if (LineOfSightWithWater(ov, sx, sy, tx, ty, out int directWater)
            && directWater <= MaxDirectWaterTiles)
            return true;

        int sr = RegionOfCell(ov, sx, sy);
        int tr = RegionOfCell(ov, tx, ty);

        // FIX #1: старт и цель в одном регионе. Грубый A* возвращает тривиальный
        // путь длины 1 ([sr]) — старый код тут же возвращал false, хотя локальный
        // A* в окне региона легко находит обход стены/изгиба. Симптом был:
        // агент «не может» дойти до соседней клетки через препятствие.
        // Строим сегмент напрямую, минуя регион-граф и выбор порталов.
        if (sr == tr)
        {
            int[] onlySeg = GetSegmentCached(ov, sx, sy, tx, ty);
            if (onlySeg == null || onlySeg.Length < 2)
                return false;

            // Сегмент включает стартовую клетку — в результат она не входит.
            int onlyLen = onlySeg.Length - 1;
            if (onlyLen <= 0)
                return true;
            var onlyResult = new int[onlyLen];
            Array.Copy(onlySeg, 1, onlyResult, 0, onlyLen);
            outPath = onlyResult;
            count = onlyLen;
            return true;
        }

        int[] regionPath = GetRegionPathCached(ov, sr, tr);
        if (regionPath == null || regionPath.Length < 2)
            return false;

        int regionCount = regionPath.Length;

        // PERF F4: якоря и сборка пути — в thread-local scratch (без new int[]/List
        // на запрос: ~500 запросов × 6 аллокаций за кадр на 100x давили Gen0 и
        // останавливали все потоки, включая рендер). Кэш regionPath — shared
        // (ConcurrentDictionary), но scratch — per-thread, гонки нет.
        var scratch = _buffers.Value;
        // anchorPairs хранится парами (x,y) в одном массиве: [x0..xn, y0..yn].
        int anchorPairs = regionCount - 1;
        // @destroyer: anchorPairs<=0 невозможен (regionPath.Length>=2 проверен
        // выше), но оставляем guard — дешевле, чем доказывать инвариант.
        // Переполнение int при anchorPairs*2: regionCount ограничен числом
        // регионов карты, переполнения нет.
        if (anchorPairs <= 0)
            return false; // регион-путь патологичен — retry на следующем кадре
        // FIX #5: потолок 256 пар убран — буфер растёт до фактической длины
        // регион-пути (змеистый путь на больших картах больше не молча отваливается).
        scratch.Anchor.EnsureCapacity(anchorPairs * 2);
        var anchorData = scratch.Anchor.Data;
        Span<int> anchors = anchorData.AsSpan(0, anchorPairs * 2);
        Span<int> anchorX = anchors.Slice(0, anchorPairs);
        Span<int> anchorY = anchors.Slice(anchorPairs, anchorPairs);

        for (int t = 0; t < regionCount - 1; t++)
        {
            if (!PickBestPortal(ov, regionPath[t], regionPath[t + 1], sx, sy, tx, ty,
                out anchorX[t], out anchorY[t]))
            {
                return false; // порталы между соседями исчезли — пересчёт на след. кадре
            }
        }

        Span<int> path = scratch.PathScratch;
        int pathLen = 0;

        for (int i = 0; i < regionCount; i++)
        {
            int fromX = i == 0 ? sx : anchorX[i - 1];
            int fromY = i == 0 ? sy : anchorY[i - 1];
            int toX = i == regionCount - 1 ? tx : anchorX[i];
            int toY = i == regionCount - 1 ? ty : anchorY[i];

            if (fromX == toX && fromY == toY)
                continue;

            int[] seg = GetSegmentCached(ov, fromX, fromY, toX, toY);
            if (seg == null || seg.Length < 2)
                return false;

            // Копируем сегмент, исключая его первый элемент (либо это старт,
            // либо якорь, уже добавленный предыдущим сегментом).
            if (pathLen + seg.Length - 1 > path.Length)
                return false; // путь длиннее scratch — retry (кэш сегментов цел)
            for (int k = 1; k < seg.Length; k++)
            {
                path[pathLen++] = seg[k];
            }
        }

        if (pathLen == 0)
            return true;

        int smoothed = SmoothPath(ov, sx, sy, tx, ty, path.Slice(0, pathLen), scratch.SmoothScratch);
        if (smoothed <= 0)
            return false;

        // Единственная аллокация на УСПЕШНЫЙ немгновенный путь: точный sized-массив
        // результата. Scratch не отдаём наружу — его перезапишет следующий
        // запрос этого потока. Сегменты GetSegmentCached уже закэшированы
        // (int[] в ConcurrentDictionary), их аллокации амортизированы.
        var result = new int[smoothed];
        scratch.SmoothScratch.AsSpan(0, smoothed).CopyTo(result);

        outPath = result;
        count = result.Length;
        return true;
    }

    // ------------------------------------------------------------------
    // Построение оверлея (стоимости + порталы регионов)
    // ------------------------------------------------------------------

    /// <summary>
    /// Возвращает актуальный оверлей, при необходимости строя его под lock.
    /// FIX #3: результат — неизменяемый снапшот; построение идёт в локальные
    /// массивы, наружу публикуется ОДНА volatile-ссылка по завершении.
    /// Читатели вне лока видят либо целиком старый, либо целиком новый оверлей.
    /// </summary>
    private OverlaySnapshot EnsureOverlay()
    {
        // Быстрый путь без лока: свежий снапшот уже опубликован.
        OverlaySnapshot ov = _overlay;
        if (ov != null)
            return ov;

        lock (_buildLock)
        {
            if (_overlay != null)
                return _overlay;

            if (_ctx == null)
            {
                // Симуляция ещё не стартовала — оверлей недоступен.
                return null;
            }

            int w = _ctx.MapWidth;
            int h = _ctx.MapHeight;
            byte[] cost = new byte[w * h];

            for (int y = 0; y < h; y++)
            {
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte c;
                    // Камень проходим (как трава): россыпь не блокирует путь.
                    if (_ctx.SolidWalls[x, y] || _ctx.Ground[x, y] == TileType.Mountain)
                        c = CostBlocked;
                    else if (_ctx.Ground[x, y] == TileType.Water)
                        c = CostWater;
                    else
                        c = CostGrass;
                    cost[rowBase + x] = c;
                }
            }

            int dimX = (w + RegionSize - 1) >> RegionShift;
            int dimY = (h + RegionSize - 1) >> RegionShift;

            // ---- Портал: проходимый отрезок общей границы двух регионов.
            // Каждую общую границу регистрируем от «юго-восточной» стороны,
            // чтобы обратный переход пользовался тем же набором якорей.
            int regionCount = dimX * dimY;
            int[] edgeStart = new int[regionCount * 4];
            int[] edgeCount = new int[regionCount * 4];
            Array.Fill(edgeStart, -1);
            Array.Clear(edgeCount, 0, edgeCount.Length);
            int[] portals = new int[regionCount * 4 * 16];
            int portalCount = 0;

            for (int ry = 0; ry < dimY; ry++)
            {
                for (int rx = 0; rx < dimX; rx++)
                {
                    int rid = ry * dimX + rx;

                    // Восточная граница (сосед справа).
                    if (rx + 1 < dimX)
                    {
                        edgeStart[rid * 4 + (byte)Dir.East] = portalCount;
                        edgeCount[rid * 4 + (byte)Dir.East] =
                            BuildEdgePortals(portals, ref portalCount, rx, ry, Dir.East, cost, w, h, dimX);
                    }

                    // Южная граница.
                    if (ry + 1 < dimY)
                    {
                        edgeStart[rid * 4 + (byte)Dir.South] = portalCount;
                        edgeCount[rid * 4 + (byte)Dir.South] =
                            BuildEdgePortals(portals, ref portalCount, rx, ry, Dir.South, cost, w, h, dimX);
                    }

                    // Северная/западная — те же якоря, что у верхнего/левого
                    // соседа (их ребра South/East).
                    if (ry > 0)
                    {
                        int northNeighborBase = (ry - 1) * dimX * 4 + rx * 4 + (byte)Dir.South;
                        edgeStart[rid * 4 + (byte)Dir.North] = edgeStart[northNeighborBase];
                        edgeCount[rid * 4 + (byte)Dir.North] = edgeCount[northNeighborBase];
                    }
                    if (rx > 0)
                    {
                        edgeStart[rid * 4 + (byte)Dir.West] = edgeStart[rid * 4 - 4 + (byte)Dir.East];
                        edgeCount[rid * 4 + (byte)Dir.West] = edgeCount[rid * 4 - 4 + (byte)Dir.East];
                    }
                }
            }

            // Единственная публикация: до этой точки снапшот невидим читателям,
            // после — все поля гарантированно согласованы (volatile write + using
            // semantics на readonly-полях конструктора).
            var snapshot = new OverlaySnapshot(cost, w, h, dimX, dimY, edgeStart, edgeCount, portals);
            _overlay = snapshot;
            return snapshot;
        }
    }

    /// <summary>
    /// Находит непрерывные проходимые отрезки общей границы региона (rx, ry)
    /// с соседом в направлении <paramref name="dir"/> (East или South) и
    /// записывает якоря (центральную клетку отрезка, packed) в portals.
    /// Возвращает число порталов.
    /// </summary>
    private static int BuildEdgePortals(int[] portals, ref int portalCount,
        int rx, int ry, Dir dir, byte[] cost, int w, int h, int dimX)
    {
        int x0 = rx << RegionShift;
        int y0 = ry << RegionShift;
        int x1 = Math.Min(x0 + RegionSize - 1, w - 1);
        int y1 = Math.Min(y0 + RegionSize - 1, h - 1);

        int count = 0;

        if (dir == Dir.East)
        {
            int nx = Math.Min(x1 + 1, w - 1);
            int runStartY = -1;
            for (int y = y0; y <= y1; y++)
            {
                bool a = cost[y * w + x1] != CostBlocked;
                bool b = cost[y * w + nx] != CostBlocked;
                if (a && b)
                {
                    if (runStartY < 0) runStartY = y;
                }
                else if (runStartY >= 0)
                {
                    portals[portalCount++] = ((runStartY + y - 1) >> 1) * w + x1;
                    count++;
                    runStartY = -1;
                }
            }
            if (runStartY >= 0)
            {
                portals[portalCount++] = ((runStartY + y1) >> 1) * w + x1;
                count++;
            }
        }
        else // Dir.South
        {
            int ny = Math.Min(y1 + 1, h - 1);
            int runStartX = -1;
            for (int x = x0; x <= x1; x++)
            {
                bool a = cost[y1 * w + x] != CostBlocked;
                bool b = cost[ny * w + x] != CostBlocked;
                if (a && b)
                {
                    if (runStartX < 0) runStartX = x;
                }
                else if (runStartX >= 0)
                {
                    portals[portalCount++] = y1 * w + ((runStartX + x - 1) >> 1);
                    count++;
                    runStartX = -1;
                }
            }
            if (runStartX >= 0)
            {
                portals[portalCount++] = y1 * w + ((runStartX + x1) >> 1);
                count++;
            }
        }

        return count;
    }

    // ------------------------------------------------------------------
    // Хелперы координат и порталов
    // ------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RegionOfCell(OverlaySnapshot ov, int tx, int ty)
    {
        return (ty >> RegionShift) * ov.DimX + (tx >> RegionShift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RegionCoordX(OverlaySnapshot ov, int regionId) => regionId % ov.DimX;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RegionCoordY(OverlaySnapshot ov, int regionId) => regionId / ov.DimX;

    /// <summary>Направление от региона ra к соседнему региону rb (или None).</summary>
    private static Dir DirectionBetween(OverlaySnapshot ov, int ra, int rb)
    {
        int ax = RegionCoordX(ov, ra), ay = RegionCoordY(ov, ra);
        int bx = RegionCoordX(ov, rb), by = RegionCoordY(ov, rb);
        if (by == ay)
        {
            if (bx == ax + 1) return Dir.East;
            if (bx == ax - 1) return Dir.West;
        }
        if (bx == ax)
        {
            if (by == ay + 1) return Dir.South;
            if (by == ay - 1) return Dir.North;
        }
        return Dir.None;
    }

    /// <summary>
    /// Выбирает лучший портал на общей границе регионов ra и rb:
    /// якорь, ближайший к прямой (sx,sy)-(tx,ty). Якорь возвращается в
    /// координатах клетки региона ra.
    /// </summary>
    private static bool PickBestPortal(OverlaySnapshot ov, int ra, int rb, int sx, int sy, int tx, int ty,
        out int anchorX, out int anchorY)
    {
        anchorX = -1;
        anchorY = -1;

        Dir dir = DirectionBetween(ov, ra, rb);
        if (dir == Dir.None)
            return false;

        int idx = ra * 4 + (byte)dir;
        int start = ov.EdgeStart[idx];
        int cnt = ov.EdgeCount[idx];
        if (start < 0 || cnt <= 0)
            return false;

        int best = -1;
        int bestScore = int.MaxValue;
        int bestPacked = 0;

        for (int i = 0; i < cnt; i++)
        {
            int portalPacked = ov.Portals[start + i];
            int px = portalPacked % ov.W;
            int py = portalPacked / ov.W;

            // Расстояние от якоря до прямой S-T (упрощённо: до отрезка через
            // проекцию). Достаточно точности для выбора портала.
            int score = DistSqToSegment(px, py, sx, sy, tx, ty);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
                bestPacked = portalPacked;
            }
        }

        if (best < 0)
            return false;

        anchorX = bestPacked % ov.W;
        anchorY = bestPacked / ov.W;
        return true;
    }

    private static int DistSqToSegment(int px, int py, int ax, int ay, int bx, int by)
    {
        int vx = bx - ax, vy = by - ay;
        int wx = px - ax, wy = py - ay;
        int lenSq = vx * vx + vy * vy;
        if (lenSq == 0)
        {
            int offX = px - ax, offY = py - ay;
            return offX * offX + offY * offY;
        }
        float t = (float)(wx * vx + wy * vy) / (float)lenSq;
        t = Math.Max(0f, Math.Min(1f, t));
        float cx = ax + t * vx;
        float cy = ay + t * vy;
        float projDx = px - cx, projDy = py - cy;
        return (int)(projDx * projDx + projDy * projDy);
    }

    // ------------------------------------------------------------------
    // Грубый уровень: A* по графу регионов
    // ------------------------------------------------------------------

    private int[] GetRegionPathCached(OverlaySnapshot ov, int sr, int tr)
    {
        // P0-1: без счётчиков кэша — вызываются из тысяч A* в параллельных фазах.
        // FIX #4: ключ упакован в ulong ((sr << 32) | tr). Старый int-ключ
        // sr * 4096 + tr давал коллизии на картах >64M тайлов (регионов >4096):
        // чужой закэшированный путь возвращался для другой пары регионов.
        // Долгие региональные пути (>256 регионов) раньше молча отваливались
        // по потолку Anchor — снято в TryFindPath (FIX #5).
        ulong key = ((ulong)(uint)sr << 32) | (uint)tr;
        if (_regionPathCache.TryGetValue(key, out int[] cached))
        {
            return cached;
        }

        int[] path = ComputeRegionPath(ov, sr, tr);
        if (path == null)
            return null;

        // Переполнение: удаляем четверть старых записей вместо Clear всего кэша
        // (Clear под нагрузкой = thundering herd: все потоки одновременно идут
        // в ComputeRegionPath/ComputeLocalSegment).
        if (_regionPathCache.Count >= MaxRegionPathCache)
        {
            int toRemove = MaxRegionPathCache / 4;
            foreach (var k in _regionPathCache.Keys)
            {
                if (toRemove-- <= 0) break;
                _regionPathCache.TryRemove(k, out _);
            }
        }
        _regionPathCache[key] = path;
        return path;
    }

    /// <summary>
    /// A* по графу регионов: узлы — регионы, ребро существует, если между
    /// регионами есть хотя бы один портал. Гедонистика — клеточное расстояние
    /// между центрами регионов.
    /// </summary>
    private int[] ComputeRegionPath(OverlaySnapshot ov, int sr, int tr)
    {
        var b = _buffers.Value;
        int cap = ov.DimX * ov.DimY;
        // FIX #5: динамические буферы грубого уровня — ёмкость растёт до
        // фактического числа регионов карты (было жёсткое 4096 регионов =
        // карта 64M тайлов, выше — тихий отказ маршрутизации).
        b.RG.EnsureCapacity(cap);
        b.RParent.EnsureCapacity(cap);
        b.RClosed.EnsureCapacity(cap);
        b.RValue.EnsureCapacity(cap);
        b.ROpen.EnsureCapacity(cap);
        var g = b.RG.Data;
        var parent = b.RParent.Data;
        var closed = b.RClosed.Data;
        int[] open = b.ROpen.Data;

        Array.Fill(g, 0, 0, cap);
        Array.Fill(g, int.MaxValue, 0, cap);
        Array.Fill(parent, -1, 0, cap);
        Array.Fill(closed, false, 0, cap);

        int openCount = 0;
        open[0] = sr;
        openCount = 1;
        g[sr] = 0;

        int trx = RegionCoordX(ov, tr);
        int tryRegionY = RegionCoordY(ov, tr);

        const int MaxExpand = 4096;
        int expanded = 0;

        while (openCount > 0 && expanded < MaxExpand)
        {
            int cur = RegionPop(b, open, ref openCount);
            if (cur == tr)
                break;
            if (closed[cur])
                continue;
            closed[cur] = true;
            expanded++;

            int cx = RegionCoordX(ov, cur);
            int cy = RegionCoordY(ov, cur);

            // 4 соседа по региональной сетке.
            TryRelaxRegion(ov, cur, cy * ov.DimX + (cx + 1), cx + 1 < ov.DimX,
                trx, tryRegionY, b, open, ref openCount);
            TryRelaxRegion(ov, cur, cy * ov.DimX + (cx - 1), cx > 0,
                trx, tryRegionY, b, open, ref openCount);
            TryRelaxRegion(ov, cur, (cy + 1) * ov.DimX + cx, cy + 1 < ov.DimY,
                trx, tryRegionY, b, open, ref openCount);
            TryRelaxRegion(ov, cur, (cy - 1) * ov.DimX + cx, cy > 0,
                trx, tryRegionY, b, open, ref openCount);
        }

        if (parent[tr] < 0 && sr != tr)
            return null;

        // Восстановление в RevScratch (без List<int>(16)): цепочка parent
        // длиной ≤ числа регионов. Кэшируем shared — возвращаем sized-копию.
        // @destroyer: parent-цикл теоретически возможен при data race, поэтому
        // счётчик итераций ограничен RevScratch.Length — зацикливания нет.
        var rb = _buffers.Value;
        int rn = 0;
        int curNode = tr;
        while (curNode >= 0 && rn < rb.RevScratch.Length)
        {
            rb.RevScratch[rn++] = curNode;
            curNode = parent[curNode];
        }
        if (curNode >= 0)
            return null; // цепочка длиннее scratch — патологично, без пути
        var result = new int[rn];
        for (int i = 0; i < rn; i++)
        {
            result[i] = rb.RevScratch[rn - 1 - i];
        }
        return result;
    }

    private static void TryRelaxRegion(OverlaySnapshot ov, int from, int to, bool valid,
        int trx, int tryRegionY, SearchBuffers b, int[] open, ref int openCount)
    {
        if (!valid)
            return;

        // Ребро существует только если между регионами есть портал.
        Dir dir = DirectionBetween(ov, from, to);
        if (dir == Dir.None)
            return;

        int idx = from * 4 + (byte)dir;
        if (ov.EdgeCount[idx] <= 0)
            return;

        if (b.RClosed[to])
            return;

        int tentative = b.RG[from] + 1;
        if (tentative >= b.RG[to])
            return;

        b.RG[to] = tentative;
        b.RParent[to] = from;

        // Индекс по f = g + h в куче.
        long f = (long)tentative + RegionHeuristic(ov, to, trx, tryRegionY);
        RegionPush(b, open, ref openCount, to, (int)Math.Min(f, int.MaxValue));
    }

    /// <summary>Клеточная (чебышёва) оценка между регионами, в узлах.</summary>
    private static int RegionHeuristic(OverlaySnapshot ov, int regionId, int trx, int tryRegionY)
    {
        int dx = Math.Abs(RegionCoordX(ov, regionId) - trx);
        int dy = Math.Abs(RegionCoordY(ov, regionId) - tryRegionY);
        return Math.Max(dx, dy);
    }

    // ------------------------------------------------------------------
    // Детальный уровень: A* по клеткам локального окна
    // ------------------------------------------------------------------

    private int[] GetSegmentCached(int fx, int fy, int tx, int ty)
    {
        int packedFrom = fy * _mapWidth + fx;
        int packedTo = ty * _mapWidth + tx;
        ulong key = ((ulong)packedFrom << 32) | (uint)packedTo;

        // P0-1: без счётчиков кэша (горячий параллельный путь).
        if (_segmentCache.TryGetValue(key, out int[] cached))
        {
            return cached;
        }

        int[] seg = ComputeLocalSegment(fx, fy, tx, ty);
        if (seg == null)
            return null;

        // Переполнение: четверть вместо Clear (thundering herd).
        if (_segmentCache.Count >= MaxSegmentCache)
        {
            int toRemove = MaxSegmentCache / 4;
            foreach (var k in _segmentCache.Keys)
            {
                if (toRemove-- <= 0) break;
                _segmentCache.TryRemove(k, out _);
            }
        }
        _segmentCache[key] = seg;
        return seg;
    }

    // ------------------------------------------------------------------
    // Бинарные кучи (lazy-deletion A*)
    // ------------------------------------------------------------------

    private static void PushMin(SearchBuffers b, int[] open, Span<int> value, ref int openCount, int node, int key)
    {
        int i = openCount;
        open[openCount++] = node;
        value[node] = key;
        while (i > 0)
        {
            int p = (i - 1) >> 1;
            if (value[open[p]] <= key)
                break;
            open[i] = open[p];
            i = p;
        }
        open[i] = node;
    }

    private static int PopMin(SearchBuffers b, int[] open, Span<int> value, ref int openCount)
    {
        int root = open[0];
        openCount--;
        if (openCount > 0)
        {
            int moved = open[openCount];
            int key = value[moved];
            int i = 0;
            while (true)
            {
                int left = i * 2 + 1;
                if (left >= openCount)
                    break;
                int right = left + 1;
                int child = left;
                if (right < openCount && value[open[right]] < value[open[left]])
                    child = right;
                if (value[open[child]] >= key)
                    break;
                open[i] = open[child];
                i = child;
            }
            open[i] = moved;
        }
        return root;
    }

    // Region-куча: буферы динамические (IndexableBuffer), value передаётся
    // как Span — резолвит ref-поле один раз на операцию heap'а.
    private static void RegionPush(SearchBuffers b, int[] open, ref int openCount, int node, int key)
    {
        PushMin(b, open, b.RValue.Data.AsSpan(0, b.RValue.Capacity), ref openCount, node, key);
    }

    private static int RegionPop(SearchBuffers b, int[] open, ref int openCount)
    {
        return PopMin(b, open, b.RValue.Data.AsSpan(0, b.RValue.Capacity), ref openCount);
    }

    private void LocalPush(int[] open, ref int openCount, int node, int key)
    {
        var b = _buffers.Value;
        PushMin(b, open, b.Value, ref openCount, node, key);
    }

    private int LocalPop(int[] open, ref int openCount)
    {
        var b = _buffers.Value;
        return PopMin(b, open, b.Value, ref openCount);
    }

    /// <summary>
    /// Полноценный A* по клеткам в окне, охватывающем регионы обеих точек
    /// плюс одно кольцо запаса (до 3x3 регионов = 2304 клетки). Это «от входа
    /// зоны к её выходу»: точка входа и точка выхода лежат на границах,
    /// окно включает оба региона, поэтому путь может свободно пересекать
    /// границу в любом легальном месте.
    /// </summary>
    private int[] ComputeLocalSegment(int fx, int fy, int tx, int ty)
    {
        if (tx < 0 || ty < 0 || tx >= _mapWidth || ty >= _mapHeight)
            return null;
        if (IsCellBlocked(tx, ty))
            return null;

        int rx0 = fx >> RegionShift, ry0 = fy >> RegionShift;
        int rx1 = tx >> RegionShift, ry1 = ty >> RegionShift;

        int minRX = Math.Max(0, Math.Min(rx0, rx1) - 1);
        int maxRX = Math.Min(_regionDimX - 1, Math.Max(rx0, rx1) + 1);
        int minRY = Math.Max(0, Math.Min(ry0, ry1) - 1);
        int maxRY = Math.Min(_regionDimY - 1, Math.Max(ry0, ry1) + 1);

        int minX = minRX << RegionShift;
        int minY = minRY << RegionShift;
        int maxX = Math.Min(((maxRX + 1) << RegionShift) - 1, _mapWidth - 1);
        int maxY = Math.Min(((maxRY + 1) << RegionShift) - 1, _mapHeight - 1);

        int ww = maxX - minX + 1;
        int hh = maxY - minY + 1;
        int cap = ww * hh;
        if (cap > LocalCapacity)
            return null;

        var b = _buffers.Value;
        int[] g = b.G;
        int[] parent = b.Parent;
        int[] open = b.Open;
        bool[] closed = b.Closed;

        Array.Fill(g, int.MaxValue);
        Array.Fill(parent, -1);
        Array.Fill(closed, false);

        int sidx = (fy - minY) * ww + (fx - minX);
        int tidx = (ty - minY) * ww + (tx - minX);
        if (sidx < 0 || tidx < 0 || sidx >= cap || tidx >= cap)
            return null;

        g[sidx] = 0;
        int openCount = 0;
        LocalPush(open, ref openCount, sidx, OctileDist(fx - tx, fy - ty));

        const int MaxExpand = 4096;
        int expanded = 0;

        while (openCount > 0 && expanded < MaxExpand)
        {
            int u = LocalPop(open, ref openCount);
            if (closed[u])
                continue;
            closed[u] = true;
            expanded++;

            if (u == tidx)
                break;

            int ux = u % ww + minX;
            int uy = u / ww + minY;
            int uCost = g[u];

            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = uy + dy;
                if (ny < minY || ny > maxY)
                    continue;
                int nyw = ny * _mapWidth;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    int nx = ux + dx;
                    if (nx < minX || nx > maxX)
                        continue;

                    int cellCost = _cost[nyw + nx];
                    if (cellCost == CostBlocked)
                        continue;

                    int step = (dx != 0 && dy != 0) ? 14 : 10;
                    if (cellCost == CostWater)
                        step *= 3;

                    int v = (ny - minY) * ww + (nx - minX);
                    if (closed[v])
                        continue;

                    int tentative = uCost + step;
                    if (tentative < g[v])
                    {
                        g[v] = tentative;
                        parent[v] = u;
                        int f = tentative + OctileDist(nx - tx, ny - ty);
                        LocalPush(open, ref openCount, v, f);
                    }
                }
            }
        }

        if (g[tidx] == int.MaxValue)
            return null;

        // Восстановление пути (включая старт и цель) в RevScratch без List.
        // Длина ограничена cap окна (≤ LocalCapacity); результат — sized-копия
        // для shared кэша сегментов.
        int n = 0;
        int cur = tidx;
        while (cur != -1 && n < b.RevScratch.Length)
        {
            b.RevScratch[n++] = cur;
            if (cur == sidx)
                break;
            cur = parent[cur];
        }
        if (n == 0 || (n >= b.RevScratch.Length && cur != -1 && cur != sidx))
            return null;
        var result = new int[n];
        for (int i = 0; i < n; i++)
        {
            int loc = b.RevScratch[n - 1 - i];
            int lx = loc % ww + minX;
            int ly = loc / ww + minY;
            result[i] = ly * _mapWidth + lx;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OctileDist(int dx, int dy)
    {
        dx = Math.Abs(dx);
        dy = Math.Abs(dy);
        return 10 * Math.Max(dx, dy) - 4 * Math.Min(dx, dy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCellBlocked(int tx, int ty)
    {
        if ((uint)tx >= (uint)_mapWidth || (uint)ty >= (uint)_mapHeight)
            return true;
        return _cost[ty * _mapWidth + tx] == CostBlocked;
    }

    // ------------------------------------------------------------------
    // Прямая видимость и сглаживание
    // ------------------------------------------------------------------

    /// <summary>
    /// Клеточная прямая видимость по Брезенхэму: сегмент свободен, если ни
    /// одна клетка по пути не блокирована стеной (вода допустима — движение
    /// напрямую сквозь воду разрешено, лишь медленнее).
    /// </summary>
    /// <summary>
    /// Целочисленный Брезенхем: ~5ns на клетку вместо ~30 (float-деление +
    /// Math.Round ~15ns на шаг). SmoothPath делает LOS на каждый узел пути —
    /// при 200 узлах × 30 шагов это был главный жор сглаживания.
    /// </summary>
    private bool HasLineOfSight(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            if (IsCellBlocked(x0, y0))
                return false;
            if (x0 == x1 && y0 == y1)
                return true;
            int e2 = err << 1;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Длина водного отрезка вдоль LOS (тайлы воды по Брезенхему).
    /// Стены здесь не проверяем — их уже отсеял HasLineOfSight.
    /// #14: оставлен для совместимости; горячий путь TryFindPath использует
    /// LineOfSightWithWater (один проход вместо двух).
    /// </summary>
    private int WaterCrossingLength(int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int water = 0;
        while (true)
        {
            if ((uint)x0 < (uint)_mapWidth && (uint)y0 < (uint)_mapHeight
                && _cost[y0 * _mapWidth + x0] == CostWater)
                water++;
            if (x0 == x1 && y0 == y1)
                return water;
            int e2 = err << 1;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// #14: совмещённый LOS + подсчёт воды за ОДИН проход Брезенхема.
    /// Возвращает false при первой же стене; иначе true + число водных
    /// тайлов отрезка в <paramref name="waterLength"/>. Экономит ~50% работы
    /// прямого коридора против пары HasLineOfSight + WaterCrossingLength.
    /// </summary>
    private bool LineOfSightWithWater(int x0, int y0, int x1, int y1, out int waterLength)
    {
        waterLength = 0;
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            if (IsCellBlocked(x0, y0))
                return false;
            if ((uint)x0 < (uint)_mapWidth && (uint)y0 < (uint)_mapHeight
                && _cost[y0 * _mapWidth + x0] == CostWater)
                waterLength++;
            if (x0 == x1 && y0 == y1)
                return true;
            int e2 = err << 1;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Прореживает детальный путь: оставляет только точки поворота, где
    /// прямая видимость между последовательными опорными точками сохраняется.
    /// PERF F4: zero-alloc — пишет в <paramref name="destination"/> (thread-local
    /// scratch вызывателя), возвращает число точек. Семантика 1-в-1 со старым
    /// List-вариантом (включая финал и fallback на последнюю точку).
    /// </summary>
    private int SmoothPath(int sx, int sy, int tx, int ty, Span<int> path, Span<int> destination)
    {
        int outLen = 0;
        int curX = sx;
        int curY = sy;
        int lastX = -1;
        int lastY = -1;

        for (int i = 0; i < path.Length; i++)
        {
            int packed = path[i];
            int px = packed % _mapWidth;
            int py = packed / _mapWidth;

            if (HasLineOfSight(curX, curY, px, py))
            {
                lastX = px;
                lastY = py;
                continue;
            }

            if (lastX >= 0)
            {
                if ((uint)outLen >= (uint)destination.Length)
                    return 0; // scratch переполнен — вызыватель retry'ит
                destination[outLen++] = lastY * _mapWidth + lastX;
                curX = lastX;
                curY = lastY;
            }

            // Текущая точка обязана быть видимой из нового опорного положения
            // (она — первая невидимая из предыдущего).
            lastX = px;
            lastY = py;
        }

        // Финал: марш мимо последней опорной к цели.
        if (HasLineOfSight(curX, curY, tx, ty))
        {
            if ((uint)outLen >= (uint)destination.Length)
                return 0;
            destination[outLen++] = ty * _mapWidth + tx;
        }
        else if (lastX >= 0 && !(lastX == tx && lastY == ty))
        {
            if ((uint)outLen >= (uint)destination.Length)
                return 0;
            destination[outLen++] = lastY * _mapWidth + lastX;
        }
        else if (outLen == 0)
        {
            if (path.Length == 0 || destination.Length == 0)
                return 0;
            destination[outLen++] = path[path.Length - 1];
        }

        return outLen;
    }
}