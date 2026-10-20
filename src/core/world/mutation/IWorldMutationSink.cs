using System;

namespace Game.Core.WorldStreaming.Mutation;

// ============================================================================
// §118 фаза 4: единый канал записи островных мутаций (PLAN.md §118.1,
// «IWorldMutationSink (мутация = массив + дельта + событие)»).
//
// КЛЮЧЕВОЕ ОГРАНИЧЕНИЕ аудита (docs/MapDataWriterAudit.md §6.1): мутации
// острова НЕЛЬЗЯ писать в чанк-стриминг — StreamingWorldView.AddBlock работает
// только с ЗАГРУЖЕННЫМИ чанками, остров = 64×64 = 4096 чанков, окно стриминга
// конечно → мутации дальнего края молча теряются. Поэтому остров пишет в
// ОТДЕЛЬНЫЙ override-стор (IslandOverrideStore), не привязанный к
// резидентности чанков. Пишут в мир-стриминг ТОЛЬКО мир-инструменты за
// кромкой (StreamingToolPlacement, §28).
//
// КОНТРАКТ:
//  * Идемпотентность обязательна (§6.6): повторная запись существующей метки →
//    false, БЕЗ ретраев и БЕЗ исключений.
//  * Батч обязателен (§6.8): инструменты пишут до 250 000 клеток за драг,
//    позаклеточный API даёт репинт-шторм. Батч применяет записи за один
//    захват хранилища.
//  * kind — СТАБИЛЬНЫЕ теги WorldDeltaKind (не изобретать номера).
// ============================================================================

/// <summary>Одна островная мутация для батча. Payload — opaque-данные kind'а (может быть null).</summary>
public readonly struct WorldMutation
{
    public byte Kind { get; }
    public long CellX { get; }
    public long CellY { get; }
    public byte[] Payload { get; }

    public WorldMutation(byte kind, long cellX, long cellY, byte[] payload = null)
    {
        Kind = kind;
        CellX = cellX;
        CellY = cellY;
        Payload = payload;
    }
}

/// <summary>
/// Единственный канал записи островных мутаций (фаза 4). Реализация —
/// IslandOverrideStore; в фазе 5 сюда же подключается гейт инструментов.
/// </summary>
public interface IWorldMutationSink
{
    /// <summary>
    /// Записать метку. false — метка УЖЕ существует (идемпотентность §6.6),
    /// без исключений и ретраев. Владение payload переходит хранилищу
    /// (как в записях WorldDeltaKind); null допустим.
    /// </summary>
    bool TryApply(byte kind, long cellX, long cellY, byte[] payload = null);

    /// <summary>Снять метку. false — метки не было (идемпотентность).</summary>
    bool TryRemove(byte kind, long cellX, long cellY);

    /// <summary>
    /// Применить батч за ОДИН захват хранилища (§6.8, драг до 250k клеток).
    /// Возвращает число РЕАЛЬНО применённых (новых) записей; дубли внутри
    /// батча и уже существующие метки не применяются повторно.
    /// </summary>
    int ApplyBatch(ReadOnlySpan<WorldMutation> batch);

    /// <summary>
    /// Снять батч за ОДИН захват хранилища (зеркало ApplyBatch: снятие разметки
    /// фермы/склада/строек приходит пачками). Возвращает число реально снятых.
    /// </summary>
    int RemoveBatch(ReadOnlySpan<WorldMutation> batch);

    /// <summary>Метка применена (новая). Стреляет ТОЛЬКО при реальном изменении, вне блокировки хранилища.</summary>
    event Action<byte, long, long> OnMutationApplied;

    /// <summary>Метка снята. Стреляет ТОЛЬКО при реальном изменении, вне блокировки хранилища.</summary>
    event Action<byte, long, long> OnMutationRemoved;
}
