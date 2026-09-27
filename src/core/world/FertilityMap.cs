using System;
using System.Threading.Tasks;

namespace Game.Core;

/// <summary>
/// Плодородие почвы: 0 = бесплодно, 100 = норма, 200 = чернозём.
/// Вода и горы = 0 (там не растёт). Верхняя граница 200 (кламп).
/// Медленная динамика: тик раз в 60 игровых минут (в 2 раза реже влаги —
/// плодородие почти геология, цифры почти стоят). Лес слегка удобряет
/// (органика), огород истощает (вынос питательных веществ).
/// Хранение плоское (y*W+x), double-buffer, int-only, ноль аллокаций в тике.
/// Чистый C#, без Godot API — можно звать из фоновых потоков.
/// </summary>
public sealed class FertilityMap
{
    /// <summary>Максимум шкалы (кламп сверху). Вода/горы = 0.</summary>
    public const int MaxFertility = 200;

    /// <summary>
    /// Интервал тика в секундах игрового времени: 60 игровых минут
    /// (1 игровой час = 500 геймсек). Процесс ооочень медленный.
    /// </summary>
    public const float TickIntervalGameSec = 500.0f;

    public int Width { get; }
    public int Height { get; }

    // Пинг-понг буферы плодородия 0..200.
    // 512² × 4 байта × 2 буфера = 2МБ, тик раз в час игрового — не критично.
    private int[] _cur;
    private int[] _nxt;

    public FertilityMap(int width, int height)
    {
        Width = width;
        Height = height;
        _cur = new int[width * height];
        _nxt = new int[width * height];
    }

    /// <summary>Плодородие клетки (0..200). OOB → 0.</summary>
    public int Get(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return 0;
        return _cur[y * Width + x];
    }

