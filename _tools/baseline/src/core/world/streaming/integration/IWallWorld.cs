using Game.Core.WorldStreaming;

namespace Game.Core.WorldStreaming.Integration;

/// <summary>
/// Минимальный контракт «мира со стенами», который потребляет StreamingWorldManager.
/// Реализуется StreamingWorldView (рендер+персистентность чанков); менеджеры главной
/// игры общаются через этот интерфейс, не завися от конкретного view.
/// </summary>
public interface IWallWorld
{
    /// <summary>Загружен ли чанк (данные и спрайт готовы).</summary>
    bool IsLoaded(ChunkKey key);

    /// <summary>Поставить персистентную стену; false, если чанк не загружен или уже стоит.</summary>
    bool AddWall(long cellX, long cellY);

    /// <summary>Снять персистентную стену; false, если её нет.</summary>
    bool RemoveWall(long cellX, long cellY);

    /// <summary>Есть ли стена в клетке (по загруженным данным).</summary>
    bool HasWall(long cellX, long cellY);

    /// <summary>Общее число постоянных стен по загруженной области.</summary>
    int WallCount { get; }
}