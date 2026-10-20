using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Game.Simulation.Scheduling;

/// <summary>
/// Кастомный partitioner для Parallel.ForEach (PLAN.md §4.4).
/// В v1 (Шаг 1) — заглушка поверх центрального атомарного курсора:
/// GetOrderableDynamicPartitions возвращает ленивый chunk-stream [start,end) батчами.
/// Полноценный учёт CostHint и рост/уполовинивание — Шаги 2/5/7.
/// Контракт через base-ctor: keysOrderedInEachPartition=true,
/// keysOrderedAcrossPartitions=false, keysNormalized=false
/// (ключи — стартовые индексы, не плотные).
/// </summary>
public sealed class AdaptivePartitioner : OrderablePartitioner<Tuple<int, int>>
{
    private readonly int _count;
    private readonly int _minBatch;
    private readonly int _maxBatch;
    private readonly Func<int, float> _costHintProvider;

    // Центральный курсор выданных элементов (один Interlocked.Add на батч).
    private long _cursor;

    /// <summary>
    /// Создаёт partitioner диапазона [0,count) с батчами [minBatch..maxBatch].
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Если count &lt; 0 или границы батча некорректны.</exception>
    public AdaptivePartitioner(int count, int minBatch, int maxBatch, Func<int, float> costHintProvider = null)
        : base(true, false, false)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Count должен быть >= 0.");
        if (minBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(minBatch), "MinBatch должен быть > 0.");
        if (maxBatch < minBatch)
            throw new ArgumentOutOfRangeException(nameof(maxBatch), "MaxBatch должен быть >= MinBatch.");
        _count = count;
        _minBatch = minBatch;
        _maxBatch = maxBatch;
        _costHintProvider = costHintProvider;
        _cursor = 0;
    }

    /// <inheritdoc/>
    public override bool SupportsDynamicPartitions => true;

    /// <summary>
    /// Сброс курсора для повторного использования инстанса (фикс @destroyer №4).
    /// Вызывать только в quiescent-состоянии (до старта/после join фазы),
    /// никогда конкурентно из worker-потоков.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _cursor, 0);
    }

    /// <summary>
    /// Возвращает ровно <paramref name="partitionCount"/> партиций (все делят один курсор).
    /// Сбрасывает курсор в начале, чтобы повторное использование инстанса
    /// не давало пустую фазу (single-use guard, фикс @destroyer №4).
    /// </summary>
    public override IList<IEnumerator<KeyValuePair<long, Tuple<int, int>>>> GetOrderablePartitions(int partitionCount)
    {
        if (partitionCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(partitionCount), "PartitionCount должен быть > 0.");
        Reset();
        var list = new List<IEnumerator<KeyValuePair<long, Tuple<int, int>>>>(partitionCount);
        for (int i = 0; i < partitionCount; i++)
            list.Add(EnumerateCore().GetEnumerator());
        return list;
    }

    /// <summary>
    /// Ленивый chunk-stream поверх атомарного курсора (один вызов = одна фаза).
    /// Сбрасывает курсор eagerly при вызове (не лениво в итераторе), поэтому
    /// повторное использование инстанса на новой фазе стартует с нуля.
    /// ВНИМАНИЕ: не вызывать N раз для N партиций — каждый вызов сбрасывает
    /// курсор; для N партиций используйте <see cref="GetOrderablePartitions"/>.
    /// Ключ — стартовый индекс (уникален, без дыр/дублей).
    /// </summary>
    public override IEnumerable<KeyValuePair<long, Tuple<int, int>>> GetOrderableDynamicPartitions()
    {
        Reset();
        return EnumerateCore();
    }

    /// <summary>Ядро итератора без сброса (сброс — только в публичных entry-point'ах выше).</summary>
    private IEnumerable<KeyValuePair<long, Tuple<int, int>>> EnumerateCore()
    {
        // Стартовый размер: середина между min и max (v1; CostHint — Шаг 5).
        int batch = Math.Min(_maxBatch, Math.Max(_minBatch, (_minBatch + _maxBatch) / 2));
        while (true)
        {
            long start = Interlocked.Add(ref _cursor, batch) - batch;
            if (start >= _count)
                yield break;
            int end = (int)Math.Min(start + batch, _count);
            yield return new KeyValuePair<long, Tuple<int, int>>(
                start, Tuple.Create((int)start, end));
        }
    }
}
