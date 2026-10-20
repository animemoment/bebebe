using System.Collections.Generic;
using Game.Core.WorldStreaming.Integration;

namespace Game.UI.Streaming;

/// <summary>Состояние человека мира (§29).</summary>
public enum WorldAgentState
{
	/// <summary>Свободен: ищет задачу или уходит домой.</summary>
	Idle,

	/// <summary>Идёт к чертежу по найденному пути.</summary>
	Walking,

	/// <summary>Работает на клетке.</summary>
	Working,

	/// <summary>Переброска по морю: цель на суше, но отрезана водой (лодок пока нет).</summary>
	SeaCrossing
}

/// <summary>
/// Человек мира: позиция в клетках (центр клетки = +0.5), задача и путь к ней.
/// Вынесен из WorldJobTicker.cs: файловый граф анализатора зависимостей видел
/// ссылку рендерера на этот файл и держал цикл WorldAgentRenderer ↔ WorldJobTicker.
/// </summary>
public sealed class WorldAgent
{
	public float X;
	public float Y;
	public WorldAgentState State;
	public WorldPlan Job;
	public float WorkLeft;
	public float IdleTime;

	/// <summary>Найденный путь (клетки), обход воды и гор.</summary>
	public readonly List<(long X, long Y)> Path = new(256);

	/// <summary>Текущая точка пути.</summary>
	public int PathIndex;

	/// <summary>Путь ещё не считан (считается по бюджету — не каждый кадр).</summary>
	public bool NeedsPath;

	/// <summary>Остаток переброски по морю, сек.</summary>
	public float TravelLeft;
}