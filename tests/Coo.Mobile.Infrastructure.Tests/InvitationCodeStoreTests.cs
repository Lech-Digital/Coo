using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Infrastructure.Persistence;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Walk fix F3: the codes of the invitations this phone made, on a real SQLite file.</summary>
public sealed class InvitationCodeStoreTests
{
    private static readonly HouseholdId Home = TestDatabase.Household;
    private static readonly HouseholdId Joined = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a2"));
    private static readonly Guid First = Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1");
    private static readonly Guid Second = Guid.Parse("7a1c0000-0000-7000-8000-0000000000e2");
    private static readonly InvitationCode Kahn = InvitationCode.Parse("KAHNZ8FW").Value;
    private static readonly InvitationCode Other = InvitationCode.Parse("ABCDEFGH").Value;

    [Fact]
    public async Task RememberAsync_ThenList_ReturnsTheCodesOfThatFamilyOnly()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var store = new InvitationCodeStore(db.Database);

        // Act
        await store.RememberAsync(Home, First, Kahn, CancellationToken.None);
        await store.RememberAsync(Joined, Second, Other, CancellationToken.None);
        var home = await store.ListAsync(Home, CancellationToken.None);

        // Assert
        home.Should().Equal(new Dictionary<Guid, InvitationCode> { [First] = Kahn });
    }

    [Fact]
    public async Task KeepOnlyAsync_AnInvitationIsGone_ForgetsItsCodeAndKeepsTheOthers()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();
        var store = new InvitationCodeStore(db.Database);
        await store.RememberAsync(Home, First, Kahn, CancellationToken.None);
        await store.RememberAsync(Home, Second, Other, CancellationToken.None);
        await store.RememberAsync(Joined, First, Kahn, CancellationToken.None);

        // Act
        await store.KeepOnlyAsync(Home, [Second], CancellationToken.None);

        // Assert
        (await store.ListAsync(Home, CancellationToken.None)).Should().Equal(new Dictionary<Guid, InvitationCode> { [Second] = Other });
        (await store.ListAsync(Joined, CancellationToken.None)).Should().ContainKey(First, "another family's codes are not touched");
    }

    [Fact]
    public async Task ListAsync_NothingRemembered_IsEmpty()
    {
        // Arrange
        await using var db = await TestDatabase.CreateAsync();

        // Act
        var codes = await new InvitationCodeStore(db.Database).ListAsync(Home, CancellationToken.None);

        // Assert
        codes.Should().BeEmpty();
    }
}
