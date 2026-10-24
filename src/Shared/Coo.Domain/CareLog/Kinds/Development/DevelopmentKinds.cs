using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Development;

/// <summary>Tummy time (owner, 2026-10-24): a session, timed like a nap; how long is all there is.</summary>
public sealed record TummyDetails : EntryDetails;

/// <summary>How the baby took a new food: as parents tell it, and whether something looked like a reaction.</summary>
public enum SolidsReaction
{
    None,
    Liked,
    Disliked,

    /// <summary>A rash, vomiting, swelling: worth telling the doctor, and the Advisor sees it.</summary>
    Reaction,
}

/// <summary>Solid food: what it was ("carrot puree"), how much in the parent's words ("3 spoons"), and how it went.</summary>
public sealed record SolidsDetails : EntryDetails
{
    public const int MaxFoodLength = 80;
    public const int MaxAmountLength = 40;

    private SolidsDetails(string food, string? amount, SolidsReaction reaction) => (Food, Amount, Reaction) = (food, amount, reaction);

    public string Food { get; }

    public string? Amount { get; }

    public SolidsReaction Reaction { get; }

    public static Result<SolidsDetails> Create(string? food, string? amount, SolidsReaction reaction)
    {
        var name = food?.Trim() ?? "";
        if (name.Length == 0)
            return Result<SolidsDetails>.Failure(DevelopmentErrors.FoodRequired);
        var how = string.IsNullOrWhiteSpace(amount) ? null : amount.Trim();
        if (name.Length > MaxFoodLength || how?.Length > MaxAmountLength)
            return Result<SolidsDetails>.Failure(DevelopmentErrors.TextTooLong);
        return Enum.IsDefined(reaction) ? new SolidsDetails(name, how, reaction) : Result<SolidsDetails>.Failure(DevelopmentErrors.ReactionUnknown);
    }
}

/// <summary>
/// A milestone: one of <see cref="MilestoneDetails.Presets"/> ("first-smile"), or <see cref="MilestoneDetails.Custom"/>
/// with the parent's own title ("Clapped hands"). The entry's time is the day it happened; parents log these later.
/// </summary>
public sealed record MilestoneDetails : EntryDetails
{
    public const string Custom = "custom";
    public const int MaxTitleLength = 60;

    /// <summary>The usual first-year milestones, in roughly the order they come.</summary>
    public static IReadOnlyList<string> Presets { get; } =
    [
        "first-smile", "laughs", "holds-head", "rolls-over", "sleeps-through", "sits", "first-tooth", "crawls", "stands",
        "first-word", "first-steps",
    ];

    private MilestoneDetails(string code, string? title) => (Code, Title) = (code, title);

    public string Code { get; }

    /// <summary>The parent's words: required for <see cref="Custom"/>, and for a preset an optional own name.</summary>
    public string? Title { get; }

    public static Result<MilestoneDetails> Create(string? code, string? title)
    {
        var what = code?.Trim().ToLowerInvariant() ?? "";
        var named = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (named?.Length > MaxTitleLength)
            return Result<MilestoneDetails>.Failure(DevelopmentErrors.TextTooLong);
        if (what == Custom)
            return named is null ? Result<MilestoneDetails>.Failure(DevelopmentErrors.MilestoneTitleRequired) : new MilestoneDetails(Custom, named);
        return Presets.Contains(what) ? new MilestoneDetails(what, named) : Result<MilestoneDetails>.Failure(DevelopmentErrors.MilestoneUnknown);
    }
}

public static class DevelopmentErrors
{
    public const string FoodRequired = "solids.food-required";
    public const string TextTooLong = "solids.text-too-long";
    public const string ReactionUnknown = "solids.reaction-unknown";
    public const string MilestoneUnknown = "milestone.unknown";
    public const string MilestoneTitleRequired = "milestone.title-required";
    public const string TummyTooLong = "tummy.too-long";
}

/// <summary>The <c>tummy</c> kind: a session of at most two hours, for the baby.</summary>
public sealed class TummyKind : EntryKind<TummyDetails>
{
    public static readonly TummyKind Instance = new();
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(2);

    private TummyKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("tummy");

    public override SubjectType Subject => SubjectType.Baby;

    /// <summary>Tummy time is minutes, not an hour: a timer still running after one was forgotten.</summary>
    public override TimeSpan? RunningLongAfter => TimeSpan.FromHours(1);

    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(TimeSpan.FromMinutes(5));

    protected override TimingRule TimingFor(TummyDetails details) => TimingRule.SessionOnly;

    protected override Result Validate(TummyDetails details, EntryTiming timing) =>
        timing is SessionTiming { Range.End: { } end } session && end - session.Range.Start > MaxDuration
            ? Result.Failure(DevelopmentErrors.TummyTooLong)
            : Result.Success;
}

/// <summary>The <c>solids</c> kind: a point in time. The same food twice within half an hour is one meal, logged twice.</summary>
public sealed class SolidsKind : EntryKind<SolidsDetails>
{
    public static readonly SolidsKind Instance = new();

    private SolidsKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("solids");

    public override SubjectType Subject => SubjectType.Baby;

    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(TimeSpan.FromMinutes(30),
        (a, b) => string.Equals(((SolidsDetails)a).Food, ((SolidsDetails)b).Food, StringComparison.OrdinalIgnoreCase));

    protected override TimingRule TimingFor(SolidsDetails details) => TimingRule.PointOnly;

    protected override Result Validate(SolidsDetails details, EntryTiming timing) => Result.Success;
}

/// <summary>The <c>milestone</c> kind: a point in time. The same milestone within a day is one, logged twice.</summary>
public sealed class MilestoneKind : EntryKind<MilestoneDetails>
{
    public static readonly MilestoneKind Instance = new();

    private MilestoneKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("milestone");

    public override SubjectType Subject => SubjectType.Baby;

    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(TimeSpan.FromDays(1), (a, b) =>
        ((MilestoneDetails)a).Code == ((MilestoneDetails)b).Code
        && string.Equals(((MilestoneDetails)a).Title, ((MilestoneDetails)b).Title, StringComparison.OrdinalIgnoreCase));

    protected override TimingRule TimingFor(MilestoneDetails details) => TimingRule.PointOnly;

    protected override Result Validate(MilestoneDetails details, EntryTiming timing) => Result.Success;
}
