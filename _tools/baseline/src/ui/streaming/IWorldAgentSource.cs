using System.Collections.Generic;

namespace Game.UI.Streaming;

/// <summary>
/// Источник людей мира для рендера. Узкий контракт: рендереру нужен только список агентов,
/// поэтому <see cref="WorldAgentRenderer"/> не зависит от конкретного тикера.
/// Реализуется <see cref="WorldJobTicker"/>.
/// Разрывает цикл WorldAgentRenderer ↔ WorldJobTicker (DIP вместо прямой связи).
/// </summary>
public interface IWorldAgentSource
{
	/// <summary>Люди мира (для рендера). Коллекция read-only для потребителя.</summary>
	IReadOnlyList<WorldAgent> Agents { get; }
}