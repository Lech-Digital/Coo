using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Diaper;

public enum DiaperContents
{
    Wet,
    Dirty,
    Mixed,
}

/// <summary>A diaper change (Spec 001 §7.3). Stool colour is a later additive field.</summary>
public sealed record DiaperDetails(DiaperContents Contents) : EntryDetails
{
    public Result<DiaperDetails> WithContents(DiaperContents contents) => this with { Contents = contents };
}

public static class DiaperThresholds
{
    public static readonly TimeSpan DuplicateTolerance = TimeSpan.FromMinutes(10);
}

/// <summary>The <c>diaper</c> kind.</summary>
public sealed class DiaperKind : EntryKind<DiaperDetails>
{
    public static readonly DiaperKind Instance = new();

    private DiaperKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("diaper");

    public override SubjectType Subject => SubjectType.Baby;

    /// <summary>Contents are not compared: one person's "wet" is another's "mixed".</summary>
    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(DiaperThresholds.DuplicateTolerance);

    protected override TimingRule TimingFor(DiaperDetails details) => TimingRule.PointOnly;

    protected override Result Validate(DiaperDetails details, EntryTiming timing) => Result.Success;
}
