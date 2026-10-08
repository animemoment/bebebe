using System.Collections.Generic;
using Godot;

namespace Game.UI.Streaming;

/// <summary>
/// Рисует людей мира (§29): цветные точки в клетках (клетка = 64 px в локальных координатах
/// вида). Дочерний узел StreamingWorldView, чтобы координаты совпадали с миром при любом масштабе.
/// </summary>
public sealed partial class WorldAgentRenderer : Node2D
{
    /// <summary>Радиус человека, px.</summary>
    [Export] public float RadiusPx = 9f;

    /// <summary>Идёт к чертежу.</summary>
    [Export] public Color WalkingColor = new Color(0.35f, 0.75f, 1f);

    /// <summary>Работает.</summary>
    [Export] public Color WorkingColor = new Color(1f, 0.65f, 0.2f);

    /// <summary>Свободен.</summary>
    [Export] public Color IdleColor = new Color(0.8f, 0.8f, 0.85f);

    /// <summary>Цвет переброски по морю (цель на суше, но отрезана водой).</summary>
    [Export] public Color SeaCrossingColor = new Color(0.4f, 0.9f, 0.85f);

    private WorldJobTicker _ticker;

    public void Bind(WorldJobTicker ticker)
    {
        _ticker = ticker;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_ticker == null)
            return;
        IReadOnlyList<WorldAgent> agents = _ticker.Agents;
        for (int i = 0; i < agents.Count; i++)
        {
            WorldAgent agent = agents[i];
            Color color = agent.State switch
            {
                WorldAgentState.Working => WorkingColor,
                WorldAgentState.Walking => WalkingColor,
                WorldAgentState.SeaCrossing => SeaCrossingColor,
                _ => IdleColor
            };
            var center = new Vector2(agent.X * MapRenderer.TileSizePx, agent.Y * MapRenderer.TileSizePx);
            DrawCircle(center, RadiusPx, color);
            DrawArc(center, RadiusPx, 0f, Mathf.Tau, 20, new Color(0f, 0f, 0f, 0.55f), 2f, true);
        }
        // Перерисовку запрашивает WorldJobTicker (при изменении позиций/состояний), а не
        // сам _Draw: самоочередь крутила бы перерисовку каждый кадр без изменений.
    }
}
