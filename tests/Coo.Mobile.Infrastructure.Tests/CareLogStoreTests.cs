using System.Diagnostics;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Tests;

public sealed class CareLogStoreTests
{
    private static readonly TimeSpan Cest = TimeSpan.FromHours(2);
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, Cest);

    [Fact]
    public async Task SaveThenLoad_EveryKind_ReturnsEqualEntry()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entries = new List<CareEntry>
        {
            await Create(db, EntrySubject.Baby(TestDatabase.Baby), new DiaperDetails(DiaperContents.Mixed), EntryTiming.Point(T0)),
            await Create(db, EntrySubject.Baby(TestDatabase.Baby),
                BreastFeed.FromSideDurations(T0, [(BreastSide.Left, 12), (BreastSide.Right, 7)]).Value, null),
            await Create(db, EntrySubject.Baby(TestDatabase.Baby),
                BottleFeed.Create([new(Milk.ExpressedBreastMilk, Ml(60)), new(Milk.Formula, Ml(40))]).Value, EntryTiming.Point(T0)),
            await Create(db, EntrySubject.Baby(TestDatabase.Baby), new SleepDetails(SleepKindOverride.Nap),
                EntryTiming.Session(TimeRange.Create(T0, T0.AddMinutes(50)).Value)),
            await Create(db, EntrySubject.Caregiver(PersonRef.ThisDevicesPerson), new PumpDetails(PumpYield.BySide(Ml(70), null).Value),
                EntryTiming.Session(TimeRange.Open(T0))),
        };

        // Act
        var loaded = new List<StoredEntry?>();
        foreach (var entry in entries)
            loaded.Add(await db.Store.FindAsync(entry.Id, CancellationToken.None));

        // Assert
        for (var i = 0; i < entries.Count; i++)
            AssertSame(entries[i], loaded[i].Should().BeOfType<LiveEntry>().Subject.Entry);
    }

    [Fact]
    public async Task SaveThenLoad_NonUtcOffsets_PreservesOffsets()
    {
        // Arrange: logged while travelling.
        await using var db = await TestDatabase.CreateAsync();
        var start = new DateTimeOffset(2026, 10, 1, 6, 30, 0, TimeSpan.FromHours(-7));
        var entry = await Create(db, EntrySubject.Baby(TestDatabase.Baby), new SleepDetails(),
            EntryTiming.Session(TimeRange.Create(start, start.AddHours(1).ToOffset(TimeSpan.FromHours(5.5))).Value));

        // Act
        var loaded = ((LiveEntry)(await db.Store.FindAsync(entry.Id, CancellationToken.None))!).Entry;

        // Assert
        var range = ((SessionTiming)loaded.Timing).Range;
        range.Start.Offset.Should().Be(TimeSpan.FromHours(-7));
        range.End!.Value.Offset.Should().Be(TimeSpan.FromHours(5.5));
    }

    [Fact]
    public async Task GetEntries_MixedOffsets_OrdersByInstant()
    {
        // Arrange: 13:30 UTC happened after 14:00 at UTC+2 (12:00 UTC); as text, the order would flip.
        await using var db = await TestDatabase.CreateAsync();
        var later = await db.DiaperAsync(new DateTimeOffset(2026, 10, 1, 13, 30, 0, TimeSpan.Zero));
        var earlier = await db.DiaperAsync(new DateTimeOffset(2026, 10, 1, 14, 0, 0, Cest));

        // Act
        var list = await db.Store.ListAsync(TestDatabase.Household, T0.AddDays(-1), T0.AddDays(1), CancellationToken.None);

        // Assert
        list.Select(e => e.Id).Should().Equal(later.Id, earlier.Id);
    }

    [Fact]
    public async Task Save_WritesOutboxRowInSameTransaction()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var entry = await db.DiaperAsync(T0);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_id = '{entry.Id}' AND operation = 'upsert';")).Should().Be(1);
    }

    [Fact]
    public async Task Save_WhenWriteFails_WritesNeitherRowNorOutbox()
    {
        // Arrange: the outbox insert fails, as a crash between the two writes would.
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync(
                "CREATE TRIGGER fail_outbox BEFORE INSERT ON outbox BEGIN SELECT RAISE(ABORT, 'simulated crash'); END;", CancellationToken.None);

        // Act
        var act = () => db.DiaperAsync(T0);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(0, "the row and its outbox entry commit together, or not at all");
    }

    [Fact]
    public async Task Save_StoredRowHasUnknownProperty_KeepsIt()
    {
        // Arrange: a newer app wrote a stool colour this build does not know.
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(T0, DiaperContents.Dirty);
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync(
                $$"""UPDATE care_entries SET details = '{"v":1,"contents":"dirty","stoolColour":"yellow"}' WHERE id = '{{entry.Id}}';""", CancellationToken.None);
        var live = ((LiveEntry)(await db.Store.FindAsync(entry.Id, CancellationToken.None))!).Entry;

        // Act
        live.Revise<DiaperDetails>(d => d.WithContents(DiaperContents.Mixed), await db.Device.NextAsync(CancellationToken.None), db.Time.GetUtcNow());
        await db.Store.SaveAsync(live, CancellationToken.None);

        // Assert
        await using var check = await db.OpenAsync();
        (await check.ScalarAsync($"SELECT details FROM care_entries WHERE id = '{entry.Id}';", CancellationToken.None))
            .Should().Be("""{"v":1,"contents":"mixed","stoolColour":"yellow"}""");
    }

    [Fact]
    public async Task Read_UnknownKindRow_ReturnsReadOnlyAndKeepsBytes()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        const string details = """{"v":3,"drug":"vitamin D","doseDrops":1}""";
        var id = await InsertForeignAsync(db, "medication", details);

        // Act
        var stored = await db.Store.FindAsync(id, CancellationToken.None);

        // Assert
        stored.Should().BeOfType<ReadOnlyEntry>().Which.KindCode.Should().Be("medication");
        (await DetailsOf(db, id)).Should().Be(details);
        (await db.Store.CountsAsync(TestDatabase.Household, CancellationToken.None)).ReadOnly.Should().Be(1);
    }

    [Fact]
    public async Task Read_UnknownEnumValue_ReturnsReadOnly()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = await InsertForeignAsync(db, "diaper", """{"v":1,"contents":"lavender"}""");

        // Act
        var stored = await db.Store.FindAsync(id, CancellationToken.None);

        // Assert
        stored.Should().BeOfType<ReadOnlyEntry>();
    }

    [Fact]
    public async Task Read_MalformedRow_QuarantinesWithoutChangingDetails()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        const string details = """{"v":1,"method":"breast","completed":true,"segments":[]}""";
        var id = await InsertForeignAsync(db, "feeding", details);

        // Act
        var stored = await db.Store.FindAsync(id, CancellationToken.None);

        // Assert
        stored.Should().BeOfType<QuarantinedEntry>().Which.Reason.Should().Be("breast.no-segments");
        (await DetailsOf(db, id)).Should().Be(details);
        (await db.ScalarAsync($"SELECT count(*) FROM care_entries WHERE id = '{id}' AND integrity = 'quarantined';")).Should().Be(1);
    }

    [Fact]
    public async Task Read_OneMalformedRowAmongMany_ReturnsTheRest()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(T0);
        await db.DiaperAsync(T0.AddMinutes(30));
        await InsertForeignAsync(db, "diaper", """{"v":1,"contents":7}""");

        // Act
        var list = await db.Store.ListAsync(TestDatabase.Household, T0.AddDays(-1), T0.AddDays(1), CancellationToken.None);

        // Assert
        list.OfType<LiveEntry>().Should().HaveCount(2);
        list.OfType<QuarantinedEntry>().Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_QuarantinedRow_WritesTombstoneAndOutbox()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = await InsertForeignAsync(db, "diaper", """{"v":1,"contents":7}""");

        // Act
        var deleted = await db.Store.DeleteAsync(id, await db.Device.NextAsync(CancellationToken.None), CancellationToken.None);

        // Assert
        deleted.Should().BeTrue();
        (await db.ScalarAsync($"SELECT is_deleted FROM care_entries WHERE id = '{id}';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_id = '{id}' AND operation = 'delete';")).Should().Be(1);
        (await db.Store.ListAsync(TestDatabase.Household, T0.AddDays(-1), T0.AddDays(1), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task FindVersionAsync_LiveDeletedAndUnknown_ReturnsTheVersionAndWhetherItIsATombstone()
    {
        // Arrange: ADR 0009 §2, a watch state is compared with the stored version, tombstones included.
        await using var db = await TestDatabase.CreateAsync();
        var (live, gone) = (await db.DiaperAsync(T0), await db.DiaperAsync(T0.AddMinutes(1)));
        var deletedAt = await Next(db);
        await db.Store.DeleteAsync(gone.Id, deletedAt, CancellationToken.None);

        // Act
        var liveVersion = await db.Store.FindVersionAsync(live.Id, CancellationToken.None);
        var goneVersion = await db.Store.FindVersionAsync(gone.Id, CancellationToken.None);
        var unknown = await db.Store.FindVersionAsync(new Coo.Domain.Common.EntryId(Guid.Parse("019a4f2e-8c00-7000-8000-0000000000ff")), CancellationToken.None);

        // Assert
        liveVersion.Should().Be((live.Version, false));
        goneVersion.Should().Be((deletedAt, true));
        unknown.Should().BeNull();
    }

    [Fact]
    public async Task WriteAll_SaveTombstoneAndRestore_WritesEachWithItsOutboxRow()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var (kept, gone, back) = (await db.DiaperAsync(T0), await db.DiaperAsync(T0.AddMinutes(1)), await db.DiaperAsync(T0.AddMinutes(2)));
        await db.Store.DeleteAsync(back.Id, await Next(db), CancellationToken.None);
        kept.SetNote(Note.From("merged").Value, await Next(db));
        var writes = new CareLogWrites([kept], [new EntryVersion(gone.Id, await Next(db))], [new EntryVersion(back.Id, await Next(db))]);

        // Act
        var written = await db.Store.WriteAllAsync(writes, CancellationToken.None);

        // Assert
        written.Should().BeTrue();
        var stored = await db.Store.ListAsync(TestDatabase.Household, T0.AddDays(-1), T0.AddDays(1), CancellationToken.None);
        stored.Select(s => s.Id).Should().BeEquivalentTo([kept.Id, back.Id]);
        ((LiveEntry)stored.Single(s => s.Id == kept.Id)).Entry.Note!.Text.Should().Be("merged");
        ((LiveEntry)stored.Single(s => s.Id == back.Id)).Entry.Version.Should().Be(writes.Restores[0].Version);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_id = '{kept.Id}' AND operation = 'upsert';")).Should().Be(2);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_id = '{gone.Id}' AND operation = 'delete';")).Should().Be(1);
        (await db.ScalarAsync($"SELECT count(*) FROM outbox WHERE entity_id = '{back.Id}' AND operation = 'upsert';")).Should().Be(2);
    }

    [Fact]
    public async Task WriteAll_TombstoneOfAnEntryAlreadyDeleted_WritesNothing()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var (kept, gone) = (await db.DiaperAsync(T0), await db.DiaperAsync(T0.AddMinutes(1)));
        await db.Store.DeleteAsync(gone.Id, await Next(db), CancellationToken.None);
        var outboxBefore = await db.ScalarAsync("SELECT count(*) FROM outbox;");
        kept.SetNote(Note.From("merged").Value, await Next(db));

        // Act
        var written = await db.Store.WriteAllAsync(new CareLogWrites([kept], [new EntryVersion(gone.Id, await Next(db))], []), CancellationToken.None);

        // Assert
        written.Should().BeFalse();
        ((LiveEntry)(await db.Store.FindAsync(kept.Id, CancellationToken.None))!).Entry.Note.Should().BeNull("the save rolls back with the failed tombstone");
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(outboxBefore);
    }

    [Fact]
    public async Task WriteAll_RestoreOfAnEntryNotDeleted_WritesNothing()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var (gone, live) = (await db.DiaperAsync(T0), await db.DiaperAsync(T0.AddMinutes(1)));
        var outboxBefore = await db.ScalarAsync("SELECT count(*) FROM outbox;");

        // Act
        var written = await db.Store.WriteAllAsync(
            new CareLogWrites([], [new EntryVersion(gone.Id, await Next(db))], [new EntryVersion(live.Id, await Next(db))]), CancellationToken.None);

        // Assert
        written.Should().BeFalse();
        (await db.ScalarAsync($"SELECT is_deleted FROM care_entries WHERE id = '{gone.Id}';")).Should().Be(0);
        (await db.ScalarAsync("SELECT count(*) FROM outbox;")).Should().Be(outboxBefore);
    }

    [Fact]
    public async Task WriteAll_WhenATombstoneFailsMidway_WritesNothing()
    {
        // Arrange: the tombstone's outbox insert fails after the save went in, as a crash between the writes would.
        await using var db = await TestDatabase.CreateAsync();
        var (kept, gone) = (await db.DiaperAsync(T0), await db.DiaperAsync(T0.AddMinutes(1)));
        await db.ExecuteAsync(
            "CREATE TRIGGER fail_delete BEFORE INSERT ON outbox WHEN NEW.operation = 'delete' BEGIN SELECT RAISE(ABORT, 'simulated crash'); END;");
        kept.SetNote(Note.From("merged").Value, await Next(db));
        var writes = new CareLogWrites([kept], [new EntryVersion(gone.Id, await Next(db))], []);

        // Act
        var act = () => db.Store.WriteAllAsync(writes, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        ((LiveEntry)(await db.Store.FindAsync(kept.Id, CancellationToken.None))!).Entry.Note.Should().BeNull();
        (await db.ScalarAsync($"SELECT is_deleted FROM care_entries WHERE id = '{gone.Id}';")).Should().Be(0);
    }

    [Fact]
    public async Task IntegrityScan_SameAppVersion_RunsOnce()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync("1.0.0");
        await using var connection = await db.OpenAsync();

        // Act
        var ranAgain = await AppStartup.IntegrityScanAsync(connection, "1.0.0", CancellationToken.None);

        // Assert
        ranAgain.Should().BeFalse();
    }

    [Fact]
    public async Task IntegrityScan_NewAppVersion_ReevaluatesRows()
    {
        // Arrange: a row wrongly marked quarantined, as if an older build's codec had a bug.
        await using var db = await TestDatabase.CreateAsync("1.0.0");
        var entry = await db.DiaperAsync(T0);
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync($"UPDATE care_entries SET integrity = 'quarantined' WHERE id = '{entry.Id}';", CancellationToken.None);

        // Act
        var ran = await AppStartup.IntegrityScanAsync(connection, "1.0.1", CancellationToken.None);

        // Assert
        ran.Should().BeTrue();
        (await connection.ScalarAsync($"SELECT integrity FROM care_entries WHERE id = '{entry.Id}';", CancellationToken.None)).Should().Be("ok");
    }

    [Fact]
    public async Task IntegrityScan_10000Rows_FinishesUnderTwoSeconds()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync("1.0.0");
        await using var connection = await db.OpenAsync();
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            for (var i = 0; i < 10_000; i++)
                await InsertRowAsync(connection, transaction, $"00000000-0000-7000-8000-{i:D12}", "diaper", """{"v":1,"contents":"wet"}""");
            await transaction.CommitAsync();
        }

        // Act
        var watch = Stopwatch.StartNew();
        await AppStartup.IntegrityScanAsync(connection, "1.0.1", CancellationToken.None);
        watch.Stop();

        // Assert
        // Two seconds on a phone-class machine; GitHub's shared runners are slower and took 2.2 s (2026-11-04).
        var budget = Environment.GetEnvironmentVariable("CI") == "true" ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2);
        watch.Elapsed.Should().BeLessThan(budget);
    }

    [Fact]
    public async Task ListKindAsync_SleepPlansAmongOtherEntries_ReturnsOnlyThePlansNewestFirst()
    {
        // Arrange: a plan from last year and one from today, with a diaper in between.
        await using var db = await TestDatabase.CreateAsync();
        var plan = Coo.Domain.CareLog.Kinds.SleepPlan.SleepPlanDetails.Create(
            [Coo.Domain.Insights.SleepPlanGoal.LongerNaps], Coo.Domain.Insights.SettlingApproach.Fading, null).Value;
        var old = await Create(db, EntrySubject.Baby(TestDatabase.Baby), plan, EntryTiming.Point(T0.AddYears(-1)));
        await db.DiaperAsync(T0.AddDays(-1));
        var current = await Create(db, EntrySubject.Baby(TestDatabase.Baby), plan, EntryTiming.Point(T0));

        // Act
        var plans = await db.Store.ListKindAsync(TestDatabase.Household, Coo.Domain.CareLog.Kinds.SleepPlan.SleepPlanKind.Instance.Code, CancellationToken.None);

        // Assert
        plans.Select(p => p.Id).Should().Equal(current.Id, old.Id);
    }

    private static async Task<CareEntry> Create(TestDatabase db, EntrySubject subject, EntryDetails details, EntryTiming? timing)
    {
        var entry = CareEntry.Create(await db.NewIdAsync(), TestDatabase.Household, subject, details, timing,
            PersonRef.User(TestDatabase.Parent), EntrySource.AiParsed("p-3"), await db.Device.NextAsync(CancellationToken.None),
            db.Time.GetUtcNow(), Note.From("first try").Value).Value;
        await db.Store.SaveAsync(entry, CancellationToken.None);
        return entry;
    }

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

    private static async Task<Coo.Domain.Common.EntryId> InsertForeignAsync(TestDatabase db, string kind, string details)
    {
        var id = await db.NewIdAsync();
        await using var connection = await db.OpenAsync();
        await InsertRowAsync(connection, null, id.ToString(), kind, details);
        return id;
    }

    private static Task<int> InsertRowAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, string kind, string details) =>
        connection.ExecuteAsync(
            """
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_utc_ms, start_offset_min,
                logged_by, source, details, version_wall_ms, version_counter, version_device)
            VALUES ($id, $household, $kind, $baby, 0, $start, 120, 'this-device', 'manual', $details, 1, 0, $device);
            """,
            CancellationToken.None,
            transaction,
            ("$id", id), ("$household", TestDatabase.Household.ToString()), ("$kind", kind), ("$baby", TestDatabase.Baby.ToString()),
            ("$start", T0.ToUnixTimeMilliseconds()), ("$details", details), ("$device", "7a1c0000-0000-7000-8000-0000000000d1"));

    private static async Task<object?> DetailsOf(TestDatabase db, Coo.Domain.Common.EntryId id)
    {
        await using var connection = await db.OpenAsync();
        return await connection.ScalarAsync($"SELECT details FROM care_entries WHERE id = '{id}';", CancellationToken.None);
    }

    private static Task<HlcStamp> Next(TestDatabase db) => db.Device.NextAsync(CancellationToken.None);

    private static Volume Ml(decimal ml) => Volume.FromMillilitres(ml).Value;
}
