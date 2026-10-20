namespace Game.Core.WorldStreaming.Integration;

/// <summary>
/// Контракт мира с персистентными клеточными метками нескольких видов
/// (стены, здания, добытое, вырубленное, склады). Виды — константы
/// <see cref="WorldDeltaKind"/>; каждая запись — «присутствие» метки в клетке,
/// payload (DTO) появляется вместе со схемой данных игры.
/// Наследует IWallWorld: стены — частный случай блока с kind Wall.
/// </summary>
public interface IBlockWorld : IWallWorld
{
    /// <summary>Добавить метку указанного вида в загруженную клетку; false — чанк не загружен или метка уже есть.</summary>
    bool AddBlock(byte kind, long cellX, long cellY);

    /// <summary>Снять метку; false — метки нет или чанк не загружен.</summary>
    bool RemoveBlock(byte kind, long cellX, long cellY);

    /// <summary>Есть ли метка указанного вида в клетке (только для загруженных чанков).</summary>
    bool HasBlock(byte kind, long cellX, long cellY);

    /// <summary>Число загруженных в память меток данного вида.</summary>
    int BlockCount(byte kind);
}