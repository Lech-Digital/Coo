using Coo.Api.Application.Auth;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 011 §7.2 and §10 (Api.Infrastructure row), against a real PostgreSQL 17.</summary>
[Collection(PostgresTests.Name)]
public sealed class SyncStoreTests(PostgresServer server)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = ServerDatabase.Household;

    private static async Task<ServerDatabase> DatabaseAsync(PostgresServer server)
    {
        var db = await ServerDatabase.CreateAsync(server);
        await db.Households.EnsureAsync(Household, CancellationToken.None);
        return db;
    }

    private static CareEntryWrite Diaper(
        ServerDatabase db, Guid id, HlcStamp version, string details = """{"v":1,"contents":"wet"}""", HouseholdId? household = null) =>
        new(CareEntry.Create(new EntryId(id), household ?? Household, EntrySubject.Baby(ServerDatabase.Baby), new DiaperDetails(DiaperContents.Wet),
            EntryTiming.Point(T0), PersonRef.User(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"))), EntrySource.Manual,
            version, T0).Value, details);

    private static Task<ChangeResult> ApplyAsync(ServerDatabase db, SyncWrite write, Guid? changeId = null) =>
        db.Sync.ApplyAsync(Household, changeId ?? db.NewId(), write, T0, CancellationToken.None);

    [Fact]
    public async Task ApplyAsync_NewEntry_StoresItWithTheHouseholdsNextSeqAndRecordsTheResult()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        var change = db.NewId();

        // Act
        var result = await ApplyAsync(db, Diaper(db, id, db.NextStamp()), change);

        // Assert
        result.Should().Be(ChangeResult.Applied);
        (await db.ScalarAsync($"SELECT seq FROM care_entries WHERE id = '{id}';")).Should().Be(1L);
        (await db.Sync.FindResultAsync(change, CancellationToken.None)).Should().Be(ChangeResult.Applied);
    }

    [Fact]
    public async Task ApplyAsync_OneOfEachType_TakesConsecutiveSeqsFromOneCounter()
    {
        // Arrange: one counter per household serves all four tables (ADR 0002 §6).
        await using var db = await DatabaseAsync(server);
        var a = db.NewId();
        var b = db.NewId();
        var baby = BabyProfile.Create(new BabyId(db.NewId()), Household, "Maja", ["Majka"], new DateOnly(2026, 8, 20), null, db.NextStamp(), new DateOnly(2026, 10, 1)).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Ounces, "Europe/Amsterdam", db.NextStamp(), [EntryKindCode.From("pump").Value]).Value;

        // Act
        await ApplyAsync(db, Diaper(db, a, db.NextStamp()));
        await ApplyAsync(db, new BabyProfileWrite(baby));
        await ApplyAsync(db, new HouseholdSettingsWrite(settings));
        await ApplyAsync(db, new DismissalWrite(DuplicateDismissal.Create(new DismissalId(db.NewId()), Household,
            new EntryPair(new EntryId(a), new EntryId(b)), db.NextStamp())));

        // Assert
        (await db.ScalarAsync(
            """
            SELECT string_agg(seq::text, ',' ORDER BY seq) FROM (
                SELECT seq FROM care_entries UNION ALL SELECT seq FROM baby_profiles
                UNION ALL SELECT seq FROM household_settings UNION ALL SELECT seq FROM duplicate_dismissals) s;
            """)).Should().Be("1,2,3,4");
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{Household.Value}';")).Should().Be(5L);
    }

    [Fact]
    public async Task ApplyAsync_SupersededWrite_TakesNoSeqAndRecordsBothVersions()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        var older = db.NextStamp();
        var newer = db.NextStamp();
        await ApplyAsync(db, Diaper(db, id, newer));

        // Act
        var result = await ApplyAsync(db, Diaper(db, id, older));

        // Assert
        result.Should().Be(ChangeResult.Superseded);
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{Household.Value}';")).Should().Be(2L);
        (await db.ScalarAsync("SELECT winner_wall_ms::text || '>' || loser_wall_ms::text FROM sync_conflicts;"))
            .Should().Be($"{newer.WallClockMs}>{older.WallClockMs}");
    }

    [Fact]
    public async Task ApplyAsync_DeleteOfAnUnknownRecord_IsAppliedAndStoresNothing()
    {
        // Arrange: created and deleted while offline, never pushed before (Review Focus 4).
        await using var db = await DatabaseAsync(server);

        // Act
        var result = await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, db.NewId(), db.NextStamp()));

        // Assert
        result.Should().Be(ChangeResult.Applied);
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(0L);
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{Household.Value}';")).Should().Be(1L);
    }

    [Fact]
    public async Task ApplyAsync_TombstoneThenHigherEdit_RevivesTheRowWithANewSeq()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        await ApplyAsync(db, Diaper(db, id, db.NextStamp()));
        await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, id, db.NextStamp()));

        // Act
        var result = await ApplyAsync(db, Diaper(db, id, db.NextStamp()));

        // Assert
        result.Should().Be(ChangeResult.Applied);
        (await db.ScalarAsync($"SELECT is_deleted::text || ':' || seq::text FROM care_entries WHERE id = '{id}';")).Should().Be("false:3");
    }

    [Fact]
    public async Task ApplyAsync_IdOfAnotherHousehold_IsRejectedAndTheRowStays()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var other = new HouseholdId(db.NewId());
        await db.Households.EnsureAsync(other, CancellationToken.None);
        var id = db.NewId();
        await db.Sync.ApplyAsync(other, db.NewId(), Diaper(db, id, db.NextStamp(), household: other), T0, CancellationToken.None);

        // Act
        var result = await ApplyAsync(db, Diaper(db, id, db.NextStamp()));

        // Assert
        result.Should().Be(ChangeResult.Rejected(RejectionReasons.HouseholdMismatch));
        (await db.ScalarAsync($"SELECT household_id FROM care_entries WHERE id = '{id}';")).Should().Be(other.Value);
    }

    [Fact]
    public async Task ApplyAsync_HouseholdUnknownToTheServer_FailsWholeAndRecordsNothing()
    {
        // Arrange: the counter row is missing, so the transaction fails after the row lock and before the result.
        await using var db = await ServerDatabase.CreateAsync(server);
        var change = db.NewId();

        // Act
        var act = async () => await ApplyAsync(db, Diaper(db, db.NewId(), db.NextStamp()), change);

        // Assert
        await act.Should().ThrowAsync<Exception>();
        (await db.Sync.FindResultAsync(change, CancellationToken.None)).Should().BeNull();
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(0L);
    }

    [Fact]
    public async Task ApplyAsync_SameChangeTwiceAtOnce_StoresOneResultAndBothSeeIt()
    {
        // Arrange: the phone's retry races its own first attempt.
        await using var db = await DatabaseAsync(server);
        var change = db.NewId();
        var write = Diaper(db, db.NewId(), db.NextStamp());

        // Act
        var results = await Task.WhenAll(ApplyAsync(db, write, change), ApplyAsync(db, write, change));

        // Assert
        results.Should().OnlyContain(r => r == ChangeResult.Applied);
        (await db.ScalarAsync($"SELECT count(*) FROM applied_changes WHERE change_id = '{change}';")).Should().Be(1L);
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(1L);
    }

    [Fact]
    public async Task ApplyAsync_DetailsWithAPropertyThisBuildDoesNotKnow_KeepsIt()
    {
        // Arrange: a newer phone added "stoolColour"; the server's codec does not know it yet (ruling 6).
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();

        // Act
        await ApplyAsync(db, Diaper(db, id, db.NextStamp(), """{"v":1,"contents":"wet","stoolColour":"green"}"""));

        // Assert
        (await db.ScalarAsync($"SELECT details->>'stoolColour' FROM care_entries WHERE id = '{id}';")).Should().Be("green");
    }

    [Fact]
    public async Task RecordRejectionAsync_ThenAgain_KeepsTheFirstReason()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var change = db.NewId();
        await db.Sync.RecordRejectionAsync(change, "sync.stamp-in-future", T0, CancellationToken.None);

        // Act
        var second = await db.Sync.RecordRejectionAsync(change, "something-else", T0, CancellationToken.None);

        // Assert
        second.Should().Be(ChangeResult.Rejected("sync.stamp-in-future"));
    }

    [Fact]
    public async Task PullAsync_SevenRecordsInPagesOfThree_ReturnsThemInSeqOrderAcrossTables()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        for (var i = 0; i < 5; i++)
            await ApplyAsync(db, Diaper(db, db.NewId(), db.NextStamp()));
        await ApplyAsync(db, new BabyProfileWrite(BabyProfile.Create(new BabyId(db.NewId()), Household, "Maja", [], new DateOnly(2026, 8, 20), null, db.NextStamp(), new DateOnly(2026, 10, 1)).Value));
        await ApplyAsync(db, new HouseholdSettingsWrite(HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", db.NextStamp()).Value));

        // Act
        var first = await db.Sync.PullAsync(Household, 0, null, 3, CancellationToken.None);
        var second = await db.Sync.PullAsync(Household, first.Cursor, first.BootstrapHead, 3, CancellationToken.None);
        var third = await db.Sync.PullAsync(Household, second.Cursor, second.BootstrapHead, 3, CancellationToken.None);

        // Assert
        first.Records.Select(r => r.Seq).Should().Equal(1, 2, 3);
        first.HasMore.Should().BeTrue();
        first.BootstrapHead.Should().Be(7);
        second.Records.Select(r => r.Seq).Should().Equal(4, 5, 6);
        second.Records[^1].EntityType.Should().Be(SyncEntityTypes.BabyProfile);
        third.Records.Single().Payload.Should().BeOfType<HouseholdSettingsPayload>();
        third.HasMore.Should().BeFalse();
        third.Cursor.Should().Be(7);
    }

    [Fact]
    public async Task PullAsync_Bootstrap_SkipsTombstonesAtOrBelowTheHeadAndSendsOnesAbove()
    {
        // Arrange: A, B, C written; A deleted (seq 4, at the head) before the bootstrap starts.
        await using var db = await DatabaseAsync(server);
        var (a, b, c) = (db.NewId(), db.NewId(), db.NewId());
        await ApplyAsync(db, Diaper(db, a, db.NextStamp()));
        await ApplyAsync(db, Diaper(db, b, db.NextStamp()));
        await ApplyAsync(db, Diaper(db, c, db.NextStamp()));
        await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, a, db.NextStamp()));
        var first = await db.Sync.PullAsync(Household, 0, null, 2, CancellationToken.None);

        // Act: B is deleted while the bootstrap is half-way (its tombstone gets seq 5, above the head).
        await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, b, db.NextStamp()));
        var second = await db.Sync.PullAsync(Household, first.Cursor, first.BootstrapHead, 2, CancellationToken.None);

        // Assert
        first.BootstrapHead.Should().Be(4);
        first.Records.Select(r => r.EntityId).Should().Equal(b, c);
        first.HasMore.Should().BeTrue("A's tombstone, at seq 4, is still above the cursor");
        second.Records.Should().ContainSingle().Which.Should().Match<PulledRecord>(r => r.EntityId == b && r.IsDeleted);
        second.Cursor.Should().Be(5, "the cursor moves past the skipped tombstone of A");
        second.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task PullAsync_FromZeroWithHeadZero_SendsEveryTombstone()
    {
        // Arrange: a phone that already holds entries asks for every tombstone (ruling 20).
        await using var db = await DatabaseAsync(server);
        var a = db.NewId();
        await ApplyAsync(db, Diaper(db, a, db.NextStamp()));
        await ApplyAsync(db, new TombstoneWrite(SyncEntityTypes.CareEntry, a, db.NextStamp()));

        // Act
        var page = await db.Sync.PullAsync(Household, 0, 0, 10, CancellationToken.None);

        // Assert
        page.Records.Should().ContainSingle().Which.IsDeleted.Should().BeTrue();
        page.BootstrapHead.Should().Be(0);
    }

    [Fact]
    public async Task PullAsync_CareEntry_ReturnsTheStoredDetailsAndTheOffsetPeopleSaw()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        await ApplyAsync(db, Diaper(db, id, db.NextStamp()));

        // Act
        var page = await db.Sync.PullAsync(Household, 0, null, 10, CancellationToken.None);

        // Assert
        var payload = page.Records.Single().Payload.Should().BeOfType<CareEntryPayload>().Subject;
        payload.StartAt.Should().Be(T0);
        payload.StartAt.Offset.Should().Be(TimeSpan.FromHours(2));
        payload.DetailsJson.Should().Contain("\"contents\"");
        payload.LoggedBy.Should().Be("user:7a1c0000-0000-7000-8000-0000000000c1");
    }

    [Fact]
    public async Task PurgeAsync_ResultsAndConflictsOlderThanTheCutoff_Go()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        await ApplyAsync(db, Diaper(db, id, db.NextStamp()));
        var newer = db.NextStamp();
        await db.Sync.ApplyAsync(Household, db.NewId(), Diaper(db, id, newer), T0.AddDays(40), CancellationToken.None);
        await db.Sync.ApplyAsync(Household, db.NewId(), Diaper(db, id, newer), T0.AddDays(40), CancellationToken.None);   // superseded: a conflict at day 40

        // Act
        var deleted = await db.Sync.PurgeAsync(T0.AddDays(10), CancellationToken.None);

        // Assert
        deleted.Should().Be(1);
        (await db.ScalarAsync("SELECT count(*) FROM applied_changes;")).Should().Be(2L);
        (await db.ScalarAsync("SELECT count(*) FROM sync_conflicts;")).Should().Be(1L);
    }

    [Fact]
    public async Task FindAsync_Memberships_TellActiveRevokedAndNever()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var (active, revoked, never) = (new UserId(db.NewId()), new UserId(db.NewId()), new UserId(db.NewId()));
        await CreateAccountAsync(db, active, "sub-a", Household);
        await CreateAccountAsync(db, revoked, "sub-r", new HouseholdId(db.NewId()));
        await db.ExecuteAsync($"INSERT INTO memberships (household_id, user_id, role, joined_at, revoked_at) VALUES ('{Household.Value}', '{revoked.Value}', 'parent', now(), now());");

        // Act
        var a = await db.Memberships.FindAsync(Household, active, CancellationToken.None);
        var r = await db.Memberships.FindAsync(Household, revoked, CancellationToken.None);
        var n = await db.Memberships.FindAsync(Household, never, CancellationToken.None);

        // Assert
        a.Membership!.Role.Should().Be(Role.Owner);
        a.IsRevoked.Should().BeFalse();
        r.IsRevoked.Should().BeTrue();
        n.Should().Be(MembershipLookup.None);
    }

    [Fact]
    public async Task RecordAsync_SameConsentTwice_KeepsTheFirst()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var user = new UserId(db.NewId());
        await CreateAccountAsync(db, user, "sub-c", Household);
        await db.Consents.RecordAsync(new ConsentRecord(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, T0, "0.11.0", "pl"), CancellationToken.None);

        // Act
        await db.Consents.RecordAsync(new ConsentRecord(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, T0.AddDays(1), "0.11.1", "en"), CancellationToken.None);
        var found = await db.Consents.FindAsync(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, CancellationToken.None);

        // Assert
        found!.GivenAt.Should().Be(T0);
        found.Language.Should().Be("pl");
    }

    [Fact]
    public async Task ExecuteAsync_LastParent_RemovesTheSyncedRowsAndTheConsent()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        var user = new UserId(db.NewId());
        await CreateAccountAsync(db, user, "sub-d", Household);
        await db.Consents.RecordAsync(new ConsentRecord(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, T0, null, null), CancellationToken.None);
        await ApplyAsync(db, Diaper(db, db.NewId(), db.NextStamp()));
        await ApplyAsync(db, new HouseholdSettingsWrite(HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", db.NextStamp()).Value));

        // Act
        await new DeleteAccount(db.HouseholdStore).ExecuteAsync(user, confirmed: true, CancellationToken.None);

        // Assert
        (await db.ScalarAsync(
            "SELECT (SELECT count(*) FROM care_entries) + (SELECT count(*) FROM household_settings) + (SELECT count(*) FROM consents);"))
            .Should().Be(0L);
    }

    [Fact]
    public async Task ApplyAsync_TwoChangesCreatingTheSameRecordAtOnceWithOneStamp_AppliesOneAndSupersedesTheOther()
    {
        // Arrange: both read "no stored row" before either writes (PF1): the household's counter row is held, so both
        // pass the read and queue behind it.
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        var stamp = db.NextStamp();
        var (first, second) = (db.NewId(), db.NewId());
        Task<ChangeResult[]> both;
        await using (var holder = await HoldCounterAsync(db))
        {
            both = Task.WhenAll(ApplyAsync(db, Diaper(db, id, stamp), first), ApplyAsync(db, Diaper(db, id, stamp), second));
            await WaitForLockWaitersAsync(db, 2);

            // Act
            await holder.ReleaseAsync();
        }

        var results = await both;

        // Assert
        results.Should().BeEquivalentTo([ChangeResult.Applied, ChangeResult.Superseded]);
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(1L);
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{Household.Value}';")).Should().Be(2L, "the superseded write took no seq");
        (await db.ScalarAsync("SELECT count(*) FROM sync_conflicts;")).Should().Be(1L);
    }

    [Fact]
    public async Task ApplyAsync_LowerAndHigherCreatingTheSameRecordAtOnce_KeepsTheHigherWhicheverCommitsFirst()
    {
        // Arrange: the same race with two versions (PF1). Whichever order the lock gives them, the lower never lands on
        // top of the higher, and every applied write took exactly one seq.
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        var lower = db.NextStamp();
        var higher = db.NextStamp();
        Task<ChangeResult[]> both;
        await using (var holder = await HoldCounterAsync(db))
        {
            both = Task.WhenAll(
                ApplyAsync(db, Diaper(db, id, higher, """{"v":1,"contents":"dirty"}""")),
                ApplyAsync(db, Diaper(db, id, lower, """{"v":1,"contents":"wet"}""")));
            await WaitForLockWaitersAsync(db, 2);

            // Act
            await holder.ReleaseAsync();
        }

        var results = await both;

        // Assert
        results[0].Should().Be(ChangeResult.Applied);
        (await db.ScalarAsync($"SELECT version_wall_ms FROM care_entries WHERE id = '{id}';")).Should().Be(higher.WallClockMs);
        var applied = results.Count(r => r == ChangeResult.Applied);
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{Household.Value}';")).Should().Be(1L + applied);
    }

    [Fact]
    public async Task ApplyAsync_TwoHouseholdsCreatingTheSameIdAtOnce_StoresOneAndRejectsTheOther()
    {
        // Arrange: both read "no stored row", each behind its own household's counter row (PF1, the household guard).
        await using var db = await DatabaseAsync(server);
        var other = new HouseholdId(db.NewId());
        await db.Households.EnsureAsync(other, CancellationToken.None);
        var id = db.NewId();
        Task<ChangeResult[]> both;
        await using (var mine = await HoldCounterAsync(db))
        await using (var theirs = await HoldCounterAsync(db, other))
        {
            both = Task.WhenAll(
                ApplyAsync(db, Diaper(db, id, db.NextStamp())),
                db.Sync.ApplyAsync(other, db.NewId(), Diaper(db, id, db.NextStamp(), household: other), T0, CancellationToken.None));
            await WaitForLockWaitersAsync(db, 2);

            // Act
            await mine.ReleaseAsync();
            await theirs.ReleaseAsync();
        }

        var results = await both;

        // Assert
        results.Should().BeEquivalentTo([ChangeResult.Applied, ChangeResult.Rejected(RejectionReasons.HouseholdMismatch)]);
        var owner = (Guid)(await db.ScalarAsync($"SELECT household_id FROM care_entries WHERE id = '{id}';"))!;
        var winner = results[0] == ChangeResult.Applied ? Household : other;
        owner.Should().Be(winner.Value);
        (await db.ScalarAsync("SELECT sum(next_seq) FROM households;")).Should().Be(3m, "only the stored write took a seq");
    }

    [Fact]
    public async Task ApplyAsync_SameClockFromAnotherDevice_TheDeviceOrderDecides()
    {
        // Arrange: the stamp's own device is the tie-break (ADR 0002 §4), compared in RFC 4122 byte order by the rule
        // and by PostgreSQL alike; the store never assumes it is the pushing device.
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        var low = new DeviceId(Guid.Parse("01000000-0000-7000-8000-0000000000ff"));
        var high = new DeviceId(Guid.Parse("02000000-0000-7000-8000-000000000000"));
        await ApplyAsync(db, Diaper(db, id, new HlcStamp(1_790_000_100_000, 3, high)));

        // Act
        var result = await ApplyAsync(db, Diaper(db, id, new HlcStamp(1_790_000_100_000, 3, low)));

        // Assert
        result.Should().Be(ChangeResult.Superseded);
        (await db.ScalarAsync($"SELECT version_device FROM care_entries WHERE id = '{id}';")).Should().Be(high.Value);
    }

    [Fact]
    public async Task ApplyAsync_CareEntryOfAnotherHouseholdThanTheCallers_ThrowsAndStoresNothing()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var other = new HouseholdId(db.NewId());
        var id = db.NewId();

        // Act
        Func<Task> act = async () => await ApplyAsync(db, Diaper(db, id, db.NextStamp(), household: other));

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        (await db.ScalarAsync("SELECT count(*) FROM care_entries;")).Should().Be(0L);
    }

    [Fact]
    public async Task ApplyAsync_BabyProfileOfAnotherHouseholdThanTheCallers_Throws()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var other = new HouseholdId(db.NewId());
        var baby = BabyProfile.Create(new BabyId(db.NewId()), other, "Maja", [], new DateOnly(2026, 8, 20), null, db.NextStamp(), new DateOnly(2026, 10, 1)).Value;

        // Act
        Func<Task> act = async () => await ApplyAsync(db, new BabyProfileWrite(baby));

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        (await db.ScalarAsync("SELECT count(*) FROM baby_profiles;")).Should().Be(0L);
    }

    [Fact]
    public async Task ApplyAsync_DismissalOfAnotherHouseholdThanTheCallers_Throws()
    {
        // Arrange
        await using var db = await DatabaseAsync(server);
        var other = new HouseholdId(db.NewId());
        var dismissal = DuplicateDismissal.Create(new DismissalId(db.NewId()), other, new EntryPair(new EntryId(db.NewId()), new EntryId(db.NewId())), db.NextStamp());

        // Act
        Func<Task> act = async () => await ApplyAsync(db, new DismissalWrite(dismissal));

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        (await db.ScalarAsync("SELECT count(*) FROM duplicate_dismissals;")).Should().Be(0L);
    }

    [Fact]
    public async Task ApplyAsync_EditThatChangesTheKind_IsRejectedAsMalformedAndTheRowStays()
    {
        // Arrange: the kind is immutable once stored.
        await using var db = await DatabaseAsync(server);
        var id = db.NewId();
        await ApplyAsync(db, Diaper(db, id, db.NextStamp()));
        var asSleep = new CareEntryWrite(CareEntry.Create(new EntryId(id), Household, EntrySubject.Baby(ServerDatabase.Baby), new SleepDetails(),
            EntryTiming.Session(TimeRange.Create(T0, T0.AddMinutes(50)).Value), PersonRef.User(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"))),
            EntrySource.Manual, db.NextStamp(), T0).Value, """{"v":1}""");

        // Act
        var result = await ApplyAsync(db, asSleep);

        // Assert
        result.Should().Be(ChangeResult.Rejected(RejectionReasons.ChangeMalformed));
        (await db.ScalarAsync($"SELECT kind FROM care_entries WHERE id = '{id}';")).Should().Be("diaper");
        (await db.ScalarAsync($"SELECT next_seq FROM households WHERE id = '{Household.Value}';")).Should().Be(2L);
        (await db.ScalarAsync("SELECT count(*) FROM sync_conflicts;")).Should().Be(0L);
    }

    [Fact]
    public async Task ApplyAsync_ManyWritersEditingOneRecordAndCreatingOthers_AllFinishWithoutADeadlock()
    {
        // Arrange: the household counter lock is always taken before the row lock.
        await using var db = await DatabaseAsync(server);
        var shared = db.NewId();
        await ApplyAsync(db, Diaper(db, shared, db.NextStamp()));
        var writes = Enumerable.Range(0, 24)
            .Select(i => i % 2 == 0 ? Diaper(db, shared, db.NextStamp()) : Diaper(db, db.NewId(), db.NextStamp()))
            .ToList();

        // Act
        var results = await Task.WhenAll(writes.Select(w => ApplyAsync(db, w)));

        // Assert
        results.Should().OnlyContain(r => r.Outcome != ChangeOutcome.Rejected);
    }

    [Fact]
    public async Task ApplyAsync_ConcurrentWritesToAllFourTables_TakeOneGaplessRunOfSeqs()
    {
        // Arrange: every writer takes its seq from the one counter row, in its own transaction.
        await using var db = await DatabaseAsync(server);
        var writes = new List<SyncWrite>();
        for (var i = 0; i < 3; i++)
        {
            writes.Add(Diaper(db, db.NewId(), db.NextStamp()));
            writes.Add(new BabyProfileWrite(BabyProfile.Create(new BabyId(db.NewId()), Household, "Maja", [], new DateOnly(2026, 8, 20), null, db.NextStamp(), new DateOnly(2026, 10, 1)).Value));
            writes.Add(new DismissalWrite(DuplicateDismissal.Create(new DismissalId(db.NewId()), Household,
                new EntryPair(new EntryId(db.NewId()), new EntryId(db.NewId())), db.NextStamp())));
        }

        writes.Add(new HouseholdSettingsWrite(HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", db.NextStamp()).Value));

        // Act
        var results = await Task.WhenAll(writes.Select(w => ApplyAsync(db, w)));

        // Assert
        results.Should().OnlyContain(r => r == ChangeResult.Applied);
        (await db.ScalarAsync(
            """
            SELECT string_agg(seq::text, ',' ORDER BY seq) FROM (
                SELECT seq FROM care_entries UNION ALL SELECT seq FROM baby_profiles
                UNION ALL SELECT seq FROM household_settings UNION ALL SELECT seq FROM duplicate_dismissals) s;
            """)).Should().Be(string.Join(',', Enumerable.Range(1, writes.Count)));
    }

    [Fact]
    public async Task FindAsync_RevokedMembership_ReturnsTheMembershipMarkedRevoked()
    {
        // Arrange: the revoked answer names the membership it was (ADR 0002 §10), never (null, revoked).
        await using var db = await ServerDatabase.CreateAsync(server);
        var user = new UserId(db.NewId());
        await CreateAccountAsync(db, user, "sub-x", new HouseholdId(db.NewId()));
        await db.Households.EnsureAsync(Household, CancellationToken.None);
        await db.ExecuteAsync($"INSERT INTO memberships (household_id, user_id, role, joined_at, revoked_at) VALUES ('{Household.Value}', '{user.Value}', 'caregiver', now(), now());");

        // Act
        var lookup = await db.Memberships.FindAsync(Household, user, CancellationToken.None);

        // Assert
        lookup.IsRevoked.Should().BeTrue();
        lookup.Membership.Should().NotBeNull();
        lookup.Membership!.Role.Should().Be(Role.Caregiver);
        lookup.Membership.HouseholdId.Should().Be(Household);
        lookup.Membership.UserId.Should().Be(user);
    }

    /// <summary>Holds the household's counter row in a transaction of its own, so appliers queue behind it.</summary>
    private static async Task<CounterHold> HoldCounterAsync(ServerDatabase db, HouseholdId? household = null)
    {
        var connection = await db.DataSource.OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT next_seq FROM households WHERE id = @h FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("h", (household ?? Household).Value);
        await command.ExecuteScalarAsync();
        return new CounterHold(connection, transaction);
    }

    /// <summary>Waits on PostgreSQL's own state, not on time: until <paramref name="count"/> sessions wait for a lock.</summary>
    private static async Task WaitForLockWaitersAsync(ServerDatabase db, int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((long)(await db.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock';"))! < count)
            deadline.Token.ThrowIfCancellationRequested();
    }

    private static async Task CreateAccountAsync(ServerDatabase db, UserId user, string subject, HouseholdId household)
    {
        await using var scope = db.NewScope();
        await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAccountAsync(
            new NewAccount(user, subject, subject + "@example.com", "Parent " + subject, household, T0), CancellationToken.None);
    }

    private sealed class CounterHold(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public Task ReleaseAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
