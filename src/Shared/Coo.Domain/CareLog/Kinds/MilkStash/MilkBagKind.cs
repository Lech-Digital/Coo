using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.MilkStash;

/// <summary>Where a bag of expressed milk is kept.</summary>
public enum MilkPlace
{
    Fridge,
    Freezer,
}

/// <summary>
/// What the milk is kept in (owner, 2026-11-05: "not bags but containers too"). Stored only when it is not a bag, so
/// every bag written before 1.11 reads as one.
/// </summary>
public enum MilkContainer
{
    Bag,
    Bottle,
    Container,
}

/// <summary>Whether a bag in stock is fine, should be used soon, or is past its use-by.</summary>
public enum MilkFreshness
{
    Fine,
    UseSoon,
    Expired,
}

/// <summary>
/// A bag (or bottle) of expressed milk in the family's stash: how much, where it is kept, when it was thawed, and when it
/// was used or thrown away. The entry's time is when the milk was expressed, which its use-by counts from.
/// </summary>
public sealed record MilkBagDetails : EntryDetails
{
    public const int MaxLabelLength = 40;

    public const int MaxNumber = 999;

    private MilkBagDetails(
        Volume volume, MilkPlace place, DateTimeOffset? thawedAt, DateOnly? usedOn, DateOnly? discardedOn, string? label, int? number,
        MilkContainer container) =>
        (Volume, Place, ThawedAt, UsedOn, DiscardedOn, Label, Number, Container) = (volume, place, thawedAt, usedOn, discardedOn, label, number, container);

    public Volume Volume { get; }

    public MilkPlace Place { get; }

    /// <summary>When it came out of the freezer; thawed milk stays in the fridge and is never frozen again.</summary>
    public DateTimeOffset? ThawedAt { get; }

    public DateOnly? UsedOn { get; }

    public DateOnly? DiscardedOn { get; }

    /// <summary>The parent's own note on the bag, such as "night milk".</summary>
    public string? Label { get; }

    /// <summary>The number written on the container, so three bags in the fridge can be told apart ("#2").</summary>
    public int? Number { get; }

    /// <summary>A bag, a bottle or another container.</summary>
    public MilkContainer Container { get; }

    public bool IsInStock => UsedOn is null && DiscardedOn is null;

    public static Result<MilkBagDetails> Create(
        Volume volume, MilkPlace place, DateTimeOffset? thawedAt = null, DateOnly? usedOn = null, DateOnly? discardedOn = null, string? label = null,
        int? number = null, MilkContainer container = MilkContainer.Bag)
    {
        if (volume.Millilitres <= 0 || volume.Millilitres > MilkStorage.MaxBagMl)
            return Result<MilkBagDetails>.Failure(MilkBagErrors.Volume);
        if (!Enum.IsDefined(place) || !Enum.IsDefined(container))
            return Result<MilkBagDetails>.Failure(MilkBagErrors.Unknown);
        if (place == MilkPlace.Freezer && thawedAt is not null)
            return Result<MilkBagDetails>.Failure(MilkBagErrors.Refreeze);
        if (usedOn is not null && discardedOn is not null)
            return Result<MilkBagDetails>.Failure(MilkBagErrors.UsedAndDiscarded);
        var trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (trimmed?.Length > MaxLabelLength)
            return Result<MilkBagDetails>.Failure(MilkBagErrors.LabelTooLong);
        if (number is < 1 or > MaxNumber)
            return Result<MilkBagDetails>.Failure(MilkBagErrors.Number);
        // To the millisecond, as stored: the codec's write gate compares the decoded value with this one.
        var thawed = thawedAt is { } t ? DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds()) : (DateTimeOffset?)null;
        return new MilkBagDetails(volume, place, thawed, usedOn, discardedOn, trimmed, number, container);
    }

    /// <summary>
    /// The number for a new bag: the lowest not on a bag still in stock, so the numbers on the containers stay short and a
    /// used bag's number comes free again.
    /// </summary>
    public static int NextNumber(IEnumerable<int?> inStock)
    {
        ArgumentNullException.ThrowIfNull(inStock);
        var taken = inStock.OfType<int>().ToHashSet();
        var next = 1;
        while (taken.Contains(next) && next < MaxNumber)
            next++;
        return next;
    }

    /// <summary>What the parent can correct: how much is left (part of it used), the number on the container, the note, and what it is in.</summary>
    public Result<MilkBagDetails> Edit(Volume volume, int? number, string? label, MilkContainer container) =>
        Create(volume, Place, ThawedAt, UsedOn, DiscardedOn, label, number, container);

    /// <summary>Into the freezer: only milk that was never thawed.</summary>
    public Result<MilkBagDetails> Freeze() => Create(Volume, MilkPlace.Freezer, ThawedAt, UsedOn, DiscardedOn, Label, Number, Container);

    /// <summary>Out of the freezer into the fridge, thawing from <paramref name="at"/>.</summary>
    public Result<MilkBagDetails> Thaw(DateTimeOffset at) =>
        Place == MilkPlace.Freezer
            ? Create(Volume, MilkPlace.Fridge, at, UsedOn, DiscardedOn, Label, Number, Container)
            : Result<MilkBagDetails>.Failure(MilkBagErrors.NotFrozen);

    public Result<MilkBagDetails> Use(DateOnly on) => Create(Volume, Place, ThawedAt, on, null, Label, Number, Container);

    public Result<MilkBagDetails> Discard(DateOnly on) => Create(Volume, Place, ThawedAt, null, on, Label, Number, Container);

    /// <summary>Back in stock: a "used" tapped by mistake.</summary>
    public Result<MilkBagDetails> Restock() => Create(Volume, Place, ThawedAt, null, null, Label, Number, Container);

    public bool Equals(MilkBagDetails? other) =>
        other is not null && Volume == other.Volume && Place == other.Place && ThawedAt == other.ThawedAt && UsedOn == other.UsedOn
        && DiscardedOn == other.DiscardedOn && Label == other.Label && Number == other.Number && Container == other.Container;

    public override int GetHashCode() => HashCode.Combine(Volume, Place, ThawedAt, UsedOn, DiscardedOn, Label, Number, Container);
}

