using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Growth;

public static class GrowthErrors
{
    public const string Empty = "growth.empty";
    public const string WeightOutOfRange = "growth.weight-out-of-range";
    public const string LengthOutOfRange = "growth.length-out-of-range";
    public const string HeadOutOfRange = "growth.head-out-of-range";
}

/// <summary>
/// A measurement: weight, length and head circumference, any of them (a home weigh-in has only weight). Stored in
/// grams and millimetres; shown in the household's units. The ranges reject typos, not small or large babies.
/// </summary>
public sealed record GrowthDetails : EntryDetails
{
    public const int MinGrams = 300;
    public const int MaxGrams = 30_000;
    public const int MinLengthMm = 200;
    public const int MaxLengthMm = 1_300;
    public const int MinHeadMm = 200;
    public const int MaxHeadMm = 600;

    private GrowthDetails(int? weightGrams, int? lengthMm, int? headMm) =>
        (WeightGrams, LengthMm, HeadMm) = (weightGrams, lengthMm, headMm);

    public int? WeightGrams { get; }

    public int? LengthMm { get; }

    public int? HeadMm { get; }

    public static Result<GrowthDetails> Create(int? weightGrams, int? lengthMm, int? headMm)
    {
        if (weightGrams is null && lengthMm is null && headMm is null)
            return Result<GrowthDetails>.Failure(GrowthErrors.Empty);
        if (weightGrams is < MinGrams or > MaxGrams)
            return Result<GrowthDetails>.Failure(GrowthErrors.WeightOutOfRange);
        if (lengthMm is < MinLengthMm or > MaxLengthMm)
            return Result<GrowthDetails>.Failure(GrowthErrors.LengthOutOfRange);
        if (headMm is < MinHeadMm or > MaxHeadMm)
            return Result<GrowthDetails>.Failure(GrowthErrors.HeadOutOfRange);
        return new GrowthDetails(weightGrams, lengthMm, headMm);
    }
}

/// <summary>The <c>growth</c> kind: a point in time, about the baby.</summary>
public sealed class GrowthKind : EntryKind<GrowthDetails>
{
    public static readonly GrowthKind Instance = new();

    private GrowthKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("growth");

    public override SubjectType Subject => SubjectType.Baby;

    /// <summary>Both parents typing in the same check-up's numbers.</summary>
    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(TimeSpan.FromHours(1));

    protected override TimingRule TimingFor(GrowthDetails details) => TimingRule.PointOnly;

    protected override Result Validate(GrowthDetails details, EntryTiming timing) => Result.Success;
}
