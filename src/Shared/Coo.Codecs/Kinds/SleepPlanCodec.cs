using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Coo.Contracts.CareLog.Kinds.SleepPlan;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.SleepPlan;
using Coo.Domain.Insights;

namespace Coo.Codecs.Kinds;

/// <summary><c>sleepplan</c> (Spec 019 §7): enums as kebab-case codes, times as <c>HH:mm</c>, days as <c>yyyy-MM-dd</c>.</summary>
internal sealed class SleepPlanCodec : EntryDetailsCodec<SleepPlanDetails, SleepPlanDetailsV1>
{
    public static readonly SleepPlanCodec Instance = new();

    private static readonly Dictionary<SleepPlanGoal, string> GoalCodes = new()
    {
        [SleepPlanGoal.FallAsleepEasier] = "fall-asleep-easier",
        [SleepPlanGoal.FewerNightWakings] = "fewer-night-wakings",
        [SleepPlanGoal.LongerNaps] = "longer-naps",
        [SleepPlanGoal.EarlierBedtime] = "earlier-bedtime",
        [SleepPlanGoal.LaterMorning] = "later-morning",
        [SleepPlanGoal.PredictableDay] = "predictable-day",
        [SleepPlanGoal.DropANap] = "drop-a-nap",
    };

    private static readonly Dictionary<SettlingApproach, string> ApproachCodes = new()
    {
        [SettlingApproach.Rhythm] = "rhythm",
        [SettlingApproach.Fading] = "fading",
        [SettlingApproach.Chair] = "chair",
        [SettlingApproach.CheckAndConsole] = "check-and-console",
        [SettlingApproach.NoCrying] = "no-crying",
    };

    private static readonly Dictionary<PlanAdjustmentKind, string> AdjustmentCodes = new()
    {
        [PlanAdjustmentKind.KeepGoing] = "keep-going",
        [PlanAdjustmentKind.NewAgeSchedule] = "new-age-schedule",
        [PlanAdjustmentKind.EarlierBedtime] = "earlier-bedtime",
        [PlanAdjustmentKind.LongerWindows] = "longer-windows",
        [PlanAdjustmentKind.ShorterLastNap] = "shorter-last-nap",
        [PlanAdjustmentKind.DropANap] = "drop-a-nap",
    };

    private SleepPlanCodec()
    {
    }

    public override EntryKindCode Kind => SleepPlanKind.Instance.Code;

    public override int CurrentVersion => 1;

    protected override JsonTypeInfo<SleepPlanDetailsV1> TypeInfo => CodecJson.Context.SleepPlanDetailsV1;

    protected override SleepPlanDetailsV1 ToDto(SleepPlanDetails details) => new()
    {
        V = 1,
        Goals = [.. details.Goals.Select(g => GoalCodes[g])],
        Approach = ApproachCodes[details.Approach],
        WakeUp = details.WakeUp?.ToString("HH:mm", CultureInfo.InvariantCulture),
        Adjustments = details.Adjustments.Count == 0
            ? null
            : [.. details.Adjustments.Select(a => new SleepPlanAdjustmentV1(a.On.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), AdjustmentCodes[a.Kind], a.Minutes))],
        StoppedOn = details.StoppedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    protected override DecodeOutcome FromDto(SleepPlanDetailsV1 dto)
    {
        var goals = dto.Goals.Select(c => Find(GoalCodes, c)).ToList();
        if (goals.Any(g => g is null))
            return new Malformed(DecodeReasons.Invalid("goals"));
        if (Find(ApproachCodes, dto.Approach) is not { } approach)
            return new Malformed(DecodeReasons.Invalid("approach"));

        TimeOnly? wakeUp = null;
        if (dto.WakeUp is { } w)
        {
            if (!TimeOnly.TryParseExact(w, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return new Malformed(DecodeReasons.Invalid("wakeUp"));
            wakeUp = parsed;
        }

        DateOnly? stopped = null;
        if (dto.StoppedOn is { } s)
        {
            if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return new Malformed(DecodeReasons.Invalid("stoppedOn"));
            stopped = day;
        }

        var adjustments = new List<AcceptedAdjustment>();
        foreach (var a in dto.Adjustments ?? [])
        {
            if (!DateOnly.TryParseExact(a.On, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var on)
                || Find(AdjustmentCodes, a.Kind) is not { } kind)
                return new Malformed(DecodeReasons.Invalid("adjustments"));
            adjustments.Add(new AcceptedAdjustment(on, kind, a.Minutes));
        }

        var details = SleepPlanDetails.Create(goals.Select(g => g!.Value), approach, wakeUp, adjustments, stopped);
        return details.IsSuccess ? Ok(details.Value) : new Malformed(DecodeReasons.Invalid("goals"));
    }

    private static T? Find<T>(Dictionary<T, string> codes, string code)
        where T : struct => codes.FirstOrDefault(p => p.Value == code) is { Value: not null } hit ? hit.Key : null;
}