/// <summary>
/// How long expressed milk keeps (CDC, "Proper Storage and Preparation of Breast Milk"): 4 days in the fridge, about 6
/// months in the freezer (best quality), and 24 hours in the fridge once thawed, never refrozen. Room temperature (4 hours)
/// is not a place in the stash.
/// </summary>
public static class MilkStorage
{
    public const decimal MaxBagMl = 1000m;

    public static readonly TimeSpan Fridge = TimeSpan.FromDays(4);

    public const int FreezerMonths = 6;

    public static readonly TimeSpan Thawed = TimeSpan.FromHours(24);

    /// <summary>How early "use soon" shows: a day for the fridge, two weeks for the freezer.</summary>
    public static readonly TimeSpan FridgeWarning = TimeSpan.FromDays(1);

    public static readonly TimeSpan FreezerWarning = TimeSpan.FromDays(14);

    /// <param name="expressedAt">The entry's time.</param>
    public static DateTimeOffset UseBy(MilkBagDetails bag, DateTimeOffset expressedAt)
    {
        ArgumentNullException.ThrowIfNull(bag);
        if (bag.ThawedAt is { } thawed)
        {
            // Thawed milk keeps a day, but not past the freezer's six months: thawing does not make old milk new.
            var fromThaw = thawed + Thawed;
            var frozenLimit = expressedAt.AddMonths(FreezerMonths);
            return frozenLimit < fromThaw ? frozenLimit : fromThaw;
        }

        return bag.Place == MilkPlace.Freezer ? expressedAt.AddMonths(FreezerMonths) : expressedAt + Fridge;
    }

    public static MilkFreshness Freshness(MilkBagDetails bag, DateTimeOffset expressedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bag);
        var useBy = UseBy(bag, expressedAt);
        if (now >= useBy)
            return MilkFreshness.Expired;
        var warning = bag.Place == MilkPlace.Freezer ? FreezerWarning : FridgeWarning;
        return now >= useBy - warning ? MilkFreshness.UseSoon : MilkFreshness.Fine;
    }
}

public static class MilkBagErrors
{
    public const string Volume = "milkbag.volume";
    public const string Unknown = "milkbag.unknown";
    public const string Refreeze = "milkbag.refreeze";
    public const string NotFrozen = "milkbag.not-frozen";
    public const string UsedAndDiscarded = "milkbag.used-and-discarded";
    public const string LabelTooLong = "milkbag.label-too-long";
    public const string Number = "milkbag.number";
}

/// <summary>
/// <c>milkbag</c>: a point, when the milk was expressed. Its subject is the family member who stocked it, like the
/// <c>pump</c> kind's: the stash is the family's, for whichever baby drinks it.
/// </summary>
public sealed class MilkBagKind : EntryKind<MilkBagDetails>
{
    public static readonly MilkBagKind Instance = new();

    private MilkBagKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("milkbag");

    public override SubjectType Subject => SubjectType.Caregiver;

    protected override TimingRule TimingFor(MilkBagDetails details) => TimingRule.PointOnly;

    protected override Result Validate(MilkBagDetails details, EntryTiming timing) => Result.Success;
}
