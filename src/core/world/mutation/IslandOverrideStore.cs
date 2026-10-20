using System;
using System.Collections.Generic;
using System.IO;

namespace Game.Core.WorldStreaming.Mutation;

// ============================================================================
// §118 фаза 4: ОСТРОВНОЙ стор дельт (PLAN.md §118.1, аудит §5.4/§6.1).
//
// Почему отдельный стор, а не чанк-стриминг:
//  * StreamingWorldView.AddBlock принимает запись ТОЛЬКО для загруженного чанка
//    (`_chunkData.ContainsKey(key)`, StreamingWorldView.cs:282-283), а остров —
//    это 64×64 = 4096 чанков при конечном окне стриминга → мутации дальнего
//    края острова молча отбрасывались бы (аудит §6.1).
//  * У острова СЕЙЧАС НЕТ персистентности вообще (аудит §5.4): срубленное
//    дерево, стена, грядка исчезают при сбросе/регенерации. Этот стор её даёт.
//
// Свойства:
//  * Идемпотентность (§6.6): повторная запись → false, без исключений.
//  * Потокобезопасность (§6.7): островные мутации идут из джоб-хендлеров,
//    в т.ч. ПАРАЛЛЕЛЬНО (Commit на Phase3b) → все операции под одним lock.
//  * События стреляют ВНЕ блокировки (иначе подписчик, дёрнувший стор, даст дедлок).
//  * Батч (§6.8): инструменты пишут до 250 000 клеток за драг — применяем за
//    один захват lock, а не позаклеточно.
//  * Смена сида инвалидирует стор целиком (§6.11): чужие дельты не прилипают.
// ============================================================================

