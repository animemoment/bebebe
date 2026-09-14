using System;
using System.Threading.Tasks;

namespace Game.Core;

/// <summary>
/// Почвенная влажность: 0 = абсолютно сухо, 100 = норма, 200 = очень мокро.
/// Верхней границы НЕТ (у воды может быть 250+). Вода = 200 (ей самой влага
/// не считается — это вода, и так видно). &gt;100 = переувлажнение.
/// Медленная динамика: тик раз в 30 игровых минут, цифры почти не прыгают.
/// Хранение плоское (y*W+x), double-buffer, int-only, ноль аллокаций в тике.
/// Чистый C#, без Godot API — можно звать из фоновых потоков.
/// </summary>
public sealed class HumidityMap
{
    /// <summary>Опорное значение для шкалы цвета/ферм (не кламп!).</summary>
    public const int MaxMoisture = 200;
    public const int WaterMoisture = 200;
    /// <summary>Радиус увлажнения от воды в клетках (с затуханием).</summary>
    public const int WaterSpreadRadius = 10;

    /// <summary>
    /// Интервал тика в секундах игрового времени: 30 игровых минут
    /// (0.5 игрового часа = 250 геймсек). Процесс ооочень медленный, как в жизни.
    /// </summary>
    public const float TickIntervalGameSec = 250.0f;

    /// <summary>
    /// Минимум влажности клетки (без учёта внешних факторов вроде выкачки):
    /// ниже 5% (10 единиц) не падает никогда — капиллярная влага всегда есть.
    /// </summary>
    public const int MinMoisture = 10;

    public int Width { get; }
    public int Height { get; }

    // Пинг-понг буферы влажности. int[] — верхней границы нет (у воды 250+).
    // 512² × 4 байта × 2 буфера = 2МБ, тик раз в 30 игровых минут — не критично.
    private int[] _cur;
    private int[] _nxt;
    // Подпитка от воды 0..R: чебышевская дистанция до ближайшей воды,
    // закодированная как сила (R-дистанция): рядом с водой = R, вдали = 0.
    // Предрасчёт при генерации/изменении карты (BFS от воды, радиус R=10).
    private byte[] _waterFeed;

    public HumidityMap(int width, int height)
    {
        Width = width;
        Height = height;
        _cur = new int[width * height];
        _nxt = new int[width * height];
        _waterFeed = new byte[width * height];
    }

    /// <summary>Влажность клетки (0+, верхней границы нет). OOB → 0.</summary>
    public int Get(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return 0;
        return _cur[y * Width + x];
    }

