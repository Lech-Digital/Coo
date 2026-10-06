using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Infrastructure.Persistence;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011b §4.1, §5.3, §6.2 and §7.1 on a real SQLite file.</summary>
public sealed class FamilyStoreTests
{
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly UserId Other = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));
    private static readonly HouseholdId Home = TestDatabase.Household;
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListAsync_BeforeTheFirstRefresh_IsNull()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var list = await new FamilyStore(db.Database).ListAsync(Me, CancellationToken.None);

        // Assert
        list.Should().BeNull("the cycle must tell 'not known yet' from 'no family at all' (§8.4)");
    }

    [Fact]
    public async Task ReplaceAsync_ThenList_ReturnsThemInJoinedOrderForThatAccountOnly()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);

        // Act
        await store.ReplaceAsync(Me, [
            new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2),
            new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)], CancellationToken.None);
        var mine = await store.ListAsync(Me, CancellationToken.None);
        var theirs = await store.ListAsync(Other, CancellationToken.None);

        // Assert
        mine.Should().Equal(new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1), new FamilyMembership(Joined, Role.Parent, Now.AddDays(1), "Andrzej", 2));
        theirs.Should().BeNull();
    }

    [Fact]
    public async Task ReplaceAsync_AnEmptyList_IsKnownAndEmpty()
    {
        // Arrange: after a removal from the only family (§8.4).
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);
        await store.ReplaceAsync(Me, [new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)], CancellationToken.None);

        // Act
        await store.ReplaceAsync(Me, [], CancellationToken.None);

        // Assert
        (await store.ListAsync(Me, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAsync_SignOut_ClearsTheListAndTheJoiningMarkerAndKeepsThePendingInvitation()
    {
        // Arrange: ruling 27.
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);
        var pending = new PendingInvitation(InvitationCode.Parse("ABCDEFGH").Value, Now);
        await store.ReplaceAsync(Me, [new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)], CancellationToken.None);
        await store.SetJoiningAsync(new JoiningMarker(Joined, Home, "Andrzej | O'Neil"), CancellationToken.None);
        await store.SetPendingInvitationAsync(pending, CancellationToken.None);

        // Act
        await store.ClearAsync(CancellationToken.None);

        // Assert
        (await store.ListAsync(Me, CancellationToken.None)).Should().BeNull();
        (await store.GetJoiningAsync(CancellationToken.None)).Should().BeNull();
        (await store.GetPendingInvitationAsync(CancellationToken.None)).Should().Be(pending);
    }

    [Fact]
    public async Task SetJoiningAsync_ANameWithSeparators_ReadsBackExactly()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);
        var marker = new JoiningMarker(Joined, Home, "Andrzej | O'Neil; Lech");

        // Act
        await store.SetJoiningAsync(marker, CancellationToken.None);
        var read = await store.GetJoiningAsync(CancellationToken.None);
        await store.SetJoiningAsync(null, CancellationToken.None);

        // Assert
        read.Should().Be(marker);
        (await store.GetJoiningAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteHouseholdAsync_ARemovedFamily_IsNoLongerNamedAnywhere()
    {
        // Arrange: Spec 011 §13.4's carry, spec §8.2: acknowledging clears it everywhere.
        await using var db = await TestDatabase.CreateAsync();
        var store = new FamilyStore(db.Database);
        await store.ReplaceAsync(Me, [new FamilyMembership(Joined, Role.Parent, Now, "Andrzej", 2), new FamilyMembership(Home, Role.Owner, Now, "Kasia", 1)], CancellationToken.None);
        await store.SetJoiningAsync(new JoiningMarker(Joined, Home, "Andrzej"), CancellationToken.None);
        await db.ExecuteAsync(
            $"""
            INSERT INTO app_state (key, value) VALUES ('softlock:{Joined.Value}', '1'), ('copied_from:{Home.Value}:{Joined.Value}', '1'),
                ('copied_from:{Joined.Value}:{Home.Value}', '1'), ('softlock:{Home.Value}', '1');
            """);

        // Act
        await new HouseholdMoves(db.Database, db.Time).DeleteHouseholdAsync(Joined, CancellationToken.None);

        // Assert
        (await db.ScalarAsync($"SELECT count(*) FROM app_state WHERE instr(key, '{Joined.Value}') > 0 OR value = '{Joined.Value}';")).Should().Be(0);
        (await db.ScalarAsync($"SELECT count(*) FROM app_state WHERE key = 'softlock:{Home.Value}';")).Should().Be(1, "another family's keys stay");
        (await store.GetJoiningAsync(CancellationToken.None)).Should().BeNull();
        (await store.ListAsync(Me, CancellationToken.None)).Should().ContainSingle().Which.Household.Should().Be(Home);
    }

    [Fact]
    public async Task WasCopiedAsync_MarkerForThePair_IsTrueOnlyForThatDirection()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        await db.ExecuteAsync($"INSERT INTO app_state (key, value) VALUES ('copied_from:{Home.Value}:{Joined.Value}', '1');");
        var store = new FamilyStore(db.Database);

        // Act
        var forward = await store.WasCopiedAsync(Home, Joined, CancellationToken.None);
        var back = await store.WasCopiedAsync(Joined, Home, CancellationToken.None);

        // Assert
        forward.Should().BeTrue();
        back.Should().BeFalse();
    }

    [Fact]
    public async Task Migrate_EmptyDatabase_CreatesFamilyMembershipsWithItsGuards()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        const string Insert = "INSERT INTO family_memberships (household_id, role, joined_utc_ms, owner_name, member_count) VALUES ('{0}', '{1}', 1, 'A', {2});";

        // Act
        var valid = async () => await db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, "x", "owner", 1));
        var badRole = async () => await db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, "y", "admin", 1));
        var noMembers = async () => await db.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Insert, "z", "owner", 0));

        // Assert: the table exists (a valid row goes in), and each guard is a constraint error, not a missing table.
        await valid.Should().NotThrowAsync();
        (await badRole.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>()).Which.SqliteErrorCode.Should().Be(19);
        (await noMembers.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>()).Which.SqliteErrorCode.Should().Be(19);
        (await db.ScalarAsync("SELECT count(*) FROM family_memberships;")).Should().Be(1);
    }

    [Fact]
    public async Task Migrate_0004DatabaseWithData_KeepsEveryRowThrough0005()
    {
        // Arrange: a 0.11.0 phone, at version 4, with entries, an outbox row, settings, a baby and app_state.
        await using var db = await TestDatabase.CreateAsync();
        await db.DiaperAsync(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2)));
        await db.DiaperAsync(new DateTimeOffset(2026, 10, 1, 9, 5, 0, TimeSpan.FromHours(2)));
        await db.ExecuteAsync("INSERT INTO app_state (key, value) VALUES ('active_household', 'h');");
        var tables = new[] { "care_entries", "outbox", "app_state", "sync_cursors", "sync_rejections", "baby_profiles", "household_settings", "duplicate_dismissals" };
        await db.ExecuteAsync("DROP TABLE family_memberships; PRAGMA user_version = 4;");
        var before = new List<long>();
        foreach (var table in tables)
            before.Add(await db.ScalarAsync($"SELECT count(*) FROM {table};"));

        // Act
        await db.StartupAsync("1.0.3");

        // Assert
        (await db.ScalarAsync("PRAGMA user_version;")).Should().Be(5);
        (await db.ScalarAsync("SELECT count(*) FROM family_memberships;")).Should().Be(0);
        var after = new List<long>();
        foreach (var table in tables)
            after.Add(await db.ScalarAsync($"SELECT count(*) FROM {table};"));
        after.Should().Equal(before);
        before[0].Should().Be(2);
    }
}
