using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Bath;

/// <summary>A bath (owner, 2026-10-24): when it was, nothing more; a note says anything else.</summary>
public sealed record BathDetails : EntryDetails;

/// <summary>The <c>bath</c> kind: a point in time. Two baths within two hours are the same one, logged twice.</summary>
public sealed class BathKind : EntryKind<BathDetails>
{
    public static readonly BathKind Instance = new();

    private BathKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("bath");

    public override SubjectType Subject => SubjectType.Baby;

    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(TimeSpan.FromHours(2));

    protected override TimingRule TimingFor(BathDetails details) => TimingRule.PointOnly;

    protected override Result Validate(BathDetails details, EntryTiming timing) => Result.Success;
}