    /// <summary>Прямая установка (генерация/дебаг). Пол MinMoisture, верха нет.</summary>
    public void Set(int x, int y, int value)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        _cur[y * Width + x] = Math.Max(value, MinMoisture);
    }

    /// <summary>Сила подпитки от воды 0..R (R = у самой воды). OOB → 0.</summary>
    public int WaterFeedAt(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return 0;
        return _waterFeed[y * Width + x];
    }

    /// <summary>
    /// Начальная заливка из MapGenerator: вода = 200, градиент от воды
    /// (10 клеток с затуханием) поверх фона 10..110 по шуму. + строит waterFeed.
    /// </summary>
    public void Initialize(TileType[,] ground, float[,] moistureNoise)
    {
        if (ground == null || moistureNoise == null)
            return;
        int w = Math.Min(Width, ground.GetLength(0));
        int h = Math.Min(Height, ground.GetLength(1));
        int nw = moistureNoise.GetLength(0);
        int nh = moistureNoise.GetLength(1);

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = y * Width + x;
                if (ground[x, y] == TileType.Water)
                {
                    _cur[idx] = WaterMoisture;
                    continue;
                }
                float n = (x < nw && y < nh) ? moistureNoise[x, y] : 0.5f;
                // Шум 0..1 → 10..110: влага есть ВЕЗДЕ (грунтовые воды/осадки).
                _cur[idx] = MinMoisture + (int)(n * 100f);
            }

        RebuildWaterNear(ground);

        // Градиент от воды: чем ближе, тем мокрее (до +140 у берега → ~250).
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = y * Width + x;
                if (ground[x, y] == TileType.Water)
                    continue;
                int feed = _waterFeed[idx]; // 0..R
                if (feed > 0)
                    _cur[idx] = _cur[idx] + feed * feed * 14 / 10;
            }

        Array.Copy(_cur, _nxt, _cur.Length);
    }

    /// <summary>
    /// Перестроить поле подпитки от воды (после терраформинга).
    /// BFS от всех водных клеток, радиус WaterSpreadRadius=10, чебышевская метрика.
    /// Сила = R - дистанция + 1 (у берега 10, на краю 1).
    /// </summary>
    public void RebuildWaterNear(TileType[,] ground)
    {
        if (ground == null)
            return;
        int w = Math.Min(Width, ground.GetLength(0));
        int h = Math.Min(Height, ground.GetLength(1));
        const int R = WaterSpreadRadius;
        Array.Clear(_waterFeed, 0, _waterFeed.Length);

        // Кольцевой буфер очереди BFS (кодируем x,y в один int).
        int[] queue = new int[w * h];
        int head = 0, tail = 0;
        // Дистанция хранится прямо в _waterFeed инверсией: 0 = не посещено,
        // иначе сила. Воду помечаем отдельно проходом ниже (сила R).
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (ground[x, y] != TileType.Water)
                    continue;
                // Соседи воды в радиусе 1 — первый фронт.
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        int nx = x + ox, ny = y + oy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        if (ground[nx, ny] == TileType.Water) continue;
                        int nidx = ny * Width + nx;
                        if (_waterFeed[nidx] != 0) continue;
                        _waterFeed[nidx] = R; // сила 10 у берега
                        queue[tail++] = (ny * w + nx);
                    }
            }

        // BFS-волна: сила падает на 1 за клетку.
        while (head < tail)
        {
            int code = queue[head++];
            int cx = code % w, cy = code / w;
            int cidx = cy * Width + cx;
            int strength = _waterFeed[cidx];
            if (strength <= 1) continue; // дальше радиуса не идём
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (ox == 0 && oy == 0) continue;
                    int nx = cx + ox, ny = cy + oy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    if (ground[nx, ny] == TileType.Water) continue;
                    int nidx = ny * Width + nx;
                    if (_waterFeed[nidx] != 0) continue;
                    _waterFeed[nidx] = (byte)(strength - 1);
                    queue[tail++] = (ny * w + nx);
                }
        }
    }

    /// <summary>
    /// Ооочень медленный тик влажности (раз в 30 игровых минут).
    /// За тик клетка меняется максимум на ±1-2: испарение едва капает,
    /// диффузия /32 почти стоит. Ниже MinMoisture (5%) не падает.
    /// Влага есть ВЕЗДЕ (фон от шума/грунтовых вод), у воды — просто мокрее.
    /// isHigh — высота/горы, hasForest — лес удерживает, hasFarm — огород тянет.
    /// </summary>
    public void Tick(
        TileType[,] ground,
        Func<int, int, bool> isHigh,
        Func<int, int, bool> hasForest,
        Func<int, int, bool> hasFarm)
    {
        int w = Width;
        int h = Height;
        int[] cur = _cur;
        int[] nxt = _nxt;
        byte[] feed = _waterFeed;

        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                int me = cur[i];

                // Вода — вечный источник 200 (Dirichlet), не считается.
                // Проверяем ground ПЕРВЫМ независимо от me: иначе клетка,
                // ставшая водой после терраформинга (или дрейфовавшая ниже 200),
                // никогда не вернётся к 200 (у воды near=0, подпитки нет).
                if (IsWaterCell(ground, x, y))
                {
                    nxt[i] = WaterMoisture;
                    continue;
                }

                // Среднее по 4 соседям (Von Neumann), границы — кламп на себя.
                int left = x > 0 ? cur[i - 1] : me;
                int right = x + 1 < w ? cur[i + 1] : me;
                int up = y > 0 ? cur[i - w] : me;
                int down = y + 1 < h ? cur[i + w] : me;
                int avg = (left + right + up + down) >> 2;

                bool high = isHigh != null && isHigh(x, y);
                // Диффузия /32: почти стоит, выравнивание за часы/дни.
                int v = me + ((avg - me) >> 5);

                // Подпитка от воды по градиенту 10 клеток: у берега +3,
                // дальше затухает до +1 на краю. Верха нет — у воды копится 250+.
                int f = feed[i];
                if (f > 7) v += 3;
                else if (f > 0) v += 1;

                // Испарение — едва капает: база раз в несколько тиков.
                // Через хеш клетки: ~1/4 клеток сохнет на 1 за тик, остальные стоят.
                int evap = 0;
                if (((x * 73856093) ^ (y * 19349663)) % 4 == 0)
                    evap = 1;
                if (high && (((x * 83492791) ^ (y * 2971215073 % 100000)) & 1) == 0)
                    evap += 1; // горки сохнут чуть быстрее
                if (hasForest != null && hasForest(x, y))
                    evap = 0; // лес держит влагу полностью
                if (hasFarm != null && hasFarm(x, y))
                    v -= 1; // огород слегка тянет воду
                v -= evap;

                // Скалы плохо проводят: тянем обратно к своему значению.
                if (high)
                    v = me + ((v - me) >> 2);

                // Пол: ниже MinMoisture не падает (капиллярная влага всегда есть).
                // Верха НЕТ: у воды влага копится сколько влезет (250+).
                if (v < MinMoisture) v = MinMoisture;
                nxt[i] = v;
            }
        });

        // Swap буферов.
        _cur = nxt;
        _nxt = cur;
    }

    private static bool IsWaterCell(TileType[,] ground, int x, int y)
    {
        if (ground == null)
            return false;
        if ((uint)x >= (uint)ground.GetLength(0) || (uint)y >= (uint)ground.GetLength(1))
            return false;
        return ground[x, y] == TileType.Water;
    }

    /// <summary>
    /// Множитель роста культур по влажности (мягкая трапеция, минимум 0.3 —
    /// засуха не убивает всё, культуры до грунтовых вод не дотягиваются).
    /// Выше 185 — всё то же болото 0.4 (верхней границы у влаги нет,
    /// но гнить сильнее уже некуда).
    /// </summary>
    public static float GrowthMultiplier(int moisture)
    {
        if (moisture < 15) return 0.3f;
        if (moisture < 40) return 0.3f + (moisture - 15) * (0.5f / 25f); // →0.8
        if (moisture < 70) return 0.8f + (moisture - 40) * (0.2f / 30f); // →1.0
        if (moisture <= 130) return 1.0f; // плато оптимума
        if (moisture <= 170) return 1.0f - (moisture - 130) * (0.4f / 40f); // →0.6
        if (moisture <= 185) return 0.6f - (moisture - 170) * (0.2f / 15f); // →0.4
        return 0.4f; // болото (и всё что мокрее — тоже 0.4)
    }
}
