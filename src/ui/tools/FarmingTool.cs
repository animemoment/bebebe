using Godot;
using Game.Core;
using Game.Simulation;
using System.Collections.Generic;

namespace Game.UI.Tools;

public class FarmingTool : ITool
{
    private readonly SelectionBox _selectionBox;
    private readonly MapData _mapData;
    private readonly WallBuildManager _wallBuildManager;
    private bool _isDragging = false;
    private bool _isLeftClick = true;

    private static readonly Color FarmZoneFill = new(0.4f, 0.6f, 0.2f, 0.35f);
    private static readonly Color FarmZoneBorder = new(0.5f, 0.8f, 0.2f, 0.9f);

    private readonly List<(int X, int Y)> _cellBuffer = new(2048);

    public FarmingTool(SelectionBox selectionBox, MapData mapData, WallBuildManager wallBuildManager)
    {
        _selectionBox = selectionBox;
        _mapData = mapData;
        _wallBuildManager = wallBuildManager;
    }

    public void OnHover(Vector2I tilePos, Vector2 worldPos) { }

    public void OnClick(Vector2I tilePos, Vector2 worldPos, bool isLeftClick)
    {
        _isDragging = true;
        _isLeftClick = isLeftClick;

        if (_selectionBox != null)
        {
            _selectionBox.SetStyle(FarmZoneFill, FarmZoneBorder);
            _selectionBox.StartSelection(worldPos);
        }
    }

    public void OnDrag(Vector2I startTile, Vector2I currentTile, Vector2 currentWorldPos)
    {
        if (_isDragging && _selectionBox != null)
        {
            _selectionBox.UpdateSelection(currentWorldPos);
        }
    }

    public IToolWorldPlacement WorldPlacement { get; set; }

    public void OnRelease(Vector2I startTile, Vector2I endTile, Vector2 worldPos, bool isLeftClick)
    {
        if (!_isDragging) return;
        _isDragging = false;

        _selectionBox?.EndSelection();
        _selectionBox?.ResetDefaultStyle();

        int minX = Mathf.Min(startTile.X, endTile.X);
        int maxX = Mathf.Max(startTile.X, endTile.X);
        int minY = Mathf.Min(startTile.Y, endTile.Y);
        int maxY = Mathf.Max(startTile.Y, endTile.Y);

        long cellCount = (long)(maxX - minX + 1) * (maxY - minY + 1);
        if (cellCount > 250_000) return;

        _cellBuffer.Clear();

        if (isLeftClick)
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (CanDesignateFarmPlot(x, y))
                    {
                        if (WorldPlacementPass.IsIslandCell(x, y))
                        {
                            _cellBuffer.Add((x, y));
                        }
                        else if (WorldPlacement != null)
                        {
                            WorldPlacement.TryPlaceFarmPlot(x, y);
                        }
                    }
                }
            }

            if (_cellBuffer.Count > 0)
            {
                FarmJobManager.Instance.MarkPlotsBatch(_cellBuffer, _mapData?.TreeOnGrass, _mapData?.StoneOnGrass);
                FarmZoneManager.Instance.CreateZone(_cellBuffer);
            }
        }
        else
        {
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (WorldPlacementPass.IsIslandCell(x, y))
                    {
                        _cellBuffer.Add((x, y));
                    }
                    else if (WorldPlacement != null)
                    {
                        WorldPlacement.TryRemoveFarmPlot(x, y);
                    }
                }
            }

            if (_cellBuffer.Count > 0)
            {
                FarmJobManager.Instance.UnmarkPlotsBatch(_cellBuffer);
                FarmZoneManager.Instance.RemoveTiles(_cellBuffer);
            }
        }
    }

    public void Cancel()
    {
        _isDragging = false;
        _selectionBox?.CancelSelection();
        _selectionBox?.ResetDefaultStyle();
    }

    private bool CanDesignateFarmPlot(int x, int y)
    {
        if (WorldPlacementPass.IsIslandCell(x, y))
        {
            // Терраин-гейт строгий и fail-closed. Раньше он был обёрнут в
            // `if (_mapData != null && координата в окне карты)`: при отсутствии карты (или
            // координате вне окна) проверка МОЛЧА пропускалась, и чертёж грядки вставал на воду,
            // гору или песок — «чертёж на рандом». Без карты острова клетку не берём вообще.
            if (_mapData == null)
                return false;
            if ((uint)x >= (uint)_mapData.Width || (uint)y >= (uint)_mapData.Height)
                return false;
            if (_mapData.Ground[x, y] != TileType.Grass)
                return false;
            // Одного Ground мало: песок (пустыня и прибрежный пляж) имеет Ground == Grass,
            // но рисуется песком (MapRenderer строит terrain_map: Biome == Desert → Sand).
            if (_mapData.GetBiome(x, y) == Game.Core.WorldStreaming.BiomeType.Desert)
                return false;

            if (_wallBuildManager != null && _wallBuildManager.IsWallAt(x, y))
                return false;

            if (StructureManager.Instance.HasStructureAt(x, y))
                return false;

            if (BuildingManager.Instance.HasBuildingAt(x, y))
                return false;

            if (BlueprintManager.Instance.IsBlueprintAt(x, y))
                return false;

            if (FarmJobManager.Instance.IsGardenBed(x, y) || FarmJobManager.Instance.IsPlotMarked(x, y))
                return false;

            return true;
        }

        // Вне острова колонии: проверяем через WorldPlacement
        if (WorldPlacement != null)
        {
            return WorldPlacement.CanPlaceFarm(x, y);
        }

        return false;
    }
}