using Godot;
using Game.Simulation;
using System.Collections.Generic;

namespace Game.UI;

public partial class FarmZoneRenderer : Node2D
{
    private const float TileSize = 64f;
    private const float DefaultBorderThickness = 2.0f;
    private const float HighlightBorderThickness = 3.2f;

    private static readonly Color BaseBorderColor = new(1f, 1f, 1f, 0.40f);
    private static readonly Color HoverBorderColor = new(1f, 1f, 1f, 0.95f);
    private static readonly Color SelectedBorderColor = new(0.9f, 1.0f, 0.4f, 0.95f);

    private static readonly Color HoverFillColor = new(1f, 1f, 1f, 0.12f);
    private static readonly Color SelectedFillColor = new(0.9f, 1.0f, 0.4f, 0.18f);

    private readonly List<(Vector2 From, Vector2 To)> _borderBuffer = new(512);

    public override void _Ready()
    {
        ZIndex = 8; // Поверх тайлов и грядок, под агентами
        FarmZoneManager.Instance.OnZonesUpdated += QueueRedraw;
    }

    public override void _Draw()
    {
        var zones = FarmZoneManager.Instance.GetAllZones();
        var hovered = FarmZoneManager.Instance.HoveredZone;
        var selected = FarmZoneManager.Instance.SelectedZone;

        foreach (var zone in zones)
        {
            bool isSelected = selected != null && selected.Id == zone.Id;
            bool isHovered = hovered != null && hovered.Id == zone.Id;

            // Заливка при наведении или выборе
            if (isSelected)
            {
                foreach (var (x, y) in zone.Tiles)
                {
                    DrawRect(new Rect2(x * TileSize, y * TileSize, TileSize, TileSize), SelectedFillColor);
                }
            }
            else if (isHovered)
            {
                foreach (var (x, y) in zone.Tiles)
                {
                    DrawRect(new Rect2(x * TileSize, y * TileSize, TileSize, TileSize), HoverFillColor);
                }
            }

            // Отрисовка контурной рамки
            Color borderColor = isSelected ? SelectedBorderColor : (isHovered ? HoverBorderColor : BaseBorderColor);
            float thickness = (isSelected || isHovered) ? HighlightBorderThickness : DefaultBorderThickness;

            _borderBuffer.Clear();
            BuildZoneBorders(zone.Tiles, _borderBuffer);

            foreach (var (from, to) in _borderBuffer)
            {
                DrawLine(from, to, borderColor, thickness);
            }
        }
    }

    private static void BuildZoneBorders(HashSet<(int X, int Y)> tiles, List<(Vector2 From, Vector2 To)> lines)
    {
        foreach (var (x, y) in tiles)
        {
            float x0 = x * TileSize;
            float y0 = y * TileSize;
            float x1 = x0 + TileSize;
            float y1 = y0 + TileSize;

            if (!tiles.Contains((x, y - 1)))
                lines.Add((new Vector2(x0, y0), new Vector2(x1, y0))); // Верх

            if (!tiles.Contains((x, y + 1)))
                lines.Add((new Vector2(x0, y1), new Vector2(x1, y1))); // Низ

            if (!tiles.Contains((x - 1, y)))
                lines.Add((new Vector2(x0, y0), new Vector2(x0, y1))); // Лево

            if (!tiles.Contains((x + 1, y)))
                lines.Add((new Vector2(x1, y0), new Vector2(x1, y1))); // Право
        }
    }

    public override void _ExitTree()
    {
        FarmZoneManager.Instance.OnZonesUpdated -= QueueRedraw;
        base._ExitTree();
    }
}