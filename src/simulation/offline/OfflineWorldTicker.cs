using System;
using Game.Core;
using Game.Simulation;

namespace Game.Simulation.Offline;

/// <summary>
/// Compact offline-only agent snapshot. The current AgentDataPool has no health,
/// energy, stable entity-id, or world-coordinate fields; this DTO therefore carries
/// the caller-provided identity/coordinates and models its actual needs fields.
/// Fatigue is the existing 0=rested, 100=exhausted value (not a separate energy stat).
/// </summary>
public readonly record struct OfflineAgentState(
    ulong EntityId,
    long WorldX,
    long WorldY,
    OfflineAgentActivity Activity,
    float Hunger,
    float Sleep,
    float Fatigue,
    float EnvironmentSatisfaction,
    float Mood,
    double LastUpdateGameTime,
    double CatchUpElapsedGameSeconds);

/// <summary>Compact offline-only crop snapshot, including the sub-stage timer.</summary>
public readonly record struct OfflineCropState(
    ulong EntityId,
    long WorldX,
    long WorldY,
    int Stage,
    double GrowthTimer,
    double LastUpdateGameTime,
    bool HarvestQueued,
    double CatchUpElapsedGameSeconds);

/// <summary>
/// Pure approximate time advancement for entities outside the active simulation.
/// This class does not simulate positions, jobs, combat, enemies, or call Godot APIs.
/// </summary>
public static class OfflineWorldTicker
{
    /// <summary>Clamp a requested offline interval to the shared seven-day policy.</summary>
    public static double ClampElapsed(double elapsedGameSeconds)
        => OfflineCatchUpPolicy.ClampElapsed(elapsedGameSeconds);

    /// <summary>
    /// Advance needs with the real AgentNeedsConfig static rates. Since that config
    /// is a static class (not an instance/config object), no cfg parameter is used.
    /// Accumulated capped time travels with the snapshot so multiple advances share
    /// one cap and compose: Advance(Advance(s,t1),t2) matches Advance(s,t1+t2).
    /// Start a new offline checkpoint with CatchUpElapsedGameSeconds = 0.
    /// </summary>
    public static OfflineAgentState AdvanceAgent(OfflineAgentState from, double elapsedGameSeconds)
    {
        double previousApplied = NormalizeApplied(from.CatchUpElapsedGameSeconds);
        double elapsed = GetAvailableElapsed(elapsedGameSeconds, previousApplied);
        if (elapsed <= 0d)
            return from;

        var currentNeeds = new AgentNeedsSnapshot(
            from.Hunger, from.Sleep, from.Fatigue, from.EnvironmentSatisfaction, from.Mood);
        AgentNeedsSnapshot advanced = OfflineAgentNeeds.Advance(
            currentNeeds, from.Activity, elapsed, maximumElapsedGameSeconds: elapsed);

        float sleep = advanced.Sleep;
        if (from.Activity == OfflineAgentActivity.Resting)
        {
            // The active loop performs baseline sleep accrual then rest recovery per
            // substep. Use its net rate here so bounded offline advances remain
            // associative even when a large interval would otherwise clamp twice.
            sleep = ClampNeed(from.Sleep + (float)(
                (AgentNeedsConfig.SleepPerGameSec - AgentNeedsConfig.RestSleepRecoveryPerGameSec) * elapsed));
        }

        float mood = CalculateMood(advanced.Hunger, sleep, advanced.Fatigue, advanced.EnvironmentSatisfaction);
        return new OfflineAgentState(
            from.EntityId,
            from.WorldX,
            from.WorldY,
            from.Activity,
            advanced.Hunger,
            sleep,
            advanced.Fatigue,
            advanced.EnvironmentSatisfaction,
            mood,
            from.LastUpdateGameTime + elapsed,
            previousApplied + elapsed);
    }

    /// <summary>
    /// Advance crop stages with the existing constant-time growth helper. The crop
    /// timer is retained between calls; after maturity it is normalized to zero so
    /// ripe crops are idempotent and further elapsed time cannot alter their state.
    /// </summary>
    public static OfflineCropState AdvanceCrop(OfflineCropState from, double elapsedGameSeconds)
    {
        double previousApplied = NormalizeApplied(from.CatchUpElapsedGameSeconds);
        double elapsed = GetAvailableElapsed(elapsedGameSeconds, previousApplied);
        if (elapsed <= 0d)
            return from;

        CropGrowthSnapshot advanced = OfflineCropGrowth.Advance(
            new CropGrowthSnapshot(from.Stage, from.GrowthTimer, from.HarvestQueued),
            elapsed,
            maximumElapsedGameSeconds: elapsed);

        double growthTimer = advanced.Stage >= OfflineCropGrowth.MatureStage ? 0d : advanced.GrowthTimer;
        return new OfflineCropState(
            from.EntityId,
            from.WorldX,
            from.WorldY,
            advanced.Stage,
            growthTimer,
            from.LastUpdateGameTime + elapsed,
            advanced.IsHarvestQueued,
            previousApplied + elapsed);
    }

    private static double GetAvailableElapsed(double requestedElapsed, double previousApplied)
    {
        double maximum = OfflineCatchUpPolicy.DefaultMaximumElapsedGameSeconds;
        double remaining = Math.Max(0d, maximum - previousApplied);
        return Math.Min(OfflineCatchUpPolicy.ClampElapsed(requestedElapsed), remaining);
    }

    private static double NormalizeApplied(double value)
    {
        double maximum = OfflineCatchUpPolicy.DefaultMaximumElapsedGameSeconds;
        if (!double.IsFinite(value) || value <= 0d)
            return value > 0d ? maximum : 0d;
        return Math.Min(value, maximum);
    }

    private static float CalculateMood(float hunger, float sleep, float fatigue, float environment)
    {
        float penalty =
            hunger * AgentNeedsConfig.MoodWeightHunger +
            sleep * AgentNeedsConfig.MoodWeightSleep +
            fatigue * AgentNeedsConfig.MoodWeightFatigue +
            (100f - environment) * AgentNeedsConfig.MoodWeightEnvironment;
        return Math.Clamp(100f - penalty, 0f, 100f);
    }

    private static float ClampNeed(float value)
    {
        if (float.IsNaN(value))
            return 0f;
        return Math.Clamp(value, 0f, 100f);
    }
}
