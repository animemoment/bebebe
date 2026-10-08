#nullable enable
using System;

namespace Game.Core.WorldStreaming.Integration;

/// <summary>
/// Управляющий слой нового (бесконечного) мира — то, к чему будут подключаться менеджеры
/// главной игры (стены, здания, добыча, фермы, склады):
///  - правила размещения по детерминированным cell-запросам (вода/горы запрещены);
///  - единая точка записи в WorldSaveCoordinator (manifest + checkpoint);
///  - агрегированный запрос «заблокирована ли клетка» (террейн + стены);
///  - персистентность собственно дельт чанков остаётся за view (проверено E2E);
///    координатор здесь отвечает за identity мира и checkpoint, а не за дублирующую запись.
/// Не зависит от legacy MapData/MapRenderer/SimulationContext.
/// </summary>
public sealed class StreamingWorldManager
{
    private readonly IWallWorld _world;
    private ulong _seed;
    private uint _generatorVersion;
    private long _startTileX;
    private long _startTileY;
    private bool _opened;

    public StreamingWorldManager(IWallWorld world, WorldSaveCoordinator coordinator)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    /// <summary>Открыть мир (создать манифест, если его нет) и запомнить identity + стартовую клетку.</summary>
    public ResumeState Open(long desiredStartTileX, long desiredStartTileY)
    {
        ResumeState resume = Coordinator.OpenOrCreate(desiredStartTileX, desiredStartTileY);
        _seed = resume.Seed;
        _generatorVersion = resume.GeneratorVersion;
        _startTileX = resume.StartTileX;
        _startTileY = resume.StartTileY;
        _opened = true;
        return resume;
    }

    /// <summary>Сохранить checkpoint (мировое время + стартовый tile).</summary>
    public void Checkpoint(double gameTime)
    {
        if (!_opened)
            throw new InvalidOperationException("Сначала вызовите Open.");
        Coordinator.PersistCheckpoint(gameTime, _startTileX, _startTileY);
    }

    public WorldSaveCoordinator Coordinator { get; }
    public long StartTileX => _startTileX;
    public long StartTileY => _startTileY;

    /// <summary>
    /// Можно ли разместить постройку/стену в клетке: террейн-правило без загрузки чанка.
    /// false — вода или горы (базовая генерация); true — пригодная поверхность.
    /// </summary>
    public bool CanPlace(long cellX, long cellY)
        => WorldCellQuery.TerrainAt(_seed, _generatorVersion, cellX, cellY) == BaseTerrainKind.Grass;

    /// <summary>Заблокирована ли клетка (террейн + объекты/планы). null — чанк ещё не загружен.</summary>
    public bool? IsBlocked(long cellX, long cellY)
    {
        ChunkKey key = WorldCoordinates.ChunkForTile(cellX, cellY);
        if (!_world.IsLoaded(key))
            return null;
        if (HasEntityBlocking(_world, cellX, cellY))
            return true;
        // Один семпл генератора вместо двух (вода ⊂ не-трава).
        return !CanPlace(cellX, cellY);
    }

    /// <summary>
    /// Заблокирована ли клетка постоянными метками (стена, здание, склад, посев и их планы).
    /// Список видов — единый (WorldDeltaKind.BlockingKinds); для IWallWorld — только стены.
    /// </summary>
    private static bool HasEntityBlocking(IWallWorld world, long cellX, long cellY)
    {
        if (world is IBlockWorld blocks)
        {
            // §29: планы (чертежи) тоже блокируют клетку — иначе на одну клетку встанут
            // два разных плана или план поверх готового.
            byte[] kinds = WorldDeltaKind.BlockingKinds;
            for (int i = 0; i < kinds.Length; i++)
            {
                if (blocks.HasBlock(kinds[i], cellX, cellY))
                    return true;
            }
            return false;
        }
        return world.HasWall(cellX, cellY);
    }

    /// <summary>
    /// Поставить стену: чанк загружен, поверхность пригодна, клетка не занята (для IBlockWorld).
    /// Для «чистого» IWallWorld остаётся AddWall — иначе стены не вставали бы вовсе.
    /// </summary>
    public bool TryPlaceWall(long cellX, long cellY)
    {
        if (!_world.IsLoaded(WorldCoordinates.ChunkForTile(cellX, cellY)))
            return false;
        if (!CanPlace(cellX, cellY))
            return false;
        return _world is IBlockWorld
            ? TryPlaceBlock(WorldDeltaKind.Wall, cellX, cellY)
            : _world.AddWall(cellX, cellY);
    }

    /// <summary>Снять стену (если есть и чанк загружен).</summary>
    public bool TryRemoveWall(long cellX, long cellY)
    {
        if (!_world.IsLoaded(WorldCoordinates.ChunkForTile(cellX, cellY)))
            return false;
        if (!_world.HasWall(cellX, cellY))
            return false;
        return _world.RemoveWall(cellX, cellY);
    }

    /// <summary>
    /// Поставить персистентную метку указанного вида (здание/добытое/вырубленное/склад/стена).
    /// Правила: чанк загружен; для «размещаемых» видов (стена, здание, склад) поверхность
    /// должна быть пригодна (трава) и клетка не заблокирована другими метками; для
    /// «добычных» видов (добытое, вырубленное) террейн-гейт не применяется — это
    /// модификации существующей поверхности. Дубль отклоняется (идемпотентно).
    /// </summary>
    public bool TryPlaceBlock(byte kind, long cellX, long cellY)
    {
        if (!_world.IsLoaded(WorldCoordinates.ChunkForTile(cellX, cellY)))
            return false;
        if (_world is not IBlockWorld blocks)
            return false;

        bool placementKind = WorldDeltaKind.IsPlacementKind(kind);
        if (placementKind && !CanPlace(cellX, cellY))
            return false;
        if (blocks.HasBlock(kind, cellX, cellY))
            return false;
        if (placementKind && HasEntityBlocking(_world, cellX, cellY))
            return false;
        return blocks.AddBlock(kind, cellX, cellY);
    }

    /// <summary>Снять персистентную метку указанного вида (если есть и чанк загружен).</summary>
    public bool TryRemoveBlock(byte kind, long cellX, long cellY)
    {
        if (!_world.IsLoaded(WorldCoordinates.ChunkForTile(cellX, cellY)))
            return false;
        if (_world is not IBlockWorld blocks)
            return false;
        if (!blocks.HasBlock(kind, cellX, cellY))
            return false;
        return blocks.RemoveBlock(kind, cellX, cellY);
    }

    public bool IsChunkLoaded(ChunkKey key) => _world.IsLoaded(key);
    public int WallCount => _world.WallCount;
}