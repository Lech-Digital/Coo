using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Feeding;

/// <summary>What was in a bottle. An open-ended set: a new value is additive (ADR 0010 §3.3).</summary>
public enum Milk
{
    /// <summary>Pumped breast milk.</summary>
    ExpressedBreastMilk,
    Formula,
}

public sealed record BottleComponent(Milk Milk, Volume Volume);

/// <summary>
/// A feed from a bottle, with the amount of each milk, so reports can tell pumped milk from formula
/// (Spec 001 §7.2).
/// </summary>
public sealed record BottleFeed : FeedingDetails
{
    private BottleFeed(IReadOnlyList<BottleComponent> components, Volume total)
    {
        Components = components;
        Total = total;
    }

    public IReadOnlyList<BottleComponent> Components { get; }

    public Volume Total { get; }

    public static Result<BottleFeed> Create(IEnumerable<BottleComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var list = components.ToList();

        if (list.Count == 0)
            return Result<BottleFeed>.Failure(FeedingErrors.BottleNoComponents);

        if (list.Select(c => c.Milk).Distinct().Count() != list.Count)
            return Result<BottleFeed>.Failure(FeedingErrors.BottleDuplicateMilk);

        var total = list.Sum(c => c.Volume.Millilitres);
        if (total > FeedingThresholds.BottleTotalMaxMl)
            return Result<BottleFeed>.Failure(FeedingErrors.BottleTotalOutOfRange);

        return new BottleFeed(list.AsReadOnly(), Volume.FromMillilitres(total).Value);
    }

    /// <summary>A bottle of a single milk, the common case.</summary>
    public static Result<BottleFeed> Of(Milk milk, Volume volume) => Create([new BottleComponent(milk, volume)]);

#pragma warning disable CA1822 // An instance method, so edits read as d => d.WithComponents(...) like every other kind.
    public Result<BottleFeed> WithComponents(IEnumerable<BottleComponent> components) => Create(components);
#pragma warning restore CA1822

    public bool Equals(BottleFeed? other) =>
        other is not null && Total == other.Total && Components.SequenceEqual(other.Components);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in Components)
            hash.Add(component);
        return hash.ToHashCode();
    }
}
