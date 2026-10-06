using Coo.Api.Application.Auth;
using Coo.Api.Application.Households;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Npgsql;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>Spec 011b §10.3–§10.4 against real PostgreSQL 17: the transaction, the locks, the reads.</summary>
[Collection(PostgresTests.Name)]
public sealed class HouseholdMembershipStoreTests(PostgresServer server)
{
    private static readonly HouseholdId Family = ServerDatabase.Household;
    private static readonly UserId Owner = new(MembersSchemaTests.Owner);
    private static readonly UserId Partner = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c2"));

    [Fact]
    public async Task RunLockedAsync_AddInvitationThenFindByHash_ReadsItBackWithTheInvitersName()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var now = db.Time.GetUtcNow();
        var invitation = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now);

        // Act
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(invitation, "hash-a", CancellationToken.None);
            return 0;
        }, CancellationToken.None);
        var found = await db.Invitations.FindByHashAsync("hash-a", CancellationToken.None);

        // Assert
        found!.InviterName.Should().Be("Andrzej");
        found.Invitation.Id.Should().Be(invitation.Id);
        found.Invitation.ExpiresAt.Should().Be(now.AddHours(72));
        found.Invitation.IsUsable(now).Should().BeTrue();
    }

    [Fact]
    public async Task RunLockedAsync_WorkThrows_RollsEverythingBack()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var invitation = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, db.Time.GetUtcNow());

        // Act
        var act = async () => await db.HouseholdStore.RunLockedAsync<int>([Family], async tx =>
        {
            await tx.AddInvitationAsync(invitation, "hash-b", CancellationToken.None);
            throw new InvalidOperationException("boom");
        }, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.ScalarAsync("SELECT count(*) FROM invitations;")).Should().Be(0L);
    }

    [Fact]
    public async Task RunLockedAsync_AMissingHousehold_IsNotLocked()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var missing = new HouseholdId(db.NewId());

        // Act
        var (family, gone) = await db.HouseholdStore.RunLockedAsync([missing, Family], tx =>
            Task.FromResult((tx.IsLocked(Family), tx.IsLocked(missing))), CancellationToken.None);

        // Assert
        family.Should().BeTrue();
        gone.Should().BeFalse();
    }

    [Fact]
    public async Task RunLockedAsync_HeldByAnotherTransaction_WaitsForIt()
    {
        // Arrange: two membership changes to one family serialise (spec §10.4).
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = db.HouseholdStore.RunLockedAsync([Family], async _ =>
        {
            entered.SetResult();
            await release.Task;
            return 1;
        }, CancellationToken.None);
        await entered.Task;

        // Act
        var second = db.HouseholdStore.RunLockedAsync([Family], _ => Task.FromResult(2), CancellationToken.None);
        await WaitForLockWaitersAsync(db, 1);
        var finishedEarly = second.IsCompleted;
        release.SetResult();

        // Assert
        finishedEarly.Should().BeFalse();
        (await first).Should().Be(1);
        (await second).Should().Be(2);
    }

    [Fact]
    public async Task Transaction_RevokeReactivateAndActiveReads_SeeOnlyActiveRows()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await AddUserAsync(db, Partner, "Kasia");
        var now = db.Time.GetUtcNow();

        // Act
        var (afterAdd, afterRevoke, lookup, afterReactivate) = await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddMembershipAsync(Membership.Create(Family, Partner, Role.Parent, now), CancellationToken.None);
            var added = (await tx.ActiveMembersAsync(Family, CancellationToken.None)).Count;
            await tx.RevokeMembershipAsync(Family, Partner, now.AddHours(1), CancellationToken.None);
            var revoked = (await tx.ActiveMembersAsync(Family, CancellationToken.None)).Count;
            var row = await tx.FindMembershipAsync(Family, Partner, CancellationToken.None);
            await tx.ReactivateMembershipAsync(Family, Partner, Role.Parent, now.AddHours(2), CancellationToken.None);
            var back = (await tx.ActiveMembershipsOfUserAsync(Partner, CancellationToken.None)).Single();
            return (added, revoked, row, back);
        }, CancellationToken.None);

        // Assert
        afterAdd.Should().Be(2);
        afterRevoke.Should().Be(1);
        lookup.IsRevoked.Should().BeTrue();
        afterReactivate.JoinedAt.Should().Be(now.AddHours(2));
        (await db.HouseholdStore.ListActiveMembersAsync(Family, CancellationToken.None)).Select(m => m.Name).Should().Equal("Andrzej", "Kasia");
    }

    [Fact]
    public async Task Transaction_WithdrawOpenInvitationsBy_LeavesOthersAndUsedOnes()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await AddUserAsync(db, Partner, "Kasia");
        var now = db.Time.GetUtcNow();
        var mine = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Partner, now);
        var used = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Partner, now);
        var theirs = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now);
        used.Accept(Owner, now);

        // Act
        var withdrawn = await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(mine, "m", CancellationToken.None);
            await tx.AddInvitationAsync(used, "u", CancellationToken.None);
            await tx.SaveInvitationAsync(used, CancellationToken.None);
            await tx.AddInvitationAsync(theirs, "t", CancellationToken.None);
            return await tx.WithdrawOpenInvitationsByAsync(Family, Partner, now.AddMinutes(1), CancellationToken.None);
        }, CancellationToken.None);

        // Assert
        withdrawn.Should().Be(1);
        (await db.HouseholdStore.ListOpenInvitationsAsync(Family, now.AddMinutes(2), CancellationToken.None)).Should().ContainSingle()
            .Which.CreatedBy.Should().Be(Owner);
    }

    [Fact]
    public async Task Transaction_HoldsAnyRecord_CountsSettingsAndTombstones()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var before = await db.HouseholdStore.RunLockedAsync([Family], tx => tx.HoldsAnyRecordAsync(Family, CancellationToken.None), CancellationToken.None);
        await db.ExecuteAsync(
            $$"""
            INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{{Family.Value}}', 'ml', 'Europe/Amsterdam', '{}', 1, 0, '{{ServerDatabase.Device.Value}}', true, 1);
            """);

        // Act
        var after = await db.HouseholdStore.RunLockedAsync([Family], tx => tx.HoldsAnyRecordAsync(Family, CancellationToken.None), CancellationToken.None);

        // Assert
        before.Should().BeFalse();
        after.Should().BeTrue();
    }

    [Fact]
    public async Task Transaction_TryCreateHouseholdWithATakenId_WritesNothing()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await AddUserAsync(db, Partner, "Kasia");

        // Act
        var created = await db.HouseholdStore.RunLockedAsync([], tx =>
            tx.TryCreateHouseholdAsync(Family, Partner, db.Time.GetUtcNow(), CancellationToken.None), CancellationToken.None);

        // Assert
        created.Should().BeFalse();
        (await db.ScalarAsync($"SELECT count(*) FROM memberships WHERE user_id = '{Partner.Value}';")).Should().Be(0L);
    }

    [Fact]
    public async Task Transaction_DeleteHousehold_TakesItsEntriesMembershipsAndInvitations()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var now = db.Time.GetUtcNow();
        await db.Store.SaveAsync(Domain.CareLog.CareEntry.Create(new Domain.Common.EntryId(db.NewId()), Family,
            Domain.CareLog.EntrySubject.Baby(ServerDatabase.Baby), new Domain.CareLog.Kinds.Diaper.DiaperDetails(Domain.CareLog.Kinds.Diaper.DiaperContents.Wet),
            Domain.CareLog.EntryTiming.Point(now), Domain.CareLog.PersonRef.User(Owner), Domain.CareLog.EntrySource.Manual, db.NextStamp(), now).Value,
            CancellationToken.None);

        // Act
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now), "d", CancellationToken.None);
            await tx.DeleteHouseholdAsync(Family, CancellationToken.None);
            return 0;
        }, CancellationToken.None);

        // Assert
        (await db.ScalarAsync(
            "SELECT (SELECT count(*) FROM households) + (SELECT count(*) FROM care_entries) + (SELECT count(*) FROM memberships) + (SELECT count(*) FROM invitations);"))
            .Should().Be(0L);
    }

    [Fact]
    public async Task RunLockedAsync_EmptyHouseholdList_IsAPlainTransactionThatCommitsAndRollsBack()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var fresh = new HouseholdId(db.NewId());
        var rolledBack = new HouseholdId(db.NewId());

        // Act
        var created = await db.HouseholdStore.RunLockedAsync([], tx =>
            tx.TryCreateHouseholdAsync(fresh, Owner, db.Time.GetUtcNow(), CancellationToken.None), CancellationToken.None);
        var act = async () => await db.HouseholdStore.RunLockedAsync<int>([], async tx =>
        {
            await tx.TryCreateHouseholdAsync(rolledBack, Owner, db.Time.GetUtcNow(), CancellationToken.None);
            throw new InvalidOperationException("boom");
        }, CancellationToken.None);

        // Assert
        created.Should().BeTrue();
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.ScalarAsync($"SELECT count(*) FROM households WHERE id = '{fresh.Value}';")).Should().Be(1L);
        (await db.ScalarAsync($"SELECT count(*) FROM households WHERE id = '{rolledBack.Value}';")).Should().Be(0L);
    }

    [Fact]
    public async Task AcceptAsync_TwoAcceptsOfOneCodeAtOnce_CreateExactlyOneMembership()
    {
        // Arrange: a third transaction holds the family's row lock, so both accepts queue on it before either decides.
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var second = new UserId(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c3"));
        await AddUserAsync(db, Partner, "Kasia");
        await AddUserAsync(db, second, "Marek");
        foreach (var user in new[] { Partner, second })
        {
            await db.Consents.RecordAsync(
                new ConsentRecord(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, db.Time.GetUtcNow(), null, null), CancellationToken.None);
        }

        const string code = "ABCDEFGH";
        var hash = AuthSecrets.Hash(code);
        var now = db.Time.GetUtcNow();
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now), hash, CancellationToken.None);
            return 0;
        }, CancellationToken.None);
        var accept = new AcceptInvitation(db.Invitations, db.HouseholdStore, db.Consents, db.Time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = db.HouseholdStore.RunLockedAsync([Family], async _ =>
        {
            held.SetResult();
            await release.Task;
            return 0;
        }, CancellationToken.None);
        await held.Task;

        // Act
        var first = accept.ExecuteAsync(Partner, code, CancellationToken.None);
        var other = accept.ExecuteAsync(second, code, CancellationToken.None);
        await WaitForLockWaitersAsync(db, 2);
        release.SetResult();
        await holder;
        var results = await Task.WhenAll(first, other);

        // Assert
        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Single(r => r.IsFailure).Error!.Code.Should().Be(Invitation.InvalidCode);
        (await db.ScalarAsync($"SELECT count(*) FROM memberships WHERE household_id = '{Family.Value}' AND role = 'parent';")).Should().Be(1L);
        (await db.ScalarAsync("SELECT count(*) FROM invitations WHERE accepted_at IS NOT NULL;")).Should().Be(1L);
    }

    [Fact]
    public async Task FindByHashAsync_UsedWithdrawnAndOrphanedInvitations_RehydrateEveryColumn()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await AddUserAsync(db, Partner, "Kasia");
        var now = db.Time.GetUtcNow();
        var used = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Caregiver, Owner, now);
        var withdrawn = Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now);
        used.Accept(Partner, now.AddMinutes(5));
        withdrawn.Withdraw(now.AddMinutes(6));

        // Act
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(used, "used", CancellationToken.None);
            await tx.SaveInvitationAsync(used, CancellationToken.None);
            await tx.AddInvitationAsync(withdrawn, "withdrawn", CancellationToken.None);
            await tx.SaveInvitationAsync(withdrawn, CancellationToken.None);
            return 0;
        }, CancellationToken.None);
        var foundUsed = (await db.Invitations.FindByHashAsync("used", CancellationToken.None))!.Invitation;
        var foundWithdrawn = (await db.Invitations.FindByHashAsync("withdrawn", CancellationToken.None))!.Invitation;
        var locked = await db.HouseholdStore.RunLockedAsync([Family], async tx => (
            await tx.FindInvitationForUpdateAsync("used", CancellationToken.None),
            await tx.FindInvitationForUpdateAsync(Family, withdrawn.Id, CancellationToken.None),
            await tx.FindInvitationForUpdateAsync("unknown", CancellationToken.None)), CancellationToken.None);
        await db.HouseholdStore.RunLockedAsync([], async tx =>
        {
            await tx.DeleteUserAsync(Partner, CancellationToken.None);
            return 0;
        }, CancellationToken.None);
        var orphaned = (await db.Invitations.FindByHashAsync("used", CancellationToken.None))!.Invitation;

        // Assert
        foundUsed.Role.Should().Be(Role.Caregiver);
        foundUsed.AcceptedBy.Should().Be(Partner);
        foundUsed.AcceptedAt.Should().Be(now.AddMinutes(5));
        foundUsed.RevokedAt.Should().BeNull();
        foundWithdrawn.RevokedAt.Should().Be(now.AddMinutes(6));
        foundWithdrawn.IsOpen.Should().BeFalse();
        locked.Item1!.Id.Should().Be(used.Id);
        locked.Item2!.Id.Should().Be(withdrawn.Id);
        locked.Item3.Should().BeNull();
        orphaned.AcceptedBy.Should().BeNull();
        orphaned.AcceptedAt.Should().Be(now.AddMinutes(5));
    }

    [Fact]
    public void InvitationId_GuidEmpty_IsRefused()
    {
        // Arrange
        var act = () => new InvitationId(Guid.Empty);

        // Act
        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task PurgeExpiredAsync_UsedAndWithdrawnToo_KeepsOnlyUnexpired()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var now = db.Time.GetUtcNow();
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.AddInvitationAsync(Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now.AddHours(-100)), "old", CancellationToken.None);
            await tx.AddInvitationAsync(Invitation.Create(new InvitationId(db.NewId()), Family, Role.Parent, Owner, now), "new", CancellationToken.None);
            return 0;
        }, CancellationToken.None);

        // Act
        var purged = await db.Invitations.PurgeExpiredAsync(now, CancellationToken.None);

        // Assert
        purged.Should().Be(1);
        (await db.Invitations.FindByHashAsync("new", CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteHouseholdAsync_EveryKindOfRecord_GoesInTheSameTransactionAndSparesOtherFamilies()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        var now = db.Time.GetUtcNow();
        var other = new HouseholdId(db.NewId());
        await db.ExecuteAsync($"INSERT INTO households (id, next_seq, created_at, created_by) VALUES ('{other.Value}', 1, now(), '{Owner.Value}');");
        string Seed(HouseholdId h) => $$"""
            INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{{db.NewId()}}', '{{h.Value}}', 'Maja', '{}', '2026-08-20', 1, 0, '{{ServerDatabase.Device.Value}}', false, 1);
            INSERT INTO household_settings (household_id, volume_unit, time_zone_id, hidden_kinds, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{{h.Value}}', 'ml', 'Europe/Amsterdam', '{}', 1, 0, '{{ServerDatabase.Device.Value}}', false, 2);
            INSERT INTO duplicate_dismissals (id, household_id, entry_a, entry_b, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{{db.NewId()}}', '{{h.Value}}', '{{db.NewId()}}', '{{db.NewId()}}', 1, 0, '{{ServerDatabase.Device.Value}}', false, 3);
            INSERT INTO sync_conflicts (household_id, change_id, entity_type, entity_id, winner_wall_ms, winner_counter, winner_device,
                loser_wall_ms, loser_counter, loser_device, recorded_at)
            VALUES ('{{h.Value}}', '{{db.NewId()}}', 'CareEntry', '{{db.NewId()}}', 2, 0, '{{ServerDatabase.Device.Value}}', 1, 0, '{{ServerDatabase.Device.Value}}', now());
            INSERT INTO invitations (id, household_id, role, code_hash, created_by, created_at, expires_at)
            VALUES ('{{db.NewId()}}', '{{h.Value}}', 'parent', 'seed-{{h.Value}}', '{{Owner.Value}}', now(), now() + interval '72 hours');
            """;
        await db.ExecuteAsync(Seed(Family) + Seed(other));
        await db.Store.SaveAsync(Domain.CareLog.CareEntry.Create(new Domain.Common.EntryId(db.NewId()), Family,
            Domain.CareLog.EntrySubject.Baby(ServerDatabase.Baby), new Domain.CareLog.Kinds.Diaper.DiaperDetails(Domain.CareLog.Kinds.Diaper.DiaperContents.Wet),
            Domain.CareLog.EntryTiming.Point(now), Domain.CareLog.PersonRef.User(Owner), Domain.CareLog.EntrySource.Manual, db.NextStamp(), now).Value,
            CancellationToken.None);
        const string CountFamily = """
            SELECT (SELECT count(*) FROM care_entries WHERE household_id = '{0}') + (SELECT count(*) FROM baby_profiles WHERE household_id = '{0}')
                + (SELECT count(*) FROM household_settings WHERE household_id = '{0}') + (SELECT count(*) FROM duplicate_dismissals WHERE household_id = '{0}')
                + (SELECT count(*) FROM sync_conflicts WHERE household_id = '{0}') + (SELECT count(*) FROM memberships WHERE household_id = '{0}')
                + (SELECT count(*) FROM invitations WHERE household_id = '{0}') + (SELECT count(*) FROM households WHERE id = '{0}');
            """;
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, CountFamily, Family.Value);

        // Act: a throw after the delete rolls all of it back; then the real delete.
        var act = async () => await db.HouseholdStore.RunLockedAsync<int>([Family], async tx =>
        {
            await tx.DeleteHouseholdAsync(Family, CancellationToken.None);
            throw new InvalidOperationException("boom");
        }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        var afterRollback = await db.ScalarAsync(sql);
        await db.HouseholdStore.RunLockedAsync([Family], async tx =>
        {
            await tx.DeleteHouseholdAsync(Family, CancellationToken.None);
            return 0;
        }, CancellationToken.None);

        // Assert
        afterRollback.Should().Be(8L);
        (await db.ScalarAsync(sql)).Should().Be(0L);
        (await db.ScalarAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, CountFamily, other.Value))).Should().Be(5L + 1L);
    }

    [Fact]
    public async Task ListBabyNamesAsync_LiveAndDeletedProfiles_ListsTheLiveOnes()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await MembersSchemaTests.SeedAsync(db);
        await db.ExecuteAsync(
            $$"""
            INSERT INTO baby_profiles (id, household_id, name, nicknames, birth_date, version_wall_ms, version_counter, version_device, is_deleted, seq)
            VALUES ('{{db.NewId()}}', '{{Family.Value}}', 'Maja', '{}', '2026-08-20', 1, 0, '{{ServerDatabase.Device.Value}}', false, 1),
                   ('{{db.NewId()}}', '{{Family.Value}}', 'Old', '{}', '2026-08-20', 1, 0, '{{ServerDatabase.Device.Value}}', true, 2);
            """);

        // Act
        var names = await db.Invitations.ListBabyNamesAsync(Family, CancellationToken.None);

        // Assert
        names.Should().Equal("Maja");
    }

    internal static async Task AddUserAsync(ServerDatabase db, UserId user, string name) => await db.ExecuteAsync(
        $"""
        INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "EmailConfirmed", "DisplayName", "PhoneNumberConfirmed",
            "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
        VALUES ('{user.Value}', 'google:{name}', 'GOOGLE:{name.ToUpperInvariant()}', '{name.ToLowerInvariant()}@example.com', true, '{name}', false, false, false, 0);
        """);

    /// <summary>Waits on PostgreSQL's own state, not on time.</summary>
    internal static async Task WaitForLockWaitersAsync(ServerDatabase db, int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((long)(await db.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock';"))! < count)
            deadline.Token.ThrowIfCancellationRequested();
    }
}
