using Coo.Codecs;
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011 §6 and §10 (Mobile.Infrastructure row), on a real SQLite file.</summary>
public sealed class SyncStoreTests
{
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly HouseholdId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a9"));
    private static readonly DeviceId Remote = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d9"));
    private const string WetDetails = """{"v":1,"contents":"wet"}""";

    private static SyncStore Store(TestDatabase db) => new(db.Database);

    private static PulledRecord PulledDiaper(Guid id, long wall, string details = WetDetails, bool deleted = false, string kind = "diaper") => new(
        SyncEntityTypes.CareEntry, id, new HlcStamp(wall, 0, Remote), deleted,
        new CareEntryPayload(kind, TestDatabase.Baby.Value, null, false, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2)), null,
            "user:7a1c0000-0000-7000-8000-0000000000c1", "manual", null, null, details));

    private static async Task<long> CountAsync(TestDatabase db, string sql) => await db.ScalarAsync(sql);

    [Fact]
    public async Task NextBatchAsync_ThreeWritesOfOneEntry_IsOneChangeWithItsCurrentStateUnderTheNewestChangeId()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        entry.SetNote(Note.From("one").Value, await db.Device.NextAsync(CancellationToken.None)).IsSuccess.Should().BeTrue();
        await db.Store.SaveAsync(entry, CancellationToken.None);
        entry.SetNote(Note.From("two").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);
        var newestChange = (string)(await (await db.OpenAsync()).ScalarAsync("SELECT change_id FROM outbox ORDER BY seq DESC LIMIT 1;", CancellationToken.None))!;

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Assert
        var change = batch.Should().ContainSingle().Subject;
        change.ChangeId.Should().Be(Guid.Parse(newestChange));
        change.Version.Should().Be(entry.Version);
        change.Payload.Should().BeOfType<CareEntryPayload>().Which.Note.Should().Be("two");
        change.LastOutboxSeq.Should().Be(3);
    }

    [Fact]
    public async Task NextBatchAsync_TimerFeedOnASubMillisecondClock_SendsTheTimingItsDetailsGive()
    {
        // Arrange: a phone clock has ticks below the millisecond; the row's columns hold milliseconds.
        await using var db = await TestDatabase.CreateAsync();
        var start = db.Time.GetLocalNow().AddTicks(1_234_567);
        var feed = BreastFeed.Start(BreastSide.Left, start).Complete(start.AddMinutes(12).AddTicks(7_654)).Value;
        var entry = CareEntry.Create(
            await db.NewIdAsync(), TestDatabase.Household, EntrySubject.Baby(TestDatabase.Baby), feed, null,
            PersonRef.User(TestDatabase.Parent), EntrySource.Timer, await db.Device.NextAsync(CancellationToken.None), db.Time.GetUtcNow().AddHours(1)).Value;
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Assert: the server derives a breast feed's timing from its details and requires the pushed one to match.
        var payload = batch.Should().ContainSingle().Subject.Payload.Should().BeOfType<CareEntryPayload>().Subject;
        var details = EntryDetailsCodecs.For(entry.KindCode)!.Decode(payload.DetailsJson).Should().BeOfType<Decoded>().Subject.Details;
        var derived = EntryKinds.Find(entry.KindCode)!.DeriveTiming(details);
        derived.Should().Be(EntryTiming.Session(TimeRange.Create(payload.StartAt, payload.EndAt).Value));
    }

    [Fact]
    public async Task NextBatchAsync_AnotherHouseholdsRecords_AreNotSent()
    {
        // Arrange: a phone that kept its own log separate (Q4) has two households' outbox rows.
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(db.Time.GetLocalNow());
        await db.Households.SaveSettingsAsync(
            HouseholdSettings.Create(Other, VolumeUnit.Millilitres, "Europe/Amsterdam", await db.Device.NextAsync(CancellationToken.None)).Value, CancellationToken.None);

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Assert
        batch.Should().ContainSingle().Which.EntityType.Should().Be(SyncEntityTypes.CareEntry);
    }

    [Fact]
    public async Task NextBatchAsync_DeletedEntryOfANewerKind_IsADeleteWithoutAPayload()
    {
        // Arrange: a read-only entry (ADR 0010 §3) can be deleted; its details are never decoded.
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id.Value, 10, """{"v":1,"drug":"vitamin D"}""", kind: "medication")], 1, false, null), CancellationToken.None);
        await db.Store.DeleteAsync(id, await db.Device.NextAsync(CancellationToken.None), CancellationToken.None);

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Assert
        var change = batch.Should().ContainSingle().Subject;
        change.IsDelete.Should().BeTrue();
        change.Payload.Should().BeNull();
    }

    [Fact]
    public async Task ApplyResultsAsync_Applied_RemovesTheRowsUpToTheChangeAndKeepsALaterEdit()
    {
        // Arrange: an edit lands while the batch is in flight.
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        var store = Store(db);
        var batch = await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);
        entry.SetNote(Note.From("later").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Act
        await store.ApplyResultsAsync(TestDatabase.Household, batch, [new PushResult(batch[0].ChangeId, PushOutcome.Applied, null)], db.Time.GetUtcNow(), CancellationToken.None);

        // Assert
        (await CountAsync(db, "SELECT count(*) FROM outbox;")).Should().Be(1);
        (await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None)).Single().Payload
            .Should().BeOfType<CareEntryPayload>().Which.Note.Should().Be("later");
    }

    [Fact]
    public async Task ApplyResultsAsync_Rejected_KeepsTheEntryListsTheReasonAndALaterApplyClearsIt()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        var store = Store(db);
        var batch = await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Act
        await store.ApplyResultsAsync(TestDatabase.Household, batch,
            [new PushResult(batch[0].ChangeId, PushOutcome.Rejected, "time.end-not-after-start")], db.Time.GetUtcNow(), CancellationToken.None);
        var listed = await store.ListRejectionsAsync(TestDatabase.Household, CancellationToken.None);
        entry.SetNote(Note.From("fixed").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);
        var fix = await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);
        await store.ApplyResultsAsync(TestDatabase.Household, fix, [new PushResult(fix[0].ChangeId, PushOutcome.Applied, null)], db.Time.GetUtcNow(), CancellationToken.None);

        // Assert
        listed.Should().ContainSingle().Which.Should().Match<SyncRejection>(r => r.EntityId == entry.Id.Value && r.Reason == "time.end-not-after-start");
        (await db.Store.FindAsync(entry.Id, CancellationToken.None)).Should().BeOfType<LiveEntry>("a rejected entry is never deleted");
        (await store.ListRejectionsAsync(TestDatabase.Household, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task NextBatchAsync_AfterAnAppKillMidPush_SendsTheSameChangeAgain()
    {
        // Arrange: the batch went out; the app died before any result was applied.
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(db.Time.GetLocalNow());
        var first = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);
        await using (var restart = await db.OpenAsync())
            SqliteConnection.ClearPool(restart);

        // Act
        var afterRestart = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Assert
        afterRestart.Single().ChangeId.Should().Be(first.Single().ChangeId, "the server's applied_changes makes the resend idempotent");
    }

    [Fact]
    public async Task PrepareForPushAsync_ThisDevicePeople_BecomeTheUser()
    {
        // Arrange: logged before sign-in, and a pumping session about the caregiver holding the phone.
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync(
                $"""
                INSERT INTO care_entries (id, household_id, kind, subject_person, is_session, start_utc_ms, start_offset_min, end_utc_ms, end_offset_min,
                    logged_by, source, details, version_wall_ms, version_counter, version_device)
                VALUES ('{(await db.NewIdAsync()).Value}', '{TestDatabase.Household.Value}', 'pump', 'this-device', 1, 1000, 0, 2000, 0,
                    'this-device', 'manual', '{"{"}"v":1{"}"}', 1, 0, '{Remote.Value}');
                """, CancellationToken.None);

        // Act
        await Store(db).PrepareForPushAsync(TestDatabase.Household, User, CancellationToken.None);

        // Assert
        (await CountAsync(db, "SELECT count(*) FROM care_entries WHERE logged_by = 'this-device' OR subject_person = 'this-device';")).Should().Be(0);
        (await CountAsync(db, "SELECT count(*) FROM care_entries WHERE logged_by = 'user:7a1c0000-0000-7000-8000-0000000000c1' AND subject_person = logged_by;")).Should().Be(1);
    }

    [Fact]
    public async Task ApplyPageAsync_EntryOfAKindThisBuildDoesNotKnow_IsStoredAsReceivedAndReadOnly()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();
        const string Foreign = """{"v":1,"drug":"vitamin D","doseDrops":1}""";

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id.Value, 10, Foreign, kind: "medication")], 1, false, null), CancellationToken.None);

        // Assert
        (await (await db.OpenAsync()).ScalarAsync($"SELECT details FROM care_entries WHERE id = '{id.Value}';", CancellationToken.None)).Should().Be(Foreign);
        (await db.Store.FindAsync(id, CancellationToken.None)).Should().BeOfType<ReadOnlyEntry>();
        (await CountAsync(db, "SELECT count(*) FROM outbox;")).Should().Be(0, "a pulled record leaves no outbox row");
    }

    [Fact]
    public async Task ApplyPageAsync_PropertyThisBuildDoesNotKnow_SurvivesALocalEdit()
    {
        // Arrange: a newer phone added "stoolColour" (ADR 0010 §3.2).
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();
        await Store(db).ApplyPageAsync(TestDatabase.Household,
            new PulledPage([PulledDiaper(id.Value, 10, """{"v":1,"contents":"wet","stoolColour":"green"}""")], 1, false, null), CancellationToken.None);
        var entry = ((LiveEntry)(await db.Store.FindAsync(id, CancellationToken.None))!).Entry;

        // Act
        entry.SetNote(Note.From("edited here").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Assert
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);
        ((CareEntryPayload)batch.Single().Payload!).DetailsJson.Should().Contain("\"stoolColour\":\"green\"");
    }

    [Theory]
    [InlineData(20, 10, "mine")]
    [InlineData(5, 10, "theirs")]
    public async Task ApplyPageAsync_PendingLocalEdit_IsKeptOnlyWhenNewer(long localWall, long remoteWall, string expected)
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();
        await using (var connection = await db.OpenAsync())
        {
            await connection.ExecuteAsync(
                $"""
                INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_utc_ms, start_offset_min, logged_by, source,
                    note, details, version_wall_ms, version_counter, version_device)
                VALUES ('{id.Value}', '{TestDatabase.Household.Value}', 'diaper', '{TestDatabase.Baby.Value}', 0, 1000, 0, 'this-device', 'manual',
                    'mine', '{WetDetails}', {localWall}, 0, '{Remote.Value}');
                INSERT INTO outbox (change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device)
                VALUES ('{(await db.NewIdAsync()).Value}', 'CareEntry', '{id.Value}', 'upsert', {localWall}, 0, '{Remote.Value}');
                """, CancellationToken.None);
        }

        var theirs = PulledDiaper(id.Value, remoteWall) with { };
        var page = new PulledPage([theirs with { Payload = ((CareEntryPayload)theirs.Payload) with { Note = "theirs" } }], 1, false, null);

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, page, CancellationToken.None);

        // Assert
        (await (await db.OpenAsync()).ScalarAsync($"SELECT note FROM care_entries WHERE id = '{id.Value}';", CancellationToken.None)).Should().Be(expected);
    }

    [Fact]
    public async Task ApplyPageAsync_ARecordTheDatabaseRefuses_ChangesNothingAndKeepsTheCursor()
    {
        // Arrange: the cursor commits with its page (Spec 011 §6.2); a refused row undoes the whole page.
        await using var db = await TestDatabase.CreateAsync();
        var good = PulledDiaper((await db.NewIdAsync()).Value, 10);
        var bad = new PulledRecord(SyncEntityTypes.BabyProfile, (await db.NewIdAsync()).Value, new HlcStamp(11, 0, Remote), false,
            new BabyProfilePayload(new string('M', 41), [], new DateOnly(2026, 8, 20), null));

        // Act
        var act = async () => await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([good, bad], 2, false, null), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<SqliteException>();
        (await CountAsync(db, "SELECT count(*) FROM care_entries;")).Should().Be(0);
        (await Store(db).GetCursorAsync(TestDatabase.Household, CancellationToken.None)).Cursor.Should().Be(0);
    }

    [Fact]
    public async Task ApplyPageAsync_TombstoneOfAnUnknownEntry_IsStoredDeleted()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = await db.NewIdAsync();

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id.Value, 10, deleted: true)], 3, false, null), CancellationToken.None);

        // Assert
        (await CountAsync(db, $"SELECT is_deleted FROM care_entries WHERE id = '{id.Value}';")).Should().Be(1);
        (await Store(db).GetCursorAsync(TestDatabase.Household, CancellationToken.None)).Should().Be(new SyncCursor(3, null));
    }

    [Fact]
    public async Task ApplyPageAsync_MiddleOfABootstrap_KeepsTheHeadForTheNextPage()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        await Store(db).ApplyPageAsync(Other, new PulledPage([], 500, true, 900), CancellationToken.None);

        // Assert
        (await Store(db).GetCursorAsync(Other, CancellationToken.None)).Should().Be(new SyncCursor(500, 900));
    }

    [Fact]
    public async Task ApplyPageAsync_FamilyTakenOffThePhoneWhileItsPageWasInFlight_AppliesNothingAndWritesNoCursor()
    {
        // Arrange: final review C1 (belt). The pull was answered, then a leave took the family off the phone; the page lands.
        await using var db = await TestDatabase.CreateAsync();
        var store = Store(db);
        await store.ApplyPageAsync(TestDatabase.Household, new PulledPage([], 100, false, null), CancellationToken.None);
        var since = await store.GetCursorAsync(TestDatabase.Household, CancellationToken.None);
        await new HouseholdMoves(db.Database, db.Time).DeleteHouseholdAsync(TestDatabase.Household, CancellationToken.None);
        var id = Guid.Parse("7a1c0000-0000-7000-8000-00000000e0c1");

        // Act
        await store.ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id, 10)], 150, false, null, since), CancellationToken.None);

        // Assert: a re-join pulls the family's whole log from 0.
        (await CountAsync(db, $"SELECT count(*) FROM sync_cursors WHERE household_id = '{TestDatabase.Household.Value}';")).Should().Be(0);
        (await CountAsync(db, $"SELECT count(*) FROM care_entries WHERE id = '{id}';")).Should().Be(0);
        (await store.GetCursorAsync(TestDatabase.Household, CancellationToken.None)).Should().Be(new SyncCursor(0, null));
    }

    [Fact]
    public async Task ApplyPageAsync_JoinedFamilysFirstPageBeforeItsSettings_IsAppliedWithItsCursor()
    {
        // Arrange: a family being joined has no settings on the phone until its settings record arrives, maybe pages later.
        await using var db = await TestDatabase.CreateAsync();
        var store = Store(db);
        var since = await store.GetCursorAsync(Other, CancellationToken.None);

        // Act
        await store.ApplyPageAsync(Other, new PulledPage([], 500, true, 900, since), CancellationToken.None);
        var next = await store.GetCursorAsync(Other, CancellationToken.None);
        await store.ApplyPageAsync(Other, new PulledPage([], 1000, true, 900, next), CancellationToken.None);

        // Assert
        (await store.GetCursorAsync(Other, CancellationToken.None)).Should().Be(new SyncCursor(1000, 900));
    }

    [Fact]
    public async Task ApplyPageAsync_HeadZeroBootstrapWithMorePages_KeepsHeadZeroNotASkip()
    {
        // Arrange: a phone that holds records pulls every page with head 0 (ruling 20).
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(db.Time.GetLocalNow());
        var store = Store(db);

        // Act
        await store.ApplyPageAsync(TestDatabase.Household, new PulledPage([], 500, true, 0), CancellationToken.None);

        // Assert
        (await store.GetCursorAsync(TestDatabase.Household, CancellationToken.None)).Should().Be(new SyncCursor(500, 0));
    }

    [Fact]
    public async Task GetCursorAsync_NoCursorButLocalEntries_AsksForEveryTombstone()
    {
        // Arrange: pushed before its first pull (ruling 20).
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Act
        var cursor = await Store(db).GetCursorAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        cursor.Should().Be(new SyncCursor(0, 0));
    }

    [Fact]
    public async Task GetCursorAsync_EmptyHousehold_LetsTheServerChooseTheHead()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var cursor = await Store(db).GetCursorAsync(Other, CancellationToken.None);

        // Assert
        cursor.Should().Be(new SyncCursor(0, null));
    }

    [Fact]
    public async Task CountsAsync_EntriesEditedAndRejected_CountsRecordsNotRows()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        entry.SetNote(Note.From("again").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Act
        var counts = await Store(db).CountsAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        counts.Should().Be(new SyncCounts(0, 2, 0));
    }

    private static async Task InsertPendingEntryAsync(TestDatabase db, Guid id, string household, long wall, string note, string versionDevice, bool deleted = false)
    {
        await using var connection = await db.OpenAsync();
        await connection.ExecuteAsync(
            $"""
            INSERT INTO care_entries (id, household_id, kind, subject_baby_id, is_session, start_utc_ms, start_offset_min, logged_by, source,
                note, details, version_wall_ms, version_counter, version_device, is_deleted)
            VALUES ('{id}', '{household}', 'diaper', '{TestDatabase.Baby.Value}', 0, 1000, 0, 'this-device', 'manual',
                '{note}', '{WetDetails}', {wall}, 0, '{versionDevice}', {(deleted ? 1 : 0)});
            INSERT INTO outbox (change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device)
            VALUES ('{(await db.NewIdAsync()).Value}', 'CareEntry', '{id}', '{(deleted ? "delete" : "upsert")}', {wall}, 0, '{Remote.Value}');
            """, CancellationToken.None);
    }

    [Fact]
    public async Task GetCursorAsync_EmptyFirstPullThenALocalEntry_StillAsksForEveryTombstone()
    {
        // Arrange: the empty first pull stored (0, null); the entry was logged and pushed afterwards.
        await using var db = await TestDatabase.CreateAsync();
        var store = Store(db);
        await store.ApplyPageAsync(TestDatabase.Household, new PulledPage([], 0, false, 0), CancellationToken.None);
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Act
        var cursor = await store.GetCursorAsync(TestDatabase.Household, CancellationToken.None);

        // Assert
        cursor.Should().Be(new SyncCursor(0, 0));
    }

    [Fact]
    public async Task ApplyPageAsync_PulledRecordWinsOverAPendingOne_DropsItsOutboxRowsAndRejection()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = (await db.NewIdAsync()).Value;
        await InsertPendingEntryAsync(db, id, TestDatabase.Household.Value.ToString(), 5, "mine", Remote.Value.ToString());
        await using (var connection = await db.OpenAsync())
            await connection.ExecuteAsync(
                $"INSERT INTO sync_rejections (entity_type, entity_id, household_id, reason, rejected_utc_ms) VALUES ('CareEntry', '{id}', '{TestDatabase.Household.Value}', 'x', 1);",
                CancellationToken.None);

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id, 10)], 1, false, null), CancellationToken.None);

        // Assert
        (await CountAsync(db, "SELECT count(*) FROM outbox;")).Should().Be(0);
        (await CountAsync(db, "SELECT count(*) FROM sync_rejections;")).Should().Be(0);
    }

    [Fact]
    public async Task ApplyPageAsync_PulledRecordLosesToAPendingOne_KeepsItsOutboxRows()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = (await db.NewIdAsync()).Value;
        await InsertPendingEntryAsync(db, id, TestDatabase.Household.Value.ToString(), 20, "mine", Remote.Value.ToString());

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id, 10)], 1, false, null), CancellationToken.None);

        // Assert
        (await CountAsync(db, "SELECT count(*) FROM outbox;")).Should().Be(1);
    }

    [Theory]
    [InlineData(20, 10, 1)]
    [InlineData(5, 10, 0)]
    public async Task ApplyPageAsync_PendingLocalDelete_IsKeptOnlyWhenNewer(long localWall, long remoteWall, long expectedDeleted)
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = (await db.NewIdAsync()).Value;
        await InsertPendingEntryAsync(db, id, TestDatabase.Household.Value.ToString(), localWall, "mine", Remote.Value.ToString(), deleted: true);

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id, remoteWall)], 1, false, null), CancellationToken.None);

        // Assert
        (await CountAsync(db, $"SELECT is_deleted FROM care_entries WHERE id = '{id}';")).Should().Be(expectedDeleted);
    }

    [Fact]
    public async Task NextBatchAsync_AnUnreadableGroupBeforeAGoodOne_IsDroppedAndTheBatchStillFills()
    {
        // Arrange: a stored version that cannot be parsed can never be sent.
        await using var db = await TestDatabase.CreateAsync();
        await InsertPendingEntryAsync(db, (await db.NewIdAsync()).Value, TestDatabase.Household.Value.ToString(), 5, "bad", "not-a-device");
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 1, CancellationToken.None);

        // Assert
        batch.Should().ContainSingle();
        (await CountAsync(db, "SELECT count(*) FROM outbox;")).Should().Be(1);
    }

    [Fact]
    public async Task ApplyPageAsync_IdOfARecordInAnotherHousehold_LeavesThatRecordAlone()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var id = (await db.NewIdAsync()).Value;
        await InsertPendingEntryAsync(db, id, Other.Value.ToString(), 5, "other", Remote.Value.ToString());

        // Act
        await Store(db).ApplyPageAsync(TestDatabase.Household, new PulledPage([PulledDiaper(id, 10)], 1, false, null), CancellationToken.None);

        // Assert
        (await (await db.OpenAsync()).ScalarAsync($"SELECT household_id FROM care_entries WHERE id = '{id}';", CancellationToken.None)).Should().Be(Other.Value.ToString());
        (await CountAsync(db, "SELECT count(*) FROM outbox;")).Should().Be(1);
    }

    [Theory]
    [InlineData(PushOutcome.Applied)]
    [InlineData(PushOutcome.Superseded)]
    public async Task ApplyResultsAsync_AppliedOrSuperseded_RemovesTheRowsAndClearsAnEarlierRejection(PushOutcome outcome)
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var entry = await db.DiaperAsync(db.Time.GetLocalNow());
        var store = Store(db);
        var batch = await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);
        await store.ApplyResultsAsync(TestDatabase.Household, batch, [new PushResult(batch[0].ChangeId, PushOutcome.Rejected, "x")], db.Time.GetUtcNow(), CancellationToken.None);
        entry.SetNote(Note.From("fixed").Value, await db.Device.NextAsync(CancellationToken.None));
        await db.Store.SaveAsync(entry, CancellationToken.None);
        var again = await store.NextBatchAsync(TestDatabase.Household, 200, CancellationToken.None);

        // Act
        await store.ApplyResultsAsync(TestDatabase.Household, again, [new PushResult(again[0].ChangeId, outcome, null)], db.Time.GetUtcNow(), CancellationToken.None);

        // Assert
        (await CountAsync(db, "SELECT count(*) FROM outbox;")).Should().Be(0);
        (await store.ListRejectionsAsync(TestDatabase.Household, CancellationToken.None)).Should().BeEmpty();
    }
    private static async Task InsertPendingAsync(TestDatabase db, string table, string columns, string values, string entityType, Guid id)
    {
        await db.ExecuteAsync(
            $"""
            INSERT INTO {table} ({columns}) VALUES ({values});
            INSERT INTO outbox (change_id, entity_type, entity_id, operation, version_wall_ms, version_counter, version_device)
            VALUES ('{(await db.NewIdAsync()).Value}', '{entityType}', '{id}', 'upsert', 5, 0, '{Remote.Value}');
            """);
    }

    [Fact]
    public async Task NextBatchAsync_ABabyWithAnUnreadableDate_IsDroppedAndTheBatchStillFills()
    {
        // Arrange: year 0 passes the table's date() check but no DateOnly can hold it (final review M9).
        await using var db = await TestDatabase.CreateAsync();
        var baby = (await db.NewIdAsync()).Value;
        await InsertPendingAsync(db, "baby_profiles", "id, household_id, name, nicknames, birth_date, version_wall_ms, version_counter, version_device",
            $"'{baby}', '{TestDatabase.Household.Value}', 'Maja', '[]', '0000-01-01', 5, 0, '{Remote.Value}'", SyncEntityTypes.BabyProfile, baby);
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 1, CancellationToken.None);

        // Assert
        batch.Should().ContainSingle().Which.EntityType.Should().Be(SyncEntityTypes.CareEntry);
        (await CountAsync(db, $"SELECT count(*) FROM outbox WHERE entity_id = '{baby}';")).Should().Be(0);
    }

    [Fact]
    public async Task NextBatchAsync_ADismissalNamingAnUnreadableEntry_IsDroppedAndTheBatchStillFills()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var dismissal = (await db.NewIdAsync()).Value;
        await InsertPendingAsync(db, "duplicate_dismissals", "id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device",
            $"'{dismissal}', '{TestDatabase.Household.Value}', 'bad-a', 'bad-b', 5, 0, '{Remote.Value}'", SyncEntityTypes.DuplicateDismissal, dismissal);
        await db.DiaperAsync(db.Time.GetLocalNow());

        // Act
        var batch = await Store(db).NextBatchAsync(TestDatabase.Household, 1, CancellationToken.None);

        // Assert
        batch.Should().ContainSingle().Which.EntityType.Should().Be(SyncEntityTypes.CareEntry);
        (await CountAsync(db, $"SELECT count(*) FROM outbox WHERE entity_id = '{dismissal}';")).Should().Be(0);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("(500, 900)", false)]
    [InlineData("(500, NULL)", true)]
    [InlineData("(0, NULL)", true)]
    public async Task HasPulledAsync_TheCursorRow_IsTrueOnlyForACompletedPull(string? cursor, bool expected)
    {
        // Arrange: no row (never pulled); a head (mid-bootstrap); no head (a pull completed, even an empty one).
        await using var db = await TestDatabase.CreateAsync();
        if (cursor is not null)
            await db.ExecuteAsync($"INSERT INTO sync_cursors (household_id, last_seq, bootstrap_head) VALUES ('{Other.Value}', {cursor.Trim('(', ')')});");

        // Act
        var pulled = await Store(db).HasPulledAsync(Other, CancellationToken.None);

        // Assert
        pulled.Should().Be(expected);
    }
}
