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

    // Work-зоны: оранжевая гамма чтобы отличать от Farm.
    private static readonly Color WorkBorderColor = new(1f, 0.65f, 0.2f, 0.85f);
    private static readonly Color WorkSelectedBorderColor = new(1f, 0.75f, 0.3f, 0.95f);
    private static readonly Color WorkSelectedFillColor = new(1f, 0.65f, 0.2f, 0.18f);

    private const float RedrawThrottleSec = 0.1f;
    private float _redrawTimer = 1f;
    private bool _redrawPending;

    private readonly List<(Vector2 From, Vector2 To)> _borderBuffer = new(512);

    public override void _Ready()
    {
        ZIndex = 8; // Поверх тайлов и грядок, под агентами
        ZoneManager.Instance.OnZonesUpdated += OnZonesUpdatedThrottled;
    }

    public override void _Process(double delta)
    {
        if (!_redrawPending) return;
        _redrawTimer += (float)delta;
        if (_redrawTimer < RedrawThrottleSec) return;
        _redrawTimer = 0f;
        _redrawPending = false;
        QueueRedraw();
    }

    private void OnZonesUpdatedThrottled()
    {
        _redrawPending = true;
    }

    public override void _Draw()
    {
        // Единый рендер всех зон: Farm — зелёные оттенки, Work — оранжевые.
        var zones = ZoneManager.Instance.GetAllZones();
        var hovered = ZoneManager.Instance.HoveredZone;
        var selected = ZoneManager.Instance.SelectedZone;

        foreach (var zone in zones)
        {
            bool isSelected = selected != null && selected.Id == zone.Id;
            bool isHovered = hovered != null && hovered.Id == zone.Id;
            bool isWork = zone.Kind == ZoneKind.Work;

            Color selFill = isWork ? WorkSelectedFillColor : SelectedFillColor;

            // Заливка при наведении или выборе
            if (isSelected)
            {
                foreach (var (x, y) in zone.Tiles)
                {
                    DrawRect(new Rect2(x * TileSize, y * TileSize, TileSize, TileSize), selFill);
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
            Color borderColor;
            if (isSelected)
                borderColor = isWork ? WorkSelectedBorderColor : SelectedBorderColor;
            else if (isHovered)
                borderColor = HoverBorderColor;
            else
                borderColor = isWork ? WorkBorderColor : BaseBorderColor;
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
        ZoneManager.Instance.OnZonesUpdated -= OnZonesUpdatedThrottled;
        base._ExitTree();
    }
}