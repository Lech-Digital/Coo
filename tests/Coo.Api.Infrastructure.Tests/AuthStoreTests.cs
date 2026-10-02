using Coo.Api.Application.Auth;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Api.Infrastructure.Tests;

/// <summary>The sign-in stores against real PostgreSQL 17 (Spec 010 §4.2, §7).</summary>
[Collection(PostgresTests.Name)]
public sealed class AuthStoreTests(PostgresServer server)
{
    private static readonly UserId User = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c1"));
    private static readonly UserId Partner = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0c2"));
    private static readonly HouseholdId PartnersHousehold = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0a2"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-00000000c0d2"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryConsumeAsync_FreshNonce_SucceedsOnceOnly()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Nonces.AddAsync("hash-1", T0, CancellationToken.None);

        // Act
        var first = await db.Nonces.TryConsumeAsync("hash-1", T0.AddMinutes(-5), T0.AddMinutes(1), CancellationToken.None);
        var second = await db.Nonces.TryConsumeAsync("hash-1", T0.AddMinutes(-5), T0.AddMinutes(2), CancellationToken.None);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [Fact]
    public async Task TryConsumeAsync_IssuedBeforeTheWindow_Fails()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Nonces.AddAsync("hash-1", T0, CancellationToken.None);

        // Act
        var consumed = await db.Nonces.TryConsumeAsync("hash-1", T0.AddSeconds(1), T0.AddMinutes(6), CancellationToken.None);

