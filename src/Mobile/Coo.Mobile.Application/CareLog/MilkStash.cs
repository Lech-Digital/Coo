using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.MilkStash;
using Coo.Domain.Common;

namespace Coo.Mobile.Application.CareLog;

/// <summary>A bag in the stash, with when to use it by and how fresh it is now.</summary>
public sealed record StashBag(CareEntry Entry, MilkBagDetails Details, DateTimeOffset UseBy, MilkFreshness Freshness)
{
    public DateTimeOffset ExpressedAt => Entry.Timing.OccurredAt;
}

/// <summary>The family's milk stash: the bags in stock, the one to use first at the top.</summary>
public sealed record MilkStashSummary(IReadOnlyList<StashBag> Bags)
{
    public static readonly MilkStashSummary Empty = new([]);

    public decimal FridgeMl => Bags.Where(b => b.Details.Place == MilkPlace.Fridge).Sum(b => b.Details.Volume.Millilitres);

    public decimal FreezerMl => Bags.Where(b => b.Details.Place == MilkPlace.Freezer).Sum(b => b.Details.Volume.Millilitres);

    public int UseSoon => Bags.Count(b => b.Freshness == MilkFreshness.UseSoon);

    public int Expired => Bags.Count(b => b.Freshness == MilkFreshness.Expired);

    public bool IsEmpty => Bags.Count == 0;
}

/// <summary>The stash now: every bag still in stock, oldest use-by first, so the one to take is always on top.</summary>
public sealed class GetMilkStash(ICareLogStore store)
{
    public async Task<MilkStashSummary> ExecuteAsync(HouseholdId household, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await store.ListKindAsync(household, MilkBagKind.Instance.Code, cancellationToken);
        var bags = stored
            .OfType<LiveEntry>()
            .Select(l => l.Entry)
            .Where(e => e.Details is MilkBagDetails { IsInStock: true })
            .Select(e =>
            {
                var details = (MilkBagDetails)e.Details;
                return new StashBag(e, details, MilkStorage.UseBy(details, e.Timing.OccurredAt), MilkStorage.Freshness(details, e.Timing.OccurredAt, now));
            })
            .OrderBy(b => b.UseBy)
            .ThenBy(b => b.ExpressedAt)
            .ToList();
        return new MilkStashSummary(bags);
    }
}

/// <summary>A bag into the stash, from when the milk was expressed; one per bag, so a pumping of 240 ml may be two.</summary>
public sealed class AddMilkBag(LogEntry log)
{
    public async Task<Result<CareEntry>> ExecuteAsync(
        HouseholdId household, Volume volume, MilkPlace place, DateTimeOffset expressedAt, string? label, CancellationToken cancellationToken)
    {
        var details = MilkBagDetails.Create(volume, place, label: label);
        if (details.IsFailure)
            return Result<CareEntry>.Failure(details.Error!);
        return await log.ExecuteAsync(
            new LogEntryCommand(household, EntrySubject.Caregiver(PersonRef.ThisDevicesPerson), details.Value, EntryTiming.Point(expressedAt),
                PersonRef.ThisDevicesPerson, EntrySource.Manual),
            cancellationToken);
    }
}

/// <summary>What happens to a bag: used, frozen, thawed, thrown away, or put back in stock.</summary>
public enum MilkBagChange
{
    Use,
    Freeze,
    Thaw,
    Discard,
    Restock,
}

public sealed class ChangeMilkBag(ReviseEntry revise)
{
    public Task<Result<CareEntry>> ExecuteAsync(EntryId bag, MilkBagChange change, DateTimeOffset now, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return revise.ExecuteAsync(bag, (e, version, at) => e.Revise<MilkBagDetails>(b => change switch
        {
            MilkBagChange.Use => b.Use(today),
            MilkBagChange.Freeze => b.Freeze(),
            MilkBagChange.Thaw => b.Thaw(now),
            MilkBagChange.Discard => b.Discard(today),
            _ => b.Restock(),
        }, version, at), cancellationToken);
    }
}
