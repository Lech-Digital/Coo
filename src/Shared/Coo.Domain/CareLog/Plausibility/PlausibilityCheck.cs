namespace Coo.Domain.CareLog.Plausibility;

/// <summary>Runs the common checks, then the entry's kind's own (Spec 001 §8).</summary>
public static class PlausibilityCheck
{
    public static IReadOnlyList<Warning> For(CareEntry entry, PlausibilityContext context)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return For(entry.Kind, entry.Subject, entry.Details, entry.Timing, context);
    }

    /// <summary>For proposals that are not entries yet, such as AI results before they are saved.</summary>
    public static IReadOnlyList<Warning> For(
        IEntryKind kind, EntrySubject subject, EntryDetails details, EntryTiming timing, PlausibilityContext context)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(context);

        var warnings = new List<Warning>();

        if (timing.Instants.Any(i => i - context.Now > PlausibilityThresholds.InFutureAfter))
            warnings.Add(PlausibilityCodes.InFuture);

        if (subject is BabySubject && context.BabyBirthDate is { } birth
            && timing.Instants.Any(i => DateOnly.FromDateTime(i.DateTime) < birth))
            warnings.Add(PlausibilityCodes.BeforeBirth);

        if (context.CapturedAt is { } captured && captured - timing.OccurredAt > PlausibilityThresholds.LongAgoBeforeCapture)
            warnings.Add(PlausibilityCodes.LongAgo);

        if (context.TimeWasUnspecified)
            warnings.Add(PlausibilityCodes.TimeNotStated);

        if (timing is SessionTiming { Range.IsOpen: true } session && kind.RunningLongAfter is { } limit
            && session.Range.Duration(context.Now) > limit)
            warnings.Add(PlausibilityCodes.RunningLong);

        warnings.AddRange(kind.Plausibility(details, timing, context));
        return warnings;
    }
}
