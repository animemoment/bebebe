using System;
using Godot;

namespace Game.UI;

public partial class SelectionBox : Node2D
{
    public bool IsSelecting = false;
    public Vector2 StartPosition;
    public Vector2 EndPosition;

    public Color FillColor { get; set; } = new Color(0.2f, 0.5f, 1.0f, 0.3f);
    public Color BorderColor { get; set; } = new Color(0.2f, 0.5f, 1.0f, 0.8f);

    private static readonly Color DefaultFill = new Color(0.2f, 0.5f, 1.0f, 0.3f);
    private static readonly Color DefaultBorder = new Color(0.2f, 0.5f, 1.0f, 0.8f);

    public event Action<Rect2> SelectionCompleted;

    public override void _Ready()
    {
        // Рисуем рамку поверх карты, стен и агентов
        ZIndex = 100;
    }

    public void SetStyle(Color fill, Color border)
    {
        FillColor = fill;
        BorderColor = border;
    }

    public void ResetDefaultStyle()
    {
        FillColor = DefaultFill;
        BorderColor = DefaultBorder;
    }

    public void StartSelection(Vector2 worldPosition)
    {
        IsSelecting = true;
        StartPosition = worldPosition;
        EndPosition = worldPosition;
        QueueRedraw();
    }

    public void UpdateSelection(Vector2 worldPosition)
    {
        if (!IsSelecting)
            return;

        EndPosition = worldPosition;
        QueueRedraw();
    }

    public void EndSelection()
    {
        if (!IsSelecting)
            return;

        Rect2 selectionRect = new Rect2(
            new Vector2(
                Mathf.Min(StartPosition.X, EndPosition.X),
                Mathf.Min(StartPosition.Y, EndPosition.Y)
            ),
            new Vector2(
                Mathf.Abs(EndPosition.X - StartPosition.X),
                Mathf.Abs(EndPosition.Y - StartPosition.Y)
            )
        );

        SelectionCompleted?.Invoke(selectionRect);
        IsSelecting = false;
        QueueRedraw();
    }

    public void CancelSelection()
    {
        if (!IsSelecting)
            return;

        IsSelecting = false;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!IsSelecting)
            return;

        Rect2 rect = new Rect2(
            new Vector2(
                Mathf.Min(StartPosition.X, EndPosition.X),
                Mathf.Min(StartPosition.Y, EndPosition.Y)
            ),
            new Vector2(
                Mathf.Abs(EndPosition.X - StartPosition.X),
                Mathf.Abs(EndPosition.Y - StartPosition.Y)
            )
        );

        DrawRect(rect, FillColor);
        DrawRect(rect, BorderColor, false, 2.0f);
    }
}