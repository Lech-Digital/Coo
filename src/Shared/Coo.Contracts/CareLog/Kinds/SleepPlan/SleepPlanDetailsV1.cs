using System.Text.Json.Serialization;

namespace Coo.Contracts.CareLog.Kinds.SleepPlan;

/// <summary>
/// <c>sleepplan</c> v1 (Spec 019 §7): the goals and the approach as kebab-case codes, the wanted wake-up
/// (<c>"07:00"</c>), the accepted changes, and the day the plan stopped.
/// </summary>
public sealed record SleepPlanDetailsV1
{
    [JsonPropertyName("v")]
    public required int V { get; init; }

    /// <summary><c>"fall-asleep-easier"</c>, <c>"fewer-night-wakings"</c>, <c>"longer-naps"</c>, <c>"earlier-bedtime"</c>, <c>"later-morning"</c>, <c>"predictable-day"</c>, <c>"drop-a-nap"</c>.</summary>
    public required IReadOnlyList<string> Goals { get; init; }

    /// <summary><c>"rhythm"</c>, <c>"fading"</c>, <c>"chair"</c>, <c>"check-and-console"</c> or <c>"no-crying"</c>.</summary>
    public required string Approach { get; init; }

    /// <summary><c>"HH:mm"</c>.</summary>
    public string? WakeUp { get; init; }

    public IReadOnlyList<SleepPlanAdjustmentV1>? Adjustments { get; init; }

    /// <summary><c>"yyyy-MM-dd"</c>.</summary>
    public string? StoppedOn { get; init; }
}

/// <param name="Kind"><c>"new-age-schedule"</c>, <c>"earlier-bedtime"</c>, <c>"longer-windows"</c>, <c>"shorter-last-nap"</c>, <c>"drop-a-nap"</c> or <c>"keep-going"</c>.</param>
public sealed record SleepPlanAdjustmentV1(string On, string Kind, int Minutes);