/// <summary>
/// Островное хранилище дельт: разреженные метки (kind, мировая клетка) → payload.
/// Не привязано к резидентности чанков; персистится атомарной заменой файла.
/// </summary>
public sealed class IslandOverrideStore : IWorldMutationSink
{
    /// <summary>Ключ клетки: упакованные координаты + вид метки (без аллокаций на запись).</summary>
    private readonly struct CellKey : IEquatable<CellKey>
    {
        public readonly long X;
        public readonly long Y;
        public readonly byte Kind;

        public CellKey(long x, long y, byte kind)
        {
            X = x;
            Y = y;
            Kind = kind;
        }

        public bool Equals(CellKey other) => X == other.X && Y == other.Y && Kind == other.Kind;

        public override bool Equals(object obj) => obj is CellKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = X.GetHashCode();
                h = (h * 397) ^ Y.GetHashCode();
                h = (h * 397) ^ Kind;
                return h;
            }
        }
    }

    private const uint FormatVersion = 1;
    private static readonly byte[] Magic = { (byte)'I', (byte)'S', (byte)'M', (byte)'1' };

    private readonly object _gate = new();
    private readonly Dictionary<CellKey, byte[]> _cells = new();

    /// <summary>Сид, к которому относятся текущие дельты (0 = стор не привязан).</summary>
    public uint Seed { get; private set; }

    /// <summary>Число меток в сторе.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _cells.Count;
        }
    }

    public event Action<byte, long, long> OnMutationApplied;
    public event Action<byte, long, long> OnMutationRemoved;

    public IslandOverrideStore(uint seed = 0) => Seed = seed;

    /// <summary>
    /// Привязать стор к сиду. При СМЕНЕ сида стор очищается целиком (§6.11:
    /// иначе дельты прошлого мира прилипают к новому).
    /// </summary>
    public bool RebindSeed(uint seed)
    {
        List<(byte, long, long)> removed = null;
        bool changed;
        lock (_gate)
        {
            changed = Seed != seed;
            if (changed && _cells.Count > 0)
            {
                removed = new List<(byte, long, long)>(_cells.Count);
                foreach (KeyValuePair<CellKey, byte[]> pair in _cells)
                    removed.Add((pair.Key.Kind, pair.Key.X, pair.Key.Y));
                _cells.Clear();
            }
            Seed = seed;
        }
        if (removed != null)
            foreach ((byte kind, long x, long y) in removed)
                OnMutationRemoved?.Invoke(kind, x, y);
        return changed;
    }

    /// <summary>Записать метку. false — метка уже есть (идемпотентность §6.6).</summary>
    public bool TryApply(byte kind, long cellX, long cellY, byte[] payload = null)
    {
        bool applied;
        lock (_gate)
            applied = _cells.TryAdd(new CellKey(cellX, cellY, kind), payload);
        if (applied)
            OnMutationApplied?.Invoke(kind, cellX, cellY);
        return applied;
    }

    /// <summary>Снять метку. false — метки не было.</summary>
    public bool TryRemove(byte kind, long cellX, long cellY)
    {
        bool removed;
        lock (_gate)
            removed = _cells.Remove(new CellKey(cellX, cellY, kind));
        if (removed)
            OnMutationRemoved?.Invoke(kind, cellX, cellY);
        return removed;
    }

    /// <summary>
    /// Батч за ОДИН захват хранилища (§6.8). Возвращает число реально новых записей;
    /// дубли внутри батча и уже существующие метки не применяются повторно.
    /// События стреляют после снятия блокировки, по одной на запись.
    /// </summary>
    public int ApplyBatch(ReadOnlySpan<WorldMutation> batch)
    {
        if (batch.Length == 0)
            return 0;
        List<(byte Kind, long X, long Y)> applied = null;
        lock (_gate)
        {
            for (int i = 0; i < batch.Length; i++)
            {
                WorldMutation m = batch[i];
                if (_cells.TryAdd(new CellKey(m.CellX, m.CellY, m.Kind), m.Payload))
                    (applied ??= new List<(byte, long, long)>()).Add((m.Kind, m.CellX, m.CellY));
            }
        }
        if (applied == null)
            return 0;
        foreach ((byte kind, long x, long y) in applied)
            OnMutationApplied?.Invoke(kind, x, y);
        return applied.Count;
    }

    /// <summary>Массовое снятие за один захват (расчистка/сброс зоны). Возвращает число снятых.</summary>
    public int RemoveBatch(ReadOnlySpan<WorldMutation> batch)
    {
        if (batch.Length == 0)
            return 0;
        List<(byte Kind, long X, long Y)> removed = null;
        lock (_gate)
        {
            for (int i = 0; i < batch.Length; i++)
            {
                WorldMutation m = batch[i];
                if (_cells.Remove(new CellKey(m.CellX, m.CellY, m.Kind)))
                    (removed ??= new List<(byte, long, long)>()).Add((m.Kind, m.CellX, m.CellY));
            }
        }
        if (removed == null)
            return 0;
        foreach ((byte kind, long x, long y) in removed)
            OnMutationRemoved?.Invoke(kind, x, y);
        return removed.Count;
    }

    public bool Contains(byte kind, long cellX, long cellY)
    {
        lock (_gate)
            return _cells.ContainsKey(new CellKey(cellX, cellY, kind));
    }

    public bool TryGet(byte kind, long cellX, long cellY, out byte[] payload)
    {
        lock (_gate)
            return _cells.TryGetValue(new CellKey(cellX, cellY, kind), out payload);
    }

    /// <summary>
    /// §119 P7б (task-17): обновить payload УЖЕ СУЩЕСТВУЮЩЕЙ метки без снятия
    /// (save-time refresh IslandStateSnapshot: TryApply идемпотентен и payload
    /// не перезаписывает — нужен отдельный путь). false — метки нет (новые
    /// метки создаёт только зеркало/refresh, здесь не создаются). События не
    /// стреляют: занятость клетки не изменилась, только состояние.
    /// </summary>
    public bool TrySetPayload(byte kind, long cellX, long cellY, byte[] payload)
    {
        lock (_gate)
        {
            CellKey key = new(cellX, cellY, kind);
            if (!_cells.ContainsKey(key))
                return false;
            _cells[key] = payload;
            return true;
        }
    }

    /// <summary>
    /// Перечислить метки (kind = 0 → все виды) в прямоугольнике [minX..maxX]×[minY..maxY]
    /// (включительно). Без аллокаций на метку — пишет в переданный список.
    /// </summary>
    public int Enumerate(long minX, long minY, long maxX, long maxY, byte kind,
        List<(byte Kind, long CellX, long CellY)> into)
    {
        if (into == null)
            return 0;
        into.Clear();
        lock (_gate)
        {
            foreach (KeyValuePair<CellKey, byte[]> pair in _cells)
            {
                CellKey key = pair.Key;
                if (kind != 0 && key.Kind != kind)
                    continue;
                if (key.X < minX || key.X > maxX || key.Y < minY || key.Y > maxY)
                    continue;
                into.Add((key.Kind, key.X, key.Y));
            }
        }
        return into.Count;
    }

    /// <summary>Очистить стор (сброс колонии). События — по каждой снятой метке.</summary>
    public int Clear()
    {
        List<(byte, long, long)> removed = null;
        lock (_gate)
        {
            if (_cells.Count > 0)
            {
                removed = new List<(byte, long, long)>(_cells.Count);
                foreach (KeyValuePair<CellKey, byte[]> pair in _cells)
                    removed.Add((pair.Key.Kind, pair.Key.X, pair.Key.Y));
                _cells.Clear();
            }
        }
        if (removed == null)
            return 0;
        foreach ((byte kind, long x, long y) in removed)
            OnMutationRemoved?.Invoke(kind, x, y);
        return removed.Count;
    }

    // ------------------------------------------------------------------ //
    //  Персистентность (аудит §5.4: у острова её не было вообще)          //
    // ------------------------------------------------------------------ //

    private const string TempSuffix = ".tmp";

    /// <summary>
    /// Сохранить стор атомарной заменой файла (как WorldChunkStore): пишем во
    /// временный файл рядом, затем заменяем целевой. Ошибки не глотаются.
    /// </summary>
    public void Save(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Пустой путь сохранения островных дельт.", nameof(path));
        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        (CellKey Key, byte[] Payload)[] snapshot;
        uint seed;
        lock (_gate)
        {
            snapshot = new (CellKey, byte[])[_cells.Count];
            int i = 0;
            foreach (KeyValuePair<CellKey, byte[]> pair in _cells)
                snapshot[i++] = (pair.Key, pair.Value);
            seed = Seed;
        }

        string temp = path + TempSuffix;
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(seed);
            writer.Write(snapshot.Length);
            foreach ((CellKey key, byte[] payload) in snapshot)
            {
                writer.Write(key.X);
                writer.Write(key.Y);
                writer.Write(key.Kind);
                int len = payload?.Length ?? -1;
                writer.Write(len);
                if (len > 0)
                    writer.Write(payload, 0, len);
            }
        }

        if (File.Exists(path))
            File.Delete(path);
        File.Move(temp, path);
    }

    /// <summary>
    /// Загрузить стор из файла. false — файла нет (не ошибка: пустой остров).
    /// Битый/чужой формат — исключение с причиной (тихо терять дельты нельзя).
    /// </summary>
    public bool Load(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(stream))
        {
            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !IsMagic(magic))
                throw new InvalidDataException($"Не файл островных дельт: {path}");
            uint version = reader.ReadUInt32();
            if (version != FormatVersion)
                throw new InvalidDataException(
                    $"Версия островных дельт {version} не поддерживается (ожидается {FormatVersion}): {path}");
            uint seed = reader.ReadUInt32();
            int count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException($"Отрицательное число записей ({count}): {path}");

            var loaded = new Dictionary<CellKey, byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                long x = reader.ReadInt64();
                long y = reader.ReadInt64();
                byte kind = reader.ReadByte();
                int len = reader.ReadInt32();
                byte[] payload = null;
                if (len > 0)
                    payload = reader.ReadBytes(len);
                else if (len < -1)
                    throw new InvalidDataException($"Некорректная длина payload ({len}) в {path}");
                loaded[new CellKey(x, y, kind)] = payload;
            }

            lock (_gate)
            {
                _cells.Clear();
                foreach (KeyValuePair<CellKey, byte[]> pair in loaded)
                    _cells[pair.Key] = pair.Value;
                Seed = seed;
            }
        }
        return true;
    }

    /// <summary>Сохранить, вернув false вместо исключения (для вызовов вне try/catch, напр. при выходе).</summary>
    public bool TrySave(string path, out string error)
    {
        try
        {
            Save(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsMagic(byte[] candidate)
    {
        for (int i = 0; i < Magic.Length; i++)
            if (candidate[i] != Magic[i])
                return false;
        return true;
    }
}