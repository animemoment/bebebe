using System;
using Game.Simulation;

namespace Game.Simulation.Offline;

/// <summary>Limits offline progression to a bounded amount of game time.</summary>
public static class OfflineCatchUpPolicy
{
    /// <summary>Seven in-world days; aligns with WorldTime's 12,000 game seconds/day.</summary>
    public const double DefaultMaximumElapsedGameSeconds = 7d * WorldTime.SecondsPerDay;

    /// <summary>
    /// Returns a finite elapsed duration in [0, maximumElapsedGameSeconds].
    /// Invalid caps are programmer errors; invalid/negative elapsed values become zero.
    /// </summary>
    public static double ClampElapsed(
        double elapsedGameSeconds,
        double maximumElapsedGameSeconds = DefaultMaximumElapsedGameSeconds)
    {
        if (!double.IsFinite(maximumElapsedGameSeconds) || maximumElapsedGameSeconds < 0d)
            throw new ArgumentOutOfRangeException(nameof(maximumElapsedGameSeconds), "Cap must be finite and non-negative.");

        if (double.IsNaN(elapsedGameSeconds) || elapsedGameSeconds <= 0d)
            return 0d;
        if (double.IsPositiveInfinity(elapsedGameSeconds))
            return maximumElapsedGameSeconds;
        return Math.Min(elapsedGameSeconds, maximumElapsedGameSeconds);
    }
}
