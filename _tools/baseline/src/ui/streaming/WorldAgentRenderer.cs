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

	private IWorldAgentSource _ticker;

	public void Bind(IWorldAgentSource source)
	{
		_ticker = source;
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (_ticker == null) return;
		var agents = _ticker.Agents;
		if (agents == null || agents.Count == 0) return;

		for (int i = 0; i < agents.Count; i++)
		{
			var a = agents[i];
			Color c = a.State switch
			{
				WorldAgentState.Walking => WalkingColor,
				WorldAgentState.Working => WorkingColor,
				WorldAgentState.SeaCrossing => SeaCrossingColor,
				_ => IdleColor
			};
			DrawCircle(new Vector2(a.X * 64f, a.Y * 64f), RadiusPx, c);
		}
	}
}
