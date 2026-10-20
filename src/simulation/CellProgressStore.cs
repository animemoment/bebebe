using System.Collections.Generic;

namespace Game.Simulation;

/// <summary>
/// Per-cell хранилище доли готовности работ 0..1 (P7б §119: «прогресс переживает
/// прерывание»). Прогресс живёт в КЛЕТКЕ, а не в агенте: прерывание (нужды, stuck,
/// отмена, перераздача) сохраняет долю работы, следующий агент продолжает с неё.
/// Экземпляр принадлежит конкретному менеджеру (TreeJobManager/StoneJobManager/
/// FarmJobManager); у ConstructionSite роль такого хранилища играет сам сайт.
/// Писатели — хендлеры из sim-потоков (OnCancel/Complete/Unmark), читатель — OnStart.
/// Вызовы редкие (старт/отмена/завершение, не ExecuteParallel) — обычный lock.
/// </summary>
public sealed class CellProgressStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), float> _fractions = new(256);

    /// <summary>Сохранить долю готовности 0..1 для клетки. Доля ≤ 0 запись не создаёт.</summary>
    public void SaveFraction(int x, int y, float fraction)
    {
        if (fraction <= 0f)
            return; // не начинали — хранить нечего
        if (fraction > 1f)
            fraction = 1f;
        lock (_lock)
            _fractions[(x, y)] = fraction;
    }

    /// <summary>Доля готовности клетки (0 = прогресса нет).</summary>
    public float GetFraction(int x, int y)
    {
        lock (_lock)
            return _fractions.TryGetValue((x, y), out var f) ? f : 0f;
    }

    /// <summary>Работа завершена или метка снята: клетка забывает прогресс.</summary>
    public void Clear(int x, int y)
    {
        lock (_lock)
            _fractions.Remove((x, y));
    }

    /// <summary>Полная очистка (смена региона / перезапуск карты / ClearAll менеджера).</summary>
    public void ClearAll()
    {
        lock (_lock)
            _fractions.Clear();
    }

    /// <summary>Число клеток с сохранённым прогрессом (диагностика/тесты).</summary>
    public int Count
    {
        get { lock (_lock) return _fractions.Count; }
    }
}
