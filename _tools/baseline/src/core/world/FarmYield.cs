namespace Game.Core;

/// <summary>
/// Урожай с 1 клетки фермы (§20.3). Земля влияет только на УРОЖАЙ,
/// скорость роста везде одинаковая (60с/фаза).
/// Качество земли = среднее двух множителей (влага + плодородие),
/// каждый 0.3..1.0. Плохая земля (0.3) → ~5, хорошая (1.0) → ~40.
/// Чистый C#, без Godot API — можно звать из фоновых потоков и из UI.
/// </summary>
public static class FarmYield
{
    public const int MinDrop = 5;
    public const int MaxDrop = 40;

    /// <summary>
    /// Посчитать урожай: среднее по качеству земли + случайный разброс ±20%.
    /// rand01 — доля 0..1 (ParallelRng.NextDouble в симуляции, Random в UI).
    /// </summary>
    public static int RollDrop(float humMul, float fertMul, double rand01)
    {
        float quality = (humMul + fertMul) * 0.5f;
        if (quality < 0.3f) quality = 0.3f;
        if (quality > 1.0f) quality = 1.0f;
        float mean = MinDrop + (MaxDrop - MinDrop) * ((quality - 0.3f) / 0.7f);
        if (rand01 < 0.0) rand01 = 0.0;
        if (rand01 > 1.0) rand01 = 1.0;
        float result = mean * (float)(0.8 + rand01 * 0.4);
        int drop = (int)result;
        if (drop < MinDrop) drop = MinDrop;
        if (drop > MaxDrop) drop = MaxDrop;
        return drop;
    }
}
