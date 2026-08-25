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

    public override void _Ready()
    {
        Instance = this;
    }

    public void SetSpeed(GameSpeed speed)
    {
        CurrentSpeed = speed;
        OnSpeedChanged?.Invoke(CurrentSpeed);
    }
}