        // Assert
        consumed.Should().BeFalse();
        (await db.ScalarAsync("SELECT consumed_at IS NULL FROM auth_nonces;")).Should().Be(true);
    }

    [Fact]
    public async Task DeleteIssuedBeforeAsync_OldAndNewRows_DeletesOnlyTheOld()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await db.Nonces.AddAsync("old", T0, CancellationToken.None);
        await db.Nonces.AddAsync("new", T0.AddDays(2), CancellationToken.None);

        // Act
        var deleted = await db.Nonces.DeleteIssuedBeforeAsync(T0.AddDays(1), CancellationToken.None);

        // Assert
        deleted.Should().Be(1);
        (await db.ScalarAsync("SELECT string_agg(nonce_hash, ',') FROM auth_nonces;")).Should().Be("new");
    }

    [Fact]
    public async Task FindByRefreshHashAsync_AfterRotation_FindsTheOldHashAsPrevious()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);

        // Act
        var rotated = await db.Sessions.RotateAsync(session.Id, "h1", "h2", T0.AddMinutes(20), CancellationToken.None);
        var current = await db.Sessions.FindByRefreshHashAsync("h2", CancellationToken.None);
        var previous = await db.Sessions.FindByRefreshHashAsync("h1", CancellationToken.None);

        // Assert
        rotated.Should().BeTrue();
        current!.Match.Should().Be(RefreshMatch.Current);
        current.Session.LastUsedAt.Should().Be(T0.AddMinutes(20));
        previous!.Match.Should().Be(RefreshMatch.Previous);
        previous.Session.Id.Should().Be(session.Id);
        (await db.Sessions.FindByRefreshHashAsync("h0", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task FindByRefreshHashAsync_PreviousHash_ReportsTheCurrentHash()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);
        await db.Sessions.RotateAsync(session.Id, "h1", "h2", T0.AddMinutes(20), CancellationToken.None);

        // Act
        var previous = await db.Sessions.FindByRefreshHashAsync("h1", CancellationToken.None);

        // Assert
        previous!.Match.Should().Be(RefreshMatch.Previous);
        previous.CurrentHash.Should().Be("h2");
    }

    [Fact]
    public async Task RotateAsync_StaleExpectedHash_ChangesNothing()
    {
        // Arrange: two refreshes raced; the first one won.
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);
        await db.Sessions.RotateAsync(session.Id, "h1", "h2", T0.AddMinutes(1), CancellationToken.None);

        // Act
        var rotated = await db.Sessions.RotateAsync(session.Id, "h1", "h3", T0.AddMinutes(2), CancellationToken.None);

        // Assert
        rotated.Should().BeFalse();
        (await db.Sessions.FindByRefreshHashAsync("h2", CancellationToken.None))!.Match.Should().Be(RefreshMatch.Current);
        (await db.Sessions.FindByRefreshHashAsync("h3", CancellationToken.None)).Should().BeNull();
        (await db.Sessions.IsActiveAsync(session.Id, CancellationToken.None)).Should().BeTrue("losing the race is not theft");
    }

    [Fact]
    public async Task RotateAsync_RevokedSession_ChangesNothing()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);
        await db.Sessions.RevokeAsync(session.Id, T0.AddMinutes(1), CancellationToken.None);

        // Act
        var rotated = await db.Sessions.RotateAsync(session.Id, "h1", "h2", T0.AddMinutes(2), CancellationToken.None);

        // Assert
        rotated.Should().BeFalse();
        (await db.Sessions.FindByRefreshHashAsync("h2", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_SameUserAndDevice_ReplacesTheSession()
    {
        // Arrange: signed out on the phone, then signed in again.
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var old = Session(User, Phone);
        await db.Sessions.CreateAsync(old, "h1", CancellationToken.None);
        await db.Sessions.RevokeAsync(old.Id, T0.AddMinutes(5), CancellationToken.None);
        var fresh = Session(User, Phone, db.NewId());

        // Act
        await db.Sessions.CreateAsync(fresh, "h9", CancellationToken.None);

        // Assert
        var sessions = await db.Sessions.ListForUserAsync(User, CancellationToken.None);
        sessions.Should().ContainSingle().Which.Should().Be(fresh);
        (await db.Sessions.IsActiveAsync(old.Id, CancellationToken.None)).Should().BeFalse();
        (await db.Sessions.IsActiveAsync(fresh.Id, CancellationToken.None)).Should().BeTrue();
        (await db.Sessions.FindByRefreshHashAsync("h1", CancellationToken.None)).Should().BeNull();
        (await db.Sessions.FindByRefreshHashAsync("h9", CancellationToken.None))!.Match.Should().Be(RefreshMatch.Current);
        (await db.Sessions.ExistsForDeviceAsync(User, Phone, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_SameUserAndDeviceStillActive_ReplacesTheSessionAndItsPreviousHash()
    {
        // Arrange: signed in again on the phone without signing out, after a rotation.
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var old = Session(User, Phone);
        await db.Sessions.CreateAsync(old, "h1", CancellationToken.None);
        await db.Sessions.RotateAsync(old.Id, "h1", "h2", T0.AddMinutes(1), CancellationToken.None);
        var fresh = Session(User, Phone, db.NewId()) with { CreatedAt = T0.AddHours(1), LastUsedAt = T0.AddHours(1) };

        // Act
        await db.Sessions.CreateAsync(fresh, "h9", CancellationToken.None);

        // Assert
        (await db.Sessions.ListForUserAsync(User, CancellationToken.None)).Should().ContainSingle().Which.Should().Be(fresh);
        (await db.Sessions.IsActiveAsync(old.Id, CancellationToken.None)).Should().BeFalse();
        (await db.Sessions.FindByRefreshHashAsync("h1", CancellationToken.None)).Should().BeNull();
        (await db.Sessions.FindByRefreshHashAsync("h2", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ExistsForDeviceAsync_RevokedSession_IsTrue()
    {
        // Arrange: a phone that signed out is still the same phone.
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);
        await db.Sessions.RevokeAsync(session.Id, T0.AddMinutes(5), CancellationToken.None);

        // Act
        var exists = await db.Sessions.ExistsForDeviceAsync(User, Phone, CancellationToken.None);

        // Assert
        exists.Should().BeTrue();
        (await db.Sessions.ExistsForDeviceAsync(User, ServerDatabase.Device, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_Session_IsNoLongerActiveButStillListed()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        var session = Session(User, Phone);
        await db.Sessions.CreateAsync(session, "h1", CancellationToken.None);

        // Act
        await db.Sessions.RevokeAsync(session.Id, T0.AddMinutes(5), CancellationToken.None);

        // Assert
        (await db.Sessions.IsActiveAsync(session.Id, CancellationToken.None)).Should().BeFalse();
        (await db.Sessions.ListForUserAsync(User, CancellationToken.None)).Should().ContainSingle()
            .Which.RevokedAt.Should().Be(T0.AddMinutes(5));
        (await db.ScalarAsync("SELECT count(*) FROM device_sessions;")).Should().Be(1L, "a revoked session keeps its row");
    }

    [Fact]
    public async Task CreateAccountAsync_NewUser_StoresTheUserWithoutAPasswordItsGoogleLoginTheHouseholdAndTheOwner()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);

        // Assert
        await using var scope = db.NewScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        (await accounts.FindByGoogleSubjectAsync("sub-1", CancellationToken.None)).Should().Be(new UserAccount(User, "sub-1@example.com", "Parent sub-1"));
        (await accounts.HouseholdExistsAsync(ServerDatabase.Household, CancellationToken.None)).Should().BeTrue();
        var membership = (await accounts.ListMembershipsOfUserAsync(User, CancellationToken.None)).Should().ContainSingle().Subject;
        membership.HouseholdId.Should().Be(ServerDatabase.Household);
        membership.Role.Should().Be(Coo.Domain.Households.Role.Owner);
        (await db.ScalarAsync($"""SELECT "PasswordHash" IS NULL FROM "AspNetUsers" WHERE "Id" = '{User.Value}';""")).Should().Be(true);
        (await db.ScalarAsync("""SELECT "LoginProvider" || ':' || "ProviderKey" FROM "AspNetUserLogins";""")).Should().Be("Google:sub-1");
        (await db.ScalarAsync($"SELECT created_by FROM households WHERE id = '{ServerDatabase.Household.Value}';")).Should().Be(User.Value);
        (await db.ScalarAsync($"""SELECT "UserName" FROM "AspNetUsers" WHERE "Id" = '{User.Value}';""")).Should().Be("google:sub-1");
    }

    [Fact]
    public async Task CreateAccountAsync_TwoUsersWithTheSameEmail_StoresBoth()
    {
        // Arrange: the user name is per Google account, never the email.
        await using var db = await ServerDatabase.CreateAsync(server);
        await using (var scope = db.NewScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAccountAsync(
                new NewAccount(User, "sub-1", "shared@example.com", "One", ServerDatabase.Household, T0), CancellationToken.None);
        }

        // Act
        await using (var scope = db.NewScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAccountAsync(
                new NewAccount(Partner, "sub-2", "shared@example.com", "Two", PartnersHousehold, T0), CancellationToken.None);
        }

        // Assert
        (await db.ScalarAsync("""SELECT count(*) FROM "AspNetUsers";""")).Should().Be(2L);
    }

    [Fact]
    public async Task CreateAccountAsync_GoogleSubjectAlreadyHasAnAccount_ThrowsAndLeavesNoPartialUser()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);

        // Act
        Func<Task> act = async () => await CreateAccountAsync(db, Partner, "sub-1", PartnersHousehold);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.ScalarAsync("""SELECT string_agg("Id"::text, ',') FROM "AspNetUsers";""")).Should().Be(User.Value.ToString());
        (await db.ScalarAsync("SELECT count(*) FROM households;")).Should().Be(1L);
        (await db.ScalarAsync("SELECT count(*) FROM memberships;")).Should().Be(1L);
    }

    [Fact]
    public async Task CreateAccountAsync_ConcurrentFirstSignInsForOneGoogleSubject_CreateOneUserWithItsLogin()
    {
        // Arrange: the same person signs in on two phones at the same moment; neither request found an account.
        await using var db = await ServerDatabase.CreateAsync(server);

        // Act
        var results = await Task.WhenAll(
            Attempt(() => CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household)),
            Attempt(() => CreateAccountAsync(db, Partner, "sub-1", PartnersHousehold)));

        // Assert
        results.Count(failure => failure is null).Should().Be(1);
        results.Single(failure => failure is not null).Should().BeOfType<InvalidOperationException>();
        (await db.ScalarAsync("""SELECT count(*) FROM "AspNetUsers";""")).Should().Be(1L);
        (await db.ScalarAsync("""SELECT count(*) FROM "AspNetUsers" u JOIN "AspNetUserLogins" l ON l."UserId" = u."Id";""")).Should().Be(1L);
        (await db.ScalarAsync("SELECT count(*) FROM households;")).Should().Be(1L);
        (await db.ScalarAsync("SELECT count(*) FROM memberships;")).Should().Be(1L);
    }

    [Fact]
    public async Task DeleteAccountAsync_SoleOwner_RemovesTheUserLoginsSessionsMembershipHouseholdAndCareEntries()
    {
        // Arrange
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        await db.Sessions.CreateAsync(Session(User, Phone), "h1", CancellationToken.None);
        var now = db.Time.GetUtcNow();
        var entry = CareEntry.Create(new EntryId(db.NewId()), ServerDatabase.Household, EntrySubject.Baby(ServerDatabase.Baby),
            new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(now), PersonRef.ThisDevicesPerson, EntrySource.Manual,
            db.NextStamp(), now).Value;
        await db.Store.SaveAsync(entry, CancellationToken.None);

        // Act
        await using (var scope = db.NewScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAccountStore>().DeleteAccountAsync(
                User, new AccountDeletionPlan([ServerDatabase.Household], []), CancellationToken.None);
        }

        // Assert
        foreach (var table in new[] { "\"AspNetUsers\"", "\"AspNetUserLogins\"", "device_sessions", "memberships", "households", "care_entries" })
            (await db.ScalarAsync($"SELECT count(*) FROM {table};")).Should().Be(0L, $"{table} must be empty");
    }

    [Fact]
    public async Task DeleteAccountAsync_OwnershipTransfer_PromotesThePartnerAndKeepsTheHousehold()
    {
        // Arrange: a partner who is a Parent in the owner's household (possible from 11b).
        await using var db = await ServerDatabase.CreateAsync(server);
        await CreateAccountAsync(db, User, "sub-1", ServerDatabase.Household);
        await CreateAccountAsync(db, Partner, "sub-2", PartnersHousehold);
        await db.ExecuteAsync(
            $"INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{ServerDatabase.Household.Value}', '{Partner.Value}', 'parent', now());");

        // Act
        await using (var scope = db.NewScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAccountStore>().DeleteAccountAsync(
                User, new AccountDeletionPlan([], [new OwnershipTransfer(ServerDatabase.Household, Partner)]), CancellationToken.None);
        }

        // Assert
        (await db.ScalarAsync($"SELECT string_agg(user_id::text || ':' || role, ',') FROM memberships WHERE household_id = '{ServerDatabase.Household.Value}';"))
            .Should().Be($"{Partner.Value}:owner");
        (await db.ScalarAsync($"SELECT created_by IS NULL FROM households WHERE id = '{ServerDatabase.Household.Value}';")).Should().Be(true);
    }

    private static async Task<Exception?> Attempt(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return e;
        }
    }

    private static DeviceSession Session(UserId user, DeviceId device, Guid? id = null) =>
        new(new DeviceSessionId(id ?? Guid.Parse("7a1c0000-0000-7000-8000-00000000c0f1")), user, device, "Pixel 10", T0, T0, null);

    private static async Task CreateAccountAsync(ServerDatabase db, UserId user, string subject, HouseholdId household)
    {
        await using var scope = db.NewScope();
        await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAccountAsync(
            new NewAccount(user, subject, subject + "@example.com", "Parent " + subject, household, T0), CancellationToken.None);
    }
}
