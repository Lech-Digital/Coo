using Coo.Api.Application.CareLog;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>The server's write path (Spec 008 §4): domain objects in, through the write gate, with a gapless seq.</summary>
[Collection(PostgresTests.Name)]
public sealed class CareEntryStoreTests(PostgresServer server)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task SaveThenFind_EveryKind_ReturnsAnEqualEntry()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        var baby = EntrySubject.Baby(ServerDatabase.Baby);
        var entries = new List<CareEntry>
        {
            Entry(db, baby, new DiaperDetails(DiaperContents.Mixed), EntryTiming.Point(T0)),
            Entry(db, baby, BreastFeed.FromSideDurations(T0, [(BreastSide.Left, 12), (BreastSide.Right, 7)]).Value, null),
            Entry(db, baby, BottleFeed.Create([new(Milk.ExpressedBreastMilk, Ml(60)), new(Milk.Formula, Ml(40))]).Value, EntryTiming.Point(T0)),
            Entry(db, baby, new SleepDetails(SleepKindOverride.Nap), EntryTiming.Session(TimeRange.Create(T0, T0.AddMinutes(50)).Value)),
            Entry(db, EntrySubject.Caregiver(PersonRef.ThisDevicesPerson), new PumpDetails(PumpYield.BySide(Ml(70), null).Value),
                EntryTiming.Session(TimeRange.Open(T0))),
        };

        // Act
        var loaded = new List<ServerStoredEntry?>();
        foreach (var entry in entries)
        {
            await db.Store.SaveAsync(entry, CancellationToken.None);
            loaded.Add(await db.Store.FindAsync(entry.Id, CancellationToken.None));
        }

        // Assert
        for (var i = 0; i < entries.Count; i++)
            AssertSame(entries[i], loaded[i].Should().BeOfType<LiveServerEntry>().Subject.Entry);
    }

    [Fact]
    public async Task Save_NonUtcOffsets_PreservesTheOffsetsPeopleSaw()
    {
        // Arrange: logged while travelling.
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        var start = new DateTimeOffset(2026, 10, 1, 6, 30, 0, TimeSpan.FromHours(-7));
        var entry = Entry(db, EntrySubject.Baby(ServerDatabase.Baby), new SleepDetails(),
            EntryTiming.Session(TimeRange.Create(start, start.AddHours(1).ToOffset(TimeSpan.FromHours(5.5))).Value));

        // Act
        await db.Store.SaveAsync(entry, CancellationToken.None);
        var loaded = ((LiveServerEntry)(await db.Store.FindAsync(entry.Id, CancellationToken.None))!).Entry;

        // Assert
        var range = ((SessionTiming)loaded.Timing).Range;
        range.Start.Should().Be(start);
        range.Start.Offset.Should().Be(TimeSpan.FromHours(-7));
        range.End!.Value.Offset.Should().Be(TimeSpan.FromHours(5.5));
    }

    [Fact]
    public async Task Save_SeveralWrites_GivesEachAGaplessSeqPerHousehold()
    {
        // Arrange: two households, each with its own counter (ADR 0002 §6).
        await using var db = await ServerDatabase.CreateAsync(server);
        var other = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0a2"));
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        await db.Households.EnsureAsync(other, CancellationToken.None);
        var first = Entry(db, EntrySubject.Baby(ServerDatabase.Baby), new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(T0));

        // Act
        var seqs = new List<long>
        {
            await db.Store.SaveAsync(first, CancellationToken.None),
            await db.Store.SaveAsync(Entry(db, EntrySubject.Baby(ServerDatabase.Baby), new DiaperDetails(DiaperContents.Dirty), EntryTiming.Point(T0)), CancellationToken.None),
        };
        first.Revise<DiaperDetails>(d => d.WithContents(DiaperContents.Mixed), db.NextStamp(), db.Time.GetUtcNow());
        seqs.Add(await db.Store.SaveAsync(first, CancellationToken.None));
        var otherSeq = await db.Store.SaveAsync(Entry(db, EntrySubject.Baby(ServerDatabase.Baby), new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(T0), other), CancellationToken.None);

        // Assert
        seqs.Should().Equal(1, 2, 3);
        otherSeq.Should().Be(1, "another household's writes do not move this counter");
        var stored = (LiveServerEntry)(await db.Store.FindAsync(first.Id, CancellationToken.None))!;
        stored.Seq.Should().Be(3, "an edit is a new write, so sync pulls it again");
        ((DiaperDetails)stored.Entry.Details).Contents.Should().Be(DiaperContents.Mixed);
    }

    [Fact]
    public async Task Save_TombstoneOfAnEntry_IsStoredAsDeleted()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        var entry = Entry(db, EntrySubject.Baby(ServerDatabase.Baby), new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(T0));
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Act
        entry.Delete(db.NextStamp());
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Assert
        var stored = (LiveServerEntry)(await db.Store.FindAsync(entry.Id, CancellationToken.None))!;
        stored.Entry.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task Save_UnknownHousehold_ThrowsAndWritesNothing()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var entry = Entry(db, EntrySubject.Baby(ServerDatabase.Baby), new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(T0));

        // Act
        Func<Task> act = () => db.Store.SaveAsync(entry, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdNotFoundException>();
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(0L);
    }

    [Fact]
    public async Task Save_SameIdInAnotherHousehold_IsRefusedAndTheCounterDoesNotMove()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var other = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0a3"));
        await db.Households.EnsureAsync(ServerDatabase.Household, CancellationToken.None);
        await db.Households.EnsureAsync(other, CancellationToken.None);
        var entry = Entry(db, EntrySubject.Baby(ServerDatabase.Baby), new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(T0));
        await db.Store.SaveAsync(entry, CancellationToken.None);
        var intruder = CareEntry.Create(entry.Id, other, EntrySubject.Baby(ServerDatabase.Baby), new DiaperDetails(DiaperContents.Dirty),
            EntryTiming.Point(T0), PersonRef.ThisDevicesPerson, EntrySource.Manual, db.NextStamp(), db.Time.GetUtcNow()).Value;

        // Act
        Func<Task> act = () => db.Store.SaveAsync(intruder, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{other.Value}';")).Should().Be(1L, "the refused write rolled back");
        var stored = (LiveServerEntry)(await db.Store.FindAsync(entry.Id, CancellationToken.None))!;
        stored.Entry.HouseholdId.Should().Be(ServerDatabase.Household);
    }

    private static CareEntry Entry(ServerDatabase db, EntrySubject subject, EntryDetails details, EntryTiming? timing, HouseholdId? household = null) =>
        CareEntry.Create(new EntryId(db.NewId()), household ?? ServerDatabase.Household, subject, details, timing,
            PersonRef.User(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0e1"))), EntrySource.AiParsed("p-3"),
            db.NextStamp(), db.Time.GetUtcNow(), Note.From("first try").Value).Value;

    private static void AssertSame(CareEntry expected, CareEntry actual)
    {
        actual.Id.Should().Be(expected.Id);
        actual.HouseholdId.Should().Be(expected.HouseholdId);
        actual.KindCode.Should().Be(expected.KindCode);
        actual.Subject.Should().Be(expected.Subject);
        actual.Timing.Should().Be(expected.Timing);
        actual.Details.Should().Be(expected.Details);
        actual.LoggedBy.Should().Be(expected.LoggedBy);
        actual.Source.Should().Be(expected.Source);
        actual.Note.Should().Be(expected.Note);
        actual.Version.Should().Be(expected.Version);
        actual.IsDeleted.Should().Be(expected.IsDeleted);
    }

    private static Volume Ml(decimal ml) => Volume.FromMillilitres(ml).Value;
}
