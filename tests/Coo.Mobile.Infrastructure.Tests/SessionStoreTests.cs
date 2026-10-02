using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Infrastructure.Auth;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>The session as one SecureStorage value (Spec 010 §3.3). Unreadable is "signed out", never a crash.</summary>
public sealed class SessionStoreTests
{
    private static readonly StoredSession Session = new(
        "access-1", new DateTimeOffset(2026, 10, 2, 12, 15, 0, TimeSpan.Zero), "refresh-1",
        new AccountProfile(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "andrzej@example.com"),
        new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1")), JoinPending: true);

    private readonly MemorySecrets _secrets = new();

    [Fact]
    public async Task SaveAsyncThenGetAsync_Session_RoundTrips()
    {
        // Arrange
        var store = new SessionStore(_secrets);

        // Act
        await store.SaveAsync(Session, CancellationToken.None);
        var loaded = await store.GetAsync(CancellationToken.None);

        // Assert
        loaded.Should().Be(Session);
    }

    [Fact]
    public async Task SaveAsync_NameWithTheSeparator_IsStoredReadably()
    {
        // Arrange
        var store = new SessionStore(_secrets);
        var odd = Session with { Profile = Session.Profile with { Name = "A\u001fB" } };

        // Act
        await store.SaveAsync(odd, CancellationToken.None);
        var loaded = await store.GetAsync(CancellationToken.None);

        // Assert
        loaded!.Profile.Name.Should().Be("A B");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("2\u001fa\u001f0\u001fr\u001f7a1c0000-0000-7000-8000-0000000000c1\u001fn\u001fe\u001f7a1c0000-0000-7000-8000-0000000000a1\u001f0")]
    [InlineData("1\u001fa\u001f0\u001fr\u001fnot-a-guid\u001fn\u001fe\u001f7a1c0000-0000-7000-8000-0000000000a1\u001f0")]
    [InlineData("1\u001f\u001f0\u001fr\u001f7a1c0000-0000-7000-8000-0000000000c1\u001fn\u001fe\u001f7a1c0000-0000-7000-8000-0000000000a1\u001f0")]
    public async Task GetAsync_UnreadableValue_ReturnsNull(string stored)
    {
        // Arrange: a value from a future version, a Keystore restored badly, a half-written value.
        _secrets.Values[SessionStore.Key] = stored;
        var store = new SessionStore(_secrets);

        // Act
        var loaded = await store.GetAsync(CancellationToken.None);

        // Assert
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task ClearAsync_StoredSession_RemovesIt()
    {
        // Arrange
        var store = new SessionStore(_secrets);
        await store.SaveAsync(Session, CancellationToken.None);

        // Act
        await store.ClearAsync(CancellationToken.None);

        // Assert
        _secrets.Values.Should().BeEmpty();
    }
}
