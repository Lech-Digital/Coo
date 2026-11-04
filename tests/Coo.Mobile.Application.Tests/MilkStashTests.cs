using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.MilkStash;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

/// <summary>The milk stash: bags in, oldest first, used, frozen, thawed and thrown away.</summary>
public sealed class MilkStashTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.FromHours(1));

    private readonly FakeTimeProvider _time = new(Now);
    private readonly MemoryStore _store = new();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private long _wall = 1;

    public MilkStashTests() =>
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));

    [Fact]
    public async Task Get_BagsInTheFridgeAndFreezer_OldestUseByFirstWithTotalsAndWarnings()
    {
        // Arrange: frozen a week ago, fresh three days ago (use soon), fresh this morning.
        await Add(150, MilkPlace.Freezer, Now.AddDays(-7));
        await Add(90, MilkPlace.Fridge, Now.AddDays(-3.5));
        await Add(120, MilkPlace.Fridge, Now.AddHours(-2));

        // Act
        var stash = await new GetMilkStash(_store).ExecuteAsync(Household, Now, CancellationToken.None);

        // Assert
        stash.Bags.Select(b => b.Details.Volume.Millilitres).Should().Equal(90, 120, 150);
        (stash.FridgeMl, stash.FreezerMl).Should().Be((210m, 150m));
        stash.UseSoon.Should().Be(1);
        stash.Expired.Should().Be(0);
    }

    [Fact]
    public async Task Change_UseThawAndDiscard_LeaveTheStashAsExpected()
    {
        // Arrange
        var frozen = (await Add(150, MilkPlace.Freezer, Now.AddDays(-7))).Value.Id;
        var fresh = (await Add(90, MilkPlace.Fridge, Now.AddHours(-1))).Value.Id;
        var old = (await Add(60, MilkPlace.Fridge, Now.AddDays(-5))).Value.Id;
        var change = new ChangeMilkBag(new ReviseEntry(_store, _clock, _time));

        // Act
        await change.ExecuteAsync(frozen, MilkBagChange.Thaw, Now, Amsterdam, CancellationToken.None);
        await change.ExecuteAsync(fresh, MilkBagChange.Use, Now, Amsterdam, CancellationToken.None);
        await change.ExecuteAsync(old, MilkBagChange.Discard, Now, Amsterdam, CancellationToken.None);
        var refreeze = await change.ExecuteAsync(frozen, MilkBagChange.Freeze, Now, Amsterdam, CancellationToken.None);
        var stash = await new GetMilkStash(_store).ExecuteAsync(Household, Now, CancellationToken.None);

        // Assert
        refreeze.IsFailure.Should().BeTrue("thawed milk is never frozen again");
        stash.Bags.Should().ContainSingle().Which.UseBy.Should().Be(Now.AddHours(24), "thawed milk keeps a day");
        stash.FreezerMl.Should().Be(0);
    }

    [Fact]
    public async Task Add_ThreeBagsThenOneUsed_NumbersThemAndReusesTheFreeNumber()
    {
        // Arrange: three containers in the fridge (owner, 2026-11-04: "how do we recognize them?").
        var first = (await Add(90, MilkPlace.Fridge, Now.AddHours(-3))).Value.Id;
        await Add(100, MilkPlace.Fridge, Now.AddHours(-2));
        await Add(110, MilkPlace.Fridge, Now.AddHours(-1));
        await new ChangeMilkBag(new ReviseEntry(_store, _clock, _time)).ExecuteAsync(first, MilkBagChange.Use, Now, Amsterdam, CancellationToken.None);

        // Act
        await Add(120, MilkPlace.Fridge, Now);
        var stash = await new GetMilkStash(_store).ExecuteAsync(Household, Now, CancellationToken.None);

        // Assert
        stash.Bags.Select(b => (b.Details.Volume.Millilitres, b.Details.Number)).Should().BeEquivalentTo(
            [(100m, (int?)2), (110m, (int?)3), (120m, (int?)1)]);
    }

    [Fact]
    public async Task Edit_HalfABagUsed_KeepsTheRestWithItsNewNumberAndNote()
    {
        // Arrange
        var bag = (await Add(150, MilkPlace.Fridge, Now.AddHours(-1))).Value.Id;
        var change = new ChangeMilkBag(new ReviseEntry(_store, _clock, _time));

        // Act
        var edited = await change.EditAsync(bag, Volume.FromMillilitres(70).Value, 5, "left side", CancellationToken.None);
        var refused = await change.EditAsync(bag, Volume.FromMillilitres(70).Value, 0, null, CancellationToken.None);
        var stash = await new GetMilkStash(_store).ExecuteAsync(Household, Now, CancellationToken.None);

        // Assert
        edited.IsSuccess.Should().BeTrue();
        refused.IsFailure.Should().BeTrue();
        var details = stash.Bags.Single().Details;
        (details.Volume.Millilitres, details.Number, details.Label).Should().Be((70m, 5, "left side"));
    }

    [Fact]
    public async Task Add_ExpiredMilk_StaysInTheStashMarkedExpired()
    {
        // Arrange: five days in the fridge.
        await Add(80, MilkPlace.Fridge, Now.AddDays(-5));

        // Act
        var stash = await new GetMilkStash(_store).ExecuteAsync(Household, Now, CancellationToken.None);

        // Assert
        stash.Expired.Should().Be(1);
        stash.Bags.Single().Freshness.Should().Be(MilkFreshness.Expired);
    }

    private Task<Result<CareEntry>> Add(decimal ml, MilkPlace place, DateTimeOffset expressed) =>
        new AddMilkBag(new LogEntry(_store, _clock, _time), new GetMilkStash(_store)).ExecuteAsync(Household, Volume.FromMillilitres(ml).Value, place, expressed, null, CancellationToken.None);

    private sealed class MemoryStore : ICareLogStore
    {
        private readonly Dictionary<EntryId, CareEntry> _entries = [];

        public Task SaveAsync(CareEntry entry, CancellationToken cancellationToken)
        {
            _entries[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task<StoredEntry?> FindAsync(EntryId id, CancellationToken cancellationToken) =>
            Task.FromResult<StoredEntry?>(_entries.TryGetValue(id, out var e) ? new LiveEntry(e) : null);

        public Task<IReadOnlyList<StoredEntry>> ListAsync(HouseholdId household, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredEntry>>([.. _entries.Values
                .Where(e => e.Timing.OccurredAt >= windowStart && e.Timing.OccurredAt < windowEnd)
                .Select(e => new LiveEntry(e))]);

        public Task<bool> DeleteAsync(EntryId id, HlcStamp version, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> WriteAllAsync(CareLogWrites changes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoreCounts> CountsAsync(HouseholdId household, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountAllEntriesAsync(HouseholdId household, CancellationToken cancellationToken) => Task.FromResult(_entries.Count);
    }
}
