using System.Collections.Concurrent;
using System.Collections.Generic;
using Game.Core;

namespace Game.Simulation;

/// <summary>
/// Прогресс ломки/стройки по клеткам для визуала ProcessOfWork
/// (uid://qqhtvxqngv4a, атлас 3×2 = 6 стадий: (0,0) начало … (2,1) конец).
/// Писатели — хендлеры из sim-потоков (Commit/ExecuteParallel): только
/// Report/ReportFraction (lock-free ConcurrentDictionary, троттлинг по смене
/// стадии). Читатель — MapRenderer._Process (главный поток): DrainDirty забирает
/// клетки со сменой стадии, рендер кладёт SetCell(processSource, atlas).
/// Завершение/отмена — Clear (хендлеры зовут при Complete/Unregister).
/// Протухшие записи (агент ушёл/работа снята без Clear) чистит SweepStale
/// по wall-clock (зовёт MapRenderer раз в ~2с, стирает из слоя).
/// Порядок блокировок: своих lock нет вообще (только concurrent-структуры),
/// внешних менеджеров не зовём — дедлоков быть не может.
/// </summary>
public sealed class WorkProgressTracker
{
    public static WorkProgressTracker Instance { get; } = new();

    /// <summary>Сколько стадий в атласе ProcessOfWork (3×2).</summary>
    public const int StageCount = 6;

    /// <summary>Атлас-координата стадии: 0→(0,0) … 5→(2,1).</summary>
    public static (int X, int Y) AtlasForStage(int stage)
    {
        int s = stage < 0 ? 0 : stage > StageCount - 1 ? StageCount - 1 : stage;
        return (s % 3, s / 3);
    }

    /// <summary>Стадия 0..5 из доли готовности 0..1.</summary>
    public static int StageForFraction(float fraction)
    {
        if (fraction <= 0f) return 0;
        if (fraction >= 1f) return StageCount - 1;
        int s = (int)(fraction * StageCount);
        return s < 0 ? 0 : s > StageCount - 1 ? StageCount - 1 : s;
    }

    private readonly struct Entry
    {
        public readonly int Stage;
        public readonly long Ticks;
        public Entry(int stage, long ticks) { Stage = stage; Ticks = ticks; }
    }

    private readonly ConcurrentDictionary<(int X, int Y), Entry> _stages = new();
    private readonly ConcurrentQueue<(int X, int Y, int Stage)> _dirty = new();
    // Дедуп очереди: клетка в _dirty не чаще раза на смену стадии.
    private readonly ConcurrentDictionary<(int X, int Y), int> _queued = new();

    /// <summary>
    /// Доложить долю готовности 0..1 (хендлеры: WorkProgress/Duration).
    /// В очередь уходит только СМЕНА стадии (0→1→…→5), повторы той же
    /// стадии — обновление метки времени без очереди (дёшево).
    /// </summary>
    public void ReportFraction(int x, int y, float fraction)
    {
        Report(x, y, StageForFraction(fraction));
    }

    public void Report(int x, int y, int stage)
    {
        int s = stage < 0 ? 0 : stage > StageCount - 1 ? StageCount - 1 : stage;
        var key = (x, y);
        long now = System.DateTime.UtcNow.Ticks;
        _stages.AddOrUpdate(key,
            addValueFactory: _ =>
            {
                _queued[key] = s;
                _dirty.Enqueue((x, y, s));
                return new Entry(s, now);
            },
            updateValueFactory: (_, old) =>
            {
                if (old.Stage == s)
                    return new Entry(s, now);
                _queued[key] = s;
                _dirty.Enqueue((x, y, s));
                return new Entry(s, now);
            });
    }

    /// <summary>
    /// ЕДИНЫЙ финиш работы по клетке (успех, отмена, застревание, снос).
    /// Гасит спрайт везде и всегда: кладёт маркер стирания (-1) в очередь
    /// так, чтобы DrainDirty его НЕ потерял (старый Clear удалял маркер
    /// заранее — стирание терялось и спрайт залипал навсегда).
    /// Зови Finish из ВСЕХ выходов хендлера: Complete + OnCancel.
    /// Застрявший агент уже идёт через OnCancel (ReleaseJobWorker) — отдельно
    /// ничего делать не надо. Повторный вызов — без вреда.
    /// </summary>
    public void Finish(int x, int y)
    {
        var key = (x, y);
        _stages.TryRemove(key, out _);
        _queued[key] = EraseStage;
        _dirty.Enqueue((x, y, EraseStage));
    }

    private const int EraseStage = -1;

    /// <summary>Работа завершена/отменена: убрать из трекера, вернуть стирание.</summary>
    public void Clear(int x, int y) => Finish(x, y);

    /// <summary>
    /// Забрать накопившиеся смены (зовёт главный поток из _Process).
    /// Бюджетом: не больше maxCount за кадр.
    /// </summary>
    public List<(int X, int Y, int Stage)> DrainDirty(List<(int X, int Y, int Stage)> destination, int maxCount)
    {
        destination.Clear();
        int n = 0;
        while (n < maxCount && _dirty.TryDequeue(out var item))
        {
            var key = (item.X, item.Y);
            // Схлопываем дубли: в слой идёт только актуальная стадия.
            // Маркер стирания (-1) НЕ выбрасываем заранее: стирание обязан
            // увидеть рендер, иначе спрайт залипнет (старый баг).
            if (item.Stage == EraseStage)
            {
                // Если после стирания уже пришла новая работа — стирание
                // устарело, отдаём свежую стадию.
                if (_queued.TryGetValue(key, out int cur) && cur != EraseStage)
                    continue;
                _queued.TryRemove(key, out _);
                destination.Add((item.X, item.Y, EraseStage));
                n++;
                continue;
            }
            if (!_queued.TryGetValue(key, out int latest) || latest == EraseStage)
                continue; // уже финишировали — промежуточные стадии не рисуем
            _queued.TryRemove(key, out _);
            // Добираем хвост дубликатов той же клетки из очереди.
            while (_dirty.TryPeek(out var peek) && peek.X == item.X && peek.Y == item.Y)
            {
                if (peek.Stage == EraseStage)
                    break; // стирание обработаем следующим кругом
                _dirty.TryDequeue(out _);
                if (_queued.TryRemove(key, out int l2)) latest = l2;
            }
            destination.Add((item.X, item.Y, latest));
            n++;
        }
        return destination;
    }

    /// <summary>
    /// Стереть протухшие (нет обновлений дольше staleAfter): агент ушёл,
    /// работа снята без Clear. Возвращает список стёртых (для EraseCell).
    /// Звать из главного потока редко (раз в ~2с).
    /// </summary>
    public List<(int X, int Y)> SweepStale(List<(int X, int Y)> destination, System.TimeSpan staleAfter)
    {
        destination.Clear();
        long now = System.DateTime.UtcNow.Ticks;
        long limit = staleAfter.Ticks;
        foreach (var kv in _stages)
        {
            if (now - kv.Value.Ticks > limit)
            {
                if (_stages.TryRemove(kv.Key, out _))
                {
                    _queued.TryRemove(kv.Key, out _);
                    destination.Add(kv.Key);
                    if (destination.Count >= 512)
                        break;
                }
            }
        }
        return destination;
    }
}
