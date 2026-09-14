using System;
using System.Collections.Generic;
using System.Threading;

namespace Game.Simulation.Scheduling;

/// <summary>
/// Chase–Lev work-stealing deque, один на worker (PLAN.md §4.5).
/// Владелец берёт с «дна» LIFO (кэш-дружественно), воры крадут с «верха» FIFO половинами.
/// Lock-free: volatile top/bottom + Interlocked.CAS. Никаких lock.
/// В v1 (Шаг 1) структура готова, steal-half подключается на Шаге 3.
/// Переполнение → возврат false (fallback в глобальный курсор, не расти бесконечно).
/// </summary>
/// <typeparam name="T">Тип элемента, struct — ноль аллокаций в ring-буфере.</typeparam>
public sealed class WorkStealingQueue<T> where T : struct
{
    private readonly T[] _array;
    private readonly int _mask;

    // Без ключевого слова volatile (нужен ref для Interlocked) —
    // доступ только через Volatile.Read/Write + Interlocked.
    private int _top;
    private int _bottom;

    /// <summary>
    /// Создаёт deque ёмкостью <paramref name="capacity"/> (округляется вверх до степени двойки).
    /// </summary>
    public WorkStealingQueue(int capacity = 1024)
    {
        if (capacity < 2)
            capacity = 2;
        // Округление вверх до степени двойки для маскирования индексом.
        int pow2 = 1;
        while (pow2 < capacity)
            pow2 <<= 1;
        _array = new T[pow2];
        _mask = pow2 - 1;
        _top = 0;
        _bottom = 0;
    }

    /// <summary>Ёмкость ring-буфера (степень двойки).</summary>
    public int Capacity => _array.Length;

    /// <summary>
    /// Приближённое число элементов (Volatile-read; может устареть к моменту возврата).
    /// </summary>
    public int Count
    {
        get
        {
            int b = Volatile.Read(ref _bottom);
            int t = Volatile.Read(ref _top);
            int n = b - t;
            return n < 0 ? 0 : n;
        }
    }

    /// <summary>
    /// Положить элемент в дно. Только владелец (очередь — ThreadLocal).
    /// Возвращает false при переполнении (вызывающий использует глобальный курсор).
    /// </summary>
    public bool TryPushLocal(T item)
    {
        int b = Volatile.Read(ref _bottom);
        int t = Volatile.Read(ref _top);
        if (b - t >= _array.Length)
            return false; // Полна — fallback в глобальный курсор.
        _array[b & _mask] = item;
        Volatile.Write(ref _bottom, b + 1);
        return true;
    }

    /// <summary>
    /// Забрать элемент со дна (LIFO). Только владелец.
    /// </summary>
    public bool TryPopLocal(out T item)
    {
        int b = Volatile.Read(ref _bottom) - 1;
        Volatile.Write(ref _bottom, b);
        Interlocked.MemoryBarrier();
        int t = Volatile.Read(ref _top);
        if (t <= b)
        {
            item = _array[b & _mask];
            if (t == b)
            {
                // Последний элемент — гонка с вором: кто CAS-нет top, тот проиграл.
                if (Interlocked.CompareExchange(ref _top, t + 1, t) != t)
                {
                    // Вор украл одновременно — элемент ему, нам пусто.
                    item = default;
                    Volatile.Write(ref _bottom, t + 1);
                    return false;
                }
                Volatile.Write(ref _bottom, t + 1);
            }
            return true;
        }

        // Пуста — выровнять bottom к top.
        Volatile.Write(ref _bottom, t);
        item = default;
        return false;
    }

    /// <summary>
    /// Вор: забрать до половины элементов жертвы (FIFO, с верха) в <paramref name="buffer"/>.
    /// Возвращает true, если украдено хотя бы что-то. Buffer не очищается — append.
    /// ВАЖНО: <paramref name="buffer"/> обязан быть thread-local (по одному List на
    /// worker-вор): общий List между ворами дал бы data race, т.к. Add/RemoveRange
    /// выполняются без синхронизации и CAS-откат правит тот же список.
    /// </summary>
    /// <exception cref="ArgumentNullException">Если victim или buffer — null.</exception>
    public bool TryStealHalf(WorkStealingQueue<T> victim, List<T> buffer)
    {
        if (victim == null)
            throw new ArgumentNullException(nameof(victim));
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        int stolen = victim.TryStealHalfInto(buffer);
        return stolen > 0;
    }

