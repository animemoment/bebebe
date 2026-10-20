using System;
using Godot;

namespace Game.Simulation;

public partial class TimeManager : Node
{
	public static TimeManager Instance { get; private set; }

	public GameSpeed CurrentSpeed { get; private set; } = GameSpeed.Normal;
	public float SpeedMultiplier => (float)CurrentSpeed;
	public bool IsPaused => CurrentSpeed == GameSpeed.Paused;

	public event Action<GameSpeed> OnSpeedChanged;

	private float _gameTimeSeconds = WorldTime.HoursToSeconds(AgentSimulationThread.InitialStartHour);

	/// <summary>Мировое игровое время (секунды). Авторитет — AgentSimulationThread.GameTimeSeconds.</summary>
	public float GameTimeSeconds => _gameTimeSeconds;

	/// <summary>Время суток в секундах [0, WorldTime.SecondsPerDay).</summary>
	public float TimeOfDaySeconds => WorldTime.TimeOfDaySeconds(_gameTimeSeconds);

	/// <summary>Номер текущего дня (с 1).</summary>
	public int DayNumber => WorldTime.DayNumber(_gameTimeSeconds);

	public override void _Ready()
	{
		Instance = this;
	}

	public void SetSpeed(GameSpeed speed)
	{
		// F71: границы режимов на графике — старая → новая.
		var old = CurrentSpeed;
		CurrentSpeed = speed;
		Game.Core.SimEvents.Mark("SPEED", $"{old}x → {speed}x");
		Game.Core.SimEvents.Count("speed.change");
		OnSpeedChanged?.Invoke(CurrentSpeed);
	}

	/// <summary>
	/// Синхронизация мирового времени из потока симуляции.
	/// Вызывается из главного потока (Main._Process) — volatile float безопасен для чтения.
	/// </summary>
	public void SyncGameTime(float gameTimeSeconds)
	{
		_gameTimeSeconds = gameTimeSeconds;
	}
}
