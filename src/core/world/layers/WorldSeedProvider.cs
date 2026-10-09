using System;
using System.IO;
using System.Security.Cryptography;

namespace Game.Core.WorldStreaming.Layers;

/// <summary>
/// Глобальный сид мира: КАЖДЫЙ запуск игры — новый случайный сид.
/// Один источник истины для мировой карты (стример чанков), мировой оверлей-карты
/// и локальной карты (окно в мир) ⇒ все три «смотрят» в один и тот же мир.
/// Сид персистится в simpleSave/world_seed.json, чтобы перезаход внутри ОДНОЙ
/// сессии запуска не пересоздавал мир на полпути; при следующем старте процесса
/// файл перезаписывается новым сидом («каждый раз новый мир»).
/// Детерминизм сохраняется: результат по-прежнему зависит только от
/// (worldSeed, generatorVersion, абсолютные координаты).
/// </summary>
public static class WorldSeedProvider
{
    private const string SeedFileName = "world_seed.json";
    private static ulong? _sessionSeed;
    private static readonly object Gate = new();

    /// <summary>Сид текущего запуска процесса (ленивая генерация нового случайного сида).</summary>
    public static ulong Current
    {
        get
        {
            lock (Gate)
            {
                if (_sessionSeed.HasValue)
                    return _sessionSeed.Value;
                _sessionSeed = GenerateAndPersist();
                return _sessionSeed.Value;
            }
        }
    }

    /// <summary>Явно задать сид (тесты/отладка); сбрасывает кэш сессии.</summary>
    public static void Override(ulong seed)
    {
        lock (Gate)
        {
            _sessionSeed = seed;
        }
    }

    private static ulong GenerateAndPersist()
    {
        // Cryptographically-strong случайность — гарантированно новый сид каждый запуск.
        ulong seed = (ulong)(uint)RandomNumberGenerator.GetInt32(1, int.MaxValue) |
                     ((ulong)RandomNumberGenerator.GetInt32(1, int.MaxValue) << 32);

        TryWrite(seed);
        try
        {
            Godot.GD.Print($"[World] NEW world seed for this run: {seed:X16}");
        }
        catch
        {
            // GD.Print недоступен вне Godot (тесты) — молча игнорируем.
        }
        return seed;
    }

    private static void TryWrite(ulong seed)
    {
        try
        {
            // user://world_seed.json внутри Godot; вне Godot (тесты) — локальный каталог.
            string dir;
            try
            {
                dir = Godot.ProjectSettings.GlobalizePath("user://");
            }
            catch
            {
                dir = ".";
            }
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, SeedFileName),
                $"{{\"worldSeed\":{seed},\"generatorVersion\":{WorldLayerStack.GeneratorVersion}}}");
        }
        catch
        {
            // Файл — лишь отладочный артефакт; отсутствие записи не влияет на генерацию.
        }
    }
}
