using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Demo;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

public sealed class SeedDemoLogTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    [Fact]
    public void Plan_ThreeWeeks_IsValidAndNeverAfterNow()
    {
        // Act
        var plan = SeedDemoLog.Plan(new DateOnly(2026, 10, 22), Amsterdam, Now).ToList();

        // Assert
        plan.Should().HaveCountGreaterThan(SeedDemoLog.Days * 12);
        plan.Should().OnlyContain(p => p.At < Now);
        plan.Should().OnlyContain(p => p.Timing == null || p.Timing.OccurredAt < Now);
        plan.Select(p => (p, r: Create(p))).Where(x => x.r.IsFailure).Select(x => $"{x.p.At:dd HH:mm} {x.p.Details.GetType().Name} {x.r.Error!.Code}").Should().BeEmpty();
    }

    [Fact]
    public async Task Plan_ThreeWeeks_SleepGrowsWeekOnWeekForTheStatsScreenshot()
    {
        // Arrange
        var entries = SeedDemoLog.Plan(new DateOnly(2026, 10, 22), Amsterdam, Now).Select(p => Create(p).Value).ToList();
        var store = Substitute.For<ICareLogStore>();
        store.ListAsync(default, default, default, default).ReturnsForAnyArgs(entries.Select(e => (StoredEntry)new LiveEntry(e)).ToList());

        // Act
        var weeks = await new GetCareTrends(store).ExecuteAsync(
            Household, Baby, TrendGrain.Week, new DateOnly(2026, 10, 22), 2, DayOfWeek.Monday, Amsterdam, Now, CancellationToken.None);

        // Assert
        weeks[1].Sleep.Should().BeGreaterThan(weeks[0].Sleep);
        weeks[0].Sleep.Should().BeGreaterThan(TimeSpan.FromHours(12)).And.BeLessThan(TimeSpan.FromHours(17));
        weeks[0].Feeds.Should().BeInRange(6, 10);
        weeks[0].Wet.Should().BeGreaterThanOrEqualTo(6, "a well-fed baby, so Today shows no wet-diaper warning in the screenshots");
    }

    [Fact]
    public async Task Plan_ThreeWeeks_NeedsNoAttention()
    {
        // Arrange: the Today screenshot showed "8 need attention" from an evening bottle on top of the last nap.
        var entries = SeedDemoLog.Plan(new DateOnly(2026, 10, 22), Amsterdam, Now).Select(p => Create(p).Value).ToList();

        // Act
        var groups = Coo.Mobile.Application.CareLog.AttentionGroups.Find(entries, new HashSet<Coo.Domain.CareLog.Duplicates.EntryPair>(), _ => false, Now);

        // Assert
        groups.Should().BeEmpty();
    }

    private static Result<CareEntry> Create(SeedDemoLog.Planned p) =>
        CareEntry.Create(
            new EntryId(Guid.CreateVersion7(p.At)), Household, p.Details is Coo.Domain.CareLog.Kinds.Pump.PumpDetails ? EntrySubject.Caregiver(PersonRef.ThisDevicesPerson) : EntrySubject.Baby(Baby), p.Details, p.Timing,
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(1, 0, Device), Now);
}
