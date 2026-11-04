using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.CareLog;

/// <summary>
/// A caregiver's day with the baby, as the parent hears it at pick-up: feeds and bottles, naps, diapers, medicines, the
/// notes, and from when to when.
/// </summary>
public sealed record HandoverDay(
    UserId Person,
    string Name,
    int Feeds,
    int Bottles,
    decimal BottleMl,
    int Naps,
    TimeSpan Sleep,
    int Diapers,
    int Dirty,
    int Medicines,
    IReadOnlyList<string> Notes,
    DateTimeOffset First,
    DateTimeOffset Last);

/// <summary>
/// What Today shows for the handover: to a parent, each caregiver's day (<see cref="FromCaregivers"/>); to a caregiver,
/// their own day to hand over (<see cref="Mine"/>).
/// </summary>
public sealed record HandoverView(IReadOnlyList<HandoverDay> FromCaregivers, HandoverDay? Mine)
{
    public static readonly HandoverView None = new([], null);
}

/// <summary>
/// The end-of-day handover for a nanny or daycare: built on the phone from today's synced entries and the family's roles.
/// Nothing is sent; the parent sees it on Today, the caregiver can share their own day as text.
/// </summary>
public sealed class GetHandover(IFamilyStore families, ISessionStore sessions)
{
    public async Task<HandoverView> ExecuteAsync(
        HouseholdId household, BabyId baby, IEnumerable<CareEntry> today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(today);
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return HandoverView.None;
        var me = session.Profile.UserId;
        var myRole = (await families.ListAsync(me, cancellationToken))?.FirstOrDefault(m => m.Household == household)?.Role;
        var members = await families.ListMemberNamesAsync(household, cancellationToken);
        var entries = today.Where(e => e.Subject is BabySubject { BabyId: var b } && b == baby).ToList();

        if (myRole == Role.Caregiver)
        {
            var name = members.FirstOrDefault(m => m.UserId == me)?.Name ?? "";
            return new HandoverView([], Day(me, name, entries.Where(e => e.LoggedBy is ThisDevicesPersonRef || e.LoggedBy is UserPerson { UserId: var u } && u == me), now));
        }

        if (myRole is not (Role.Owner or Role.Parent))
            return HandoverView.None;
        var days = members
            .Where(m => m.Role == Role.Caregiver && m.UserId != me)
            .Select(m => Day(m.UserId, m.Name, entries.Where(e => e.LoggedBy is UserPerson { UserId: var u } && u == m.UserId), now))
            .OfType<HandoverDay>()
            .ToList();
        return new HandoverView(days, null);
    }

    /// <summary>The day from these entries; null when there are none.</summary>
    public static HandoverDay? Day(UserId person, string name, IEnumerable<CareEntry> logged, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(logged);
        var entries = logged.OrderBy(e => e.Timing.OccurredAt).ToList();
        if (entries.Count == 0)
            return null;
        var bottles = entries.Select(e => e.Details).OfType<BottleFeed>().ToList();
        var sleeps = entries.Where(e => e.Details is SleepDetails && e.Timing is SessionTiming).Select(e => ((SessionTiming)e.Timing).Range).ToList();
        var diapers = entries.Select(e => e.Details).OfType<DiaperDetails>().ToList();
        return new HandoverDay(
            person,
            name,
            entries.Count(e => e.Details is FeedingDetails),
            bottles.Count,
            bottles.Sum(b => b.Total.Millilitres),
            sleeps.Count,
            sleeps.Aggregate(TimeSpan.Zero, (sum, range) => sum + range.Duration(now)),
            diapers.Count,
            diapers.Count(d => d.Contents is DiaperContents.Dirty or DiaperContents.Mixed),
            entries.Count(e => e.Details is MedicineDetails),
            [.. entries.Select(e => e.Note?.Text).OfType<string>().Where(n => n.Trim().Length > 0)],
            entries[0].Timing.OccurredAt,
            entries.Max(e => e.Timing is SessionTiming { Range.End: { } end } ? end : e.Timing.OccurredAt));
    }
}
