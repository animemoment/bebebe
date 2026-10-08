using System;
using Game.Core;

namespace Game.Simulation.Offline;

/// <summary>Off-screen activity class; deliberately contains no movement/job simulation.</summary>
public enum OfflineAgentActivity : byte
{
    Working,
    Idle,
    Resting,
    Other
}

/// <summary>
/// Minimal immutable needs DTO for approximate offline progression.
/// It is not a serialized AgentDataPool record.
/// </summary>
public readonly record struct AgentNeedsSnapshot(
    float Hunger,
    float Sleep,
    float Fatigue,
    float EnvironmentSatisfaction,
    float Mood);

/// <summary>Pure arithmetic for needs progression outside the active simulation.</summary>
public static class OfflineAgentNeeds
{
    /// <summary>
    /// Advances needs using the existing AgentNeedsConfig rates and mood weights.
    /// Hunger and sleep accumulate for all activities. Fatigue follows current
    /// simulation semantics: work adds fatigue, idle recovers at the idle rate,
    /// resting only applies rest recovery, and other states leave fatigue unchanged.
    /// Resting applies baseline sleep accumulation first and then rest recovery,
    /// matching UpdateNeedsSingle followed by NeedsJobSystem.CommitResting.
    /// </summary>
    public static AgentNeedsSnapshot Advance(
        in AgentNeedsSnapshot current,
        OfflineAgentActivity activity,
        double elapsedGameSeconds,
        double maximumElapsedGameSeconds = OfflineCatchUpPolicy.DefaultMaximumElapsedGameSeconds)
    {
        double elapsed = OfflineCatchUpPolicy.ClampElapsed(elapsedGameSeconds, maximumElapsedGameSeconds);
        if (elapsed <= 0d)
            return current;

        float hunger = ClampNeed(current.Hunger + (float)(AgentNeedsConfig.HungerPerGameSec * elapsed));
        float sleep = ClampNeed(current.Sleep + (float)(AgentNeedsConfig.SleepPerGameSec * elapsed));
        float fatigue = ClampNeed(current.Fatigue);

        switch (activity)
        {
            case OfflineAgentActivity.Working:
                fatigue = ClampNeed(fatigue + (float)(AgentNeedsConfig.FatigueWorkPerGameSec * elapsed));
                break;
            case OfflineAgentActivity.Idle:
                fatigue = ClampNeed(fatigue - (float)(AgentNeedsConfig.FatigueIdleRecoveryPerGameSec * elapsed));
                break;
            case OfflineAgentActivity.Resting:
                // Current runtime order: baseline needs update, then CommitResting.
                sleep = ClampNeed(sleep - (float)(AgentNeedsConfig.RestSleepRecoveryPerGameSec * elapsed));
                fatigue = ClampNeed(fatigue - (float)(AgentNeedsConfig.RestFatigueRecoveryPerGameSec * elapsed));
                break;
            case OfflineAgentActivity.Other:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(activity), activity, "Unknown offline activity.");
        }

        float environment = ClampNeed(current.EnvironmentSatisfaction);
        float penalty =
            hunger * AgentNeedsConfig.MoodWeightHunger +
            sleep * AgentNeedsConfig.MoodWeightSleep +
            fatigue * AgentNeedsConfig.MoodWeightFatigue +
            (100f - environment) * AgentNeedsConfig.MoodWeightEnvironment;
        float mood = Math.Clamp(100f - penalty, 0f, 100f);
        return new AgentNeedsSnapshot(hunger, sleep, fatigue, environment, mood);
    }

    private static float ClampNeed(float value)
    {
        // Keep malformed persisted values from propagating NaN through later updates.
        if (float.IsNaN(value))
            return 0f;
        return Math.Clamp(value, 0f, 100f);
    }
}