    /// <summary>
    /// Эта очередь выступает жертвой: дописать до половины её элементов в buffer.
    /// Возвращает число украденных элементов (0 — пусто / гонка проиграна).
    /// ВАЖНО: buffer обязан быть thread-local буфером вора (см. выше).
    /// </summary>
    /// <exception cref="ArgumentNullException">Если buffer — null.</exception>
    public int TryStealHalfInto(List<T> buffer)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        int t = Volatile.Read(ref _top);
        Interlocked.MemoryBarrier();
        int b = Volatile.Read(ref _bottom);
        int n = b - t;
        if (n <= 0)
            return 0;
        // Половина остатка, минимум 1 (одиночный элемент тоже крадём).
        int stealCount = n / 2;
        if (stealCount <= 0)
            stealCount = 1;
        if (stealCount > n)
            stealCount = n;

        // Копируем ДО CAS — если CAS проиграем, скопированное игнорируется.
        int startCount = buffer.Count;
        for (int i = 0; i < stealCount; i++)
            buffer.Add(_array[(t + i) & _mask]);

        if (Interlocked.CompareExchange(ref _top, t + stealCount, t) != t)
        {
            // Гонка проиграна (владелец/воры ушли вперёд) — откатить buffer.
            buffer.RemoveRange(startCount, buffer.Count - startCount);
            return 0;
        }
        return stealCount;
    }

    // FIX круг-2 №10: кража «половины по стоимости», а не по штукам.
    /// <summary>
    /// Cost-aware кража: забрать элементы с верха, пока суммарная стоимость
    /// (Count × CostHint через <paramref name="costOf"/>) не достигнет половины
    /// общей стоимости очереди (минимум 1 элемент). Возвращает число украденных.
    /// Без costOf — эквивалент <see cref="TryStealHalfInto"/>.
    /// ВАЖНО: buffer обязан быть thread-local буфером вора.
    /// </summary>
    /// <exception cref="ArgumentNullException">Если buffer или costOf — null.</exception>
    public int TryStealHalfByCostInto(List<T> buffer, Func<T, float> costOf)
    {
        if (buffer == null)
            throw new ArgumentNullException(nameof(buffer));
        if (costOf == null)
            throw new ArgumentNullException(nameof(costOf));
        int t = Volatile.Read(ref _top);
        Interlocked.MemoryBarrier();
        int b = Volatile.Read(ref _bottom);
        int n = b - t;
        if (n <= 0)
            return 0;
        // Считаем суммарную стоимость снимка очереди (без lock — приближённо).
        double totalCost = 0.0;
        for (int i = 0; i < n; i++)
        {
            float c = costOf(_array[(t + i) & _mask]);
            totalCost += (!float.IsFinite(c) || c <= 0f) ? 1.0 : c;
        }
        double halfCost = totalCost * 0.5;
        // Набираем элементы с верха до половины стоимости (минимум 1).
        double acc = 0.0;
        int stealCount = 0;
        for (int i = 0; i < n; i++)
        {
            float c = costOf(_array[(t + i) & _mask]);
            double cc = (!float.IsFinite(c) || c <= 0f) ? 1.0 : c;
            acc += cc;
            stealCount++;
            if (acc >= halfCost)
                break;
        }
        if (stealCount <= 0)
            stealCount = 1;
        if (stealCount > n)
            stealCount = n;

        int startCount = buffer.Count;
        for (int i = 0; i < stealCount; i++)
            buffer.Add(_array[(t + i) & _mask]);

        if (Interlocked.CompareExchange(ref _top, t + stealCount, t) != t)
        {
            buffer.RemoveRange(startCount, buffer.Count - startCount);
            return 0;
        }
        return stealCount;
    }

    /// <summary>
    /// Очистить очередь. Только в quiescent-состоянии (после join всех workers,
    /// из sim-потока или из владельца до старта фазы) — никогда конкурентно
    /// из workers/воров.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _top, 0);
        Volatile.Write(ref _bottom, 0);
    }
}
