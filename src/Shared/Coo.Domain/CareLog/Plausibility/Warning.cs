namespace Coo.Domain.CareLog.Plausibility;

/// <summary>Something unusual, flagged but never blocking a save (Spec 001 §8).</summary>
public sealed record Warning(string Code)
{
    public override string ToString() => Code;
}

/// <summary>What the plausibility checks may know besides the entry itself.</summary>
public sealed record PlausibilityContext(DateTimeOffset Now)
{
    /// <summary>For <c>warn.before-birth</c>; checks that need it are skipped without it.</summary>
    public DateOnly? BabyBirthDate { get; init; }

    /// <summary>When the words were captured, for AI proposals (ADR 0005 §3).</summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>The AI's time was <c>Unspecified</c> and resolved to <see cref="CapturedAt"/>.</summary>
    public bool TimeWasUnspecified { get; init; }
}