    /// <summary>Прямая установка (генерация/дебаг). Кламп 0..200.</summary>
    public void Set(int x, int y, int value)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        _cur[y * Width + x] = Math.Clamp(value, 0, MaxFertility);
    }

    /// <summary>
    /// Истощение после сбора урожая (§20.4): значение × 0.8
    /// (100 → 80 → 64 → 51...). Кламп 0..200. _base НЕ трогаем —
    /// восстановление под паром идёт к начальному значению.
    /// RACE: тик раз в час игрового может съесть одно истощение
    /// (swap буферов) — приемлемо, следующий урожай доберёт. Без lock.
    /// Зовётся из CropGrowthManager.HarvestCrop (параллельный Commit).
    /// </summary>
    public void DrainAfterHarvest(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        int i = y * Width + x;
        int v = (int)(_cur[i] * 0.8f);
        if (v < 0) v = 0;
        else if (v > MaxFertility) v = MaxFertility;
        _cur[i] = v;
    }

    // Базовое значение для восстановления под паром (п.19.6-П3): клетка без
    // огорода медленно ползёт обратно к стартовому. 256КБ на 512², ок.
    private byte[] _base;

    /// <summary>Стартовое плодородие клетки (якорь восстановления под паром). OOB → 0.</summary>
    public int BaseAt(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || _base == null)
            return 0;
        return _base[y * Width + x];
    }

    /// <summary>
    /// Крутой склон для штрафа плодородия (п.19.6-П4): перепад высоты на клетку.
    /// Выше — эрозия смывает слой (до −70%), низины чуть заболачиваются.
    /// </summary>
    public const float SteepFertSlope = 0.012f;

    /// <summary>
    /// Начальная заливка из MapGenerator (п.19.6-П1/П2/П4): СВОЙ слой шума
    /// (fertile, сид +4242), 0..200 по шуму + пойменный бонус у воды (+40),
    /// +20 под лесом (органика), штраф за склон (до −70%). Вода и горы = 0.
    /// Требует heightMap + forestMask + waterFeed-силу (может быть null —
    /// тогда чистый шум без поправок).
    /// </summary>
    public void Initialize(TileType[,] ground, float[,] fertileNoise, float[,] heightMap = null, bool[] forestMask = null, Func<int, int, int> waterFeedAt = null)
    {
        if (ground == null || fertileNoise == null)
            return;
        int w = Math.Min(Width, ground.GetLength(0));
        int h = Math.Min(Height, ground.GetLength(1));
        int nw = fertileNoise.GetLength(0);
        int nh = fertileNoise.GetLength(1);

        int hw = heightMap != null ? heightMap.GetLength(0) : 0;
        int hh = heightMap != null ? heightMap.GetLength(1) : 0;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = y * Width + x;
                if (ground[x, y] != TileType.Grass)
                {
                    _cur[idx] = 0;
                    continue;
                }
                float n = (x < nw && y < nh) ? fertileNoise[x, y] : 0.5f;
                // Шум 0..1 → 0..200: бедные и богатые пятна есть везде.
                int v = Math.Clamp((int)(n * (MaxFertility + 1)), 0, MaxFertility);

                // Пойма (п.19.6-П2): у воды + низко = свежий аллювий, +40.
                // Болото (мокро + низко) отдельно штрафуется влагой (кислый
                // торф) — здесь только бонус поймы, влага разберётся сама.
                if (waterFeedAt != null && waterFeedAt(x, y) > 0)
                    v += 40;
                // Под лесом старт выше (органика, п.19.6-П2).
                if (forestMask != null && forestMask[idx])
                    v += 20;
                // Склон (п.19.6-П4): крутой смывает слой, пологий низинный —
                // лёгкое заболачивание (−10%).
                if (heightMap != null && x < hw && y < hh)
                {
                    float gx = (x + 1 < hw ? heightMap[x + 1, y] : heightMap[x, y])
                        - (x > 0 ? heightMap[x - 1, y] : heightMap[x, y]);
                    float gy = (y + 1 < hh ? heightMap[x, y + 1] : heightMap[x, y])
                        - (y > 0 ? heightMap[x, y - 1] : heightMap[x, y]);
                    float slope = MathF.Sqrt(gx * gx + gy * gy) * 0.5f;
                    if (slope > SteepFertSlope)
                        v = (int)(v * Math.Max(0.3f, 1f - slope * 25f)); // до −70%
                    else if (slope < 0.002f)
                        v = (int)(v * 0.9f); // застой воды в низине
                }

                _cur[idx] = Math.Clamp(v, 0, MaxFertility);
            }

        // Якорь восстановления под паром (п.19.6-П3).
        _base = new byte[_cur.Length];
        for (int i = 0; i < _cur.Length; i++)
            _base[i] = (byte)Math.Clamp(_cur[i], 0, 255);
        Array.Copy(_cur, _nxt, _cur.Length);
    }

    /// <summary>
    /// Ооочень медленный тик плодородия (раз в 60 игровых минут).
    /// За тик клетка меняется максимум на ±1: диффузия /64 почти стоит.
    /// Лес слегка удобряет (+1 раз в несколько тиков, органика),
    /// огород истощает (−1, вынос питательных веществ).
    /// Вода/горы всегда 0 (Dirichlet). Кламп 0..200.
    /// </summary>
    /// <param name="ground">Типы поверхности (для сброса воды/гор в 0).</param>
    /// <param name="forestMask">Лес удерживает/удобряет. Может быть null.</param>
    /// <param name="farmMask">Огород истощает. Может быть null.</param>
    public void Tick(TileType[,] ground, bool[] forestMask, bool[] farmMask)
    {
        int w = Width;
        int h = Height;
        int[] cur = _cur;
        int[] nxt = _nxt;
        int gw = ground != null ? ground.GetLength(0) : 0;
        int gh = ground != null ? ground.GetLength(1) : 0;

        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                int me = cur[i];

                // Вода/горы — вечный ноль (там не растёт).
                if (ground != null && (uint)x < (uint)gw && (uint)y < (uint)gh
                    && ground[x, y] != TileType.Grass)
                {
                    nxt[i] = 0;
                    continue;
                }

                // Среднее по 4 соседям (Von Neumann), границы — кламп на себя.
                int left = x > 0 ? cur[i - 1] : me;
                int right = x + 1 < w ? cur[i + 1] : me;
                int up = y > 0 ? cur[i - w] : me;
                int down = y + 1 < h ? cur[i + w] : me;
                int avg = (left + right + up + down) >> 2;

                // Диффузия /64: почти геология, выравнивание за дни.
                int v = me + ((avg - me) >> 6);

                // Лес слегка удобряет: ~1/8 клеток +1 за тик (органика).
                if (forestMask != null && forestMask[i]
                    && (((x * 73856093) ^ (y * 19349663)) & 7) == 0)
                    v += 1;
                // Огород истощает: −1 за тик (вынос питательных веществ).
                if (farmMask != null && farmMask[i])
                    v -= 1;
                // Пар (п.19.6-П3): без огорода — медленное восстановление
                // к базовому (+1 раз в 8 тиков, только вверх, не выше базы).
                else if (_base != null && me < _base[i]
                    && (((x * 83492791) ^ (y * 2971215073 % 100000)) & 7) == 0)
                    v = Math.Min(v + 1, _base[i]);

                if (v < 0) v = 0;
                else if (v > MaxFertility) v = MaxFertility;
                nxt[i] = v;
            }
        });

        // Swap буферов.
        _cur = nxt;
        _nxt = cur;
    }

    /// <summary>
    /// Множитель роста культур по плодородию (мягкая трапеция, минимум 0.3 —
    /// бедная почва не убивает всё, культуры вытягивают что могут).
    /// Выше 180 — переудобрено (засоление), всё то же 0.4.
    /// Итоговый множитель роста = humidityMul * fertilityMul (мин 0.09).
    /// </summary>
    public static float GrowthMultiplier(int fertility)
    {
        if (fertility < 20) return 0.3f;
        if (fertility < 60) return 0.3f + (fertility - 20) * (0.5f / 40f); // →0.8
        if (fertility <= 140) return 0.8f + Math.Min(fertility - 60, 40) * (0.2f / 40f); // →1.0
        if (fertility <= 180) return 1.0f - (fertility - 140) * (0.4f / 40f); // →0.6
        return 0.4f; // переудобрено (и всё что выше — тоже 0.4)
    }
}
