using System;

namespace Game.Simulation.Offline;

/// <summary>Minimal crop-growth DTO; coordinates and farming-manager state are intentionally absent.</summary>
public readonly record struct CropGrowthSnapshot(int Stage, double GrowthTimer, bool IsHarvestQueued);

/// <summary>Constant-time crop stage advancement without per-second simulation.</summary>
public static class OfflineCropGrowth
{
    // Mirrors CropGrowthManager's private StageDuration and stage range; keep aligned if that manager changes.
    public const double StageDurationGameSeconds = 60d;
    public const int FirstStage = 1;
    public const int MatureStage = 4;

    /// <summary>
    /// Advances a growing crop by calculating the number of crossed stages arithmetically.
    /// This does not harvest crops, emit jobs, or touch manager state.
    /// </summary>
    public static CropGrowthSnapshot Advance(
        in CropGrowthSnapshot current,
        double elapsedGameSeconds,
        double maximumElapsedGameSeconds = OfflineCatchUpPolicy.DefaultMaximumElapsedGameSeconds)
    {
        // Empty and already mature crops are inert in CropGrowthManager.UpdateGrowth.
        if (current.Stage < FirstStage || current.Stage >= MatureStage)
            return current;

        double elapsed = OfflineCatchUpPolicy.ClampElapsed(elapsedGameSeconds, maximumElapsedGameSeconds);
        if (elapsed <= 0d)
            return current;

        double timer = current.GrowthTimer;
        if (!double.IsFinite(timer) || timer < 0d)
            timer = 0d;

        double accumulated = timer + elapsed;
        int maxTransitions = MatureStage - current.Stage;
        double wholeTransitions = Math.Floor(accumulated / StageDurationGameSeconds);
        int transitions = wholeTransitions >= maxTransitions ? maxTransitions : (int)wholeTransitions;
        int stage = current.Stage + transitions;

        // Match the manager's while loop: subtract only transitions possible before
        // reaching stage 4, so a large elapsed interval remains as ripe-crop residual.
        double residual = accumulated - transitions * StageDurationGameSeconds;
        if (residual < 0d)
            residual = 0d;
        if (!double.IsFinite(residual))
            residual = double.MaxValue;

        bool harvestQueued = current.IsHarvestQueued || stage == MatureStage;
        return new CropGrowthSnapshot(stage, residual, harvestQueued);
    }
}
