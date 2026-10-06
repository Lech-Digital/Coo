using Coo.Api.Application.Auth;
using Coo.Api.Application.Households;
using Coo.Contracts.Households;
using Coo.Domain.Households;
using static Coo.Api.Application.Tests.HouseholdKit;

namespace Coo.Api.Application.Tests;

/// <summary>Spec 011b §3, §5 and §10.1 (Api.Application row): inviting, withdrawing, previewing, accepting.</summary>
public sealed class InvitationUseCaseTests
{
    private readonly HouseholdKit _kit = new();

    public InvitationUseCaseTests() => _kit.Store.AddFamily(Family, Owner, Start.AddDays(-30));

    [Fact]
    public async Task ExecuteAsync_ParentRole_CreatesAnInvitationStoredOnlyByHash()
    {
        // Act
        var created = await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Assert
        created.Value.Code.Value.Should().Be("ABCDEFGH", "the test's randomness counts 0, 1, 2, …");
        created.Value.ExpiresAt.Should().Be(Start.AddHours(72));
        var row = _kit.Store.Invitations.Should().ContainSingle().Subject;
        row.Hash.Should().Be(AuthSecrets.Hash("ABCDEFGH"));
        row.Invitation.Role.Should().Be(Role.Parent);
        row.Invitation.CreatedBy.Should().Be(Owner);
        _kit.Store.LockedSets.Should().ContainSingle().Which.Should().Equal(Family);
    }

    [Theory]
    [InlineData("caregiver")]
    [InlineData("owner")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ExecuteAsync_AnyRoleButParent_IsRoleUnsupported(string? role)
    {
        // Act
        var created = await _kit.Create().ExecuteAsync(Owner, Family, role, CancellationToken.None);

        // Assert
        created.Error!.Code.Should().Be(HouseholdErrors.InvitationRoleUnsupported);
        _kit.Store.Invitations.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_FiveOpenInvitations_IsTooMany()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
            (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).IsSuccess.Should().BeTrue();

        // Act
        var sixth = await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Assert
        sixth.Error!.Code.Should().Be(HouseholdErrors.InvitationTooMany);
        _kit.Store.Invitations.Should().HaveCount(5);
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredOnesDoNotCount_CreatesASixth()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
            await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromHours(72));

        // Act
        var created = await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None);

        // Assert
        created.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CaregiverInvites_IsForbidden()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1));

        // Act
        var created = await _kit.Create().ExecuteAsync(Nanny, Family, "parent", CancellationToken.None);

        // Assert
        created.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_NotAMemberOrGone_IsForbidden()
    {
        // Act
        var stranger = await _kit.Create().ExecuteAsync(Partner, Family, "parent", CancellationToken.None);
        var gone = await _kit.Create().ExecuteAsync(Owner, Other, "parent", CancellationToken.None);

        // Assert
        stranger.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
        gone.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_ParentWithdrawsTheOwnersInvitation_WithdrawsIt()
    {
        // Arrange: any Owner or Parent may withdraw (spec §15.2 Q3).
        _kit.Store.AddMember(Family, Partner, Role.Parent, Start.AddDays(-1));
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;

        // Act
        var result = await _kit.Withdraw().ExecuteAsync(Partner, Family, created.Id, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _kit.Store.Invitations.Single().Invitation.RevokedAt.Should().Be(Start);
    }

    [Fact]
    public async Task ExecuteAsync_WithdrawUnknownOrAlreadyWithdrawn_SucceedsAndChangesNothing()
    {
        // Arrange
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;
        await _kit.Withdraw().ExecuteAsync(Owner, Family, created.Id, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromMinutes(1));

        // Act
        var again = await _kit.Withdraw().ExecuteAsync(Owner, Family, created.Id, CancellationToken.None);
        var unknown = await _kit.Withdraw().ExecuteAsync(Owner, Family, new InvitationId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e9")), CancellationToken.None);

        // Assert
        again.IsSuccess.Should().BeTrue();
        unknown.IsSuccess.Should().BeTrue();
        _kit.Store.Invitations.Single().Invitation.RevokedAt.Should().Be(Start, "the first withdrawal's time stays");
    }

    [Fact]
    public async Task ExecuteAsync_CaregiverWithdraws_IsForbidden()
    {
        // Arrange
        _kit.Store.AddMember(Family, Nanny, Role.Caregiver, Start.AddDays(-1));
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;

        // Act
        var result = await _kit.Withdraw().ExecuteAsync(Nanny, Family, created.Id, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
        _kit.Store.Invitations.Single().Invitation.IsOpen.Should().BeTrue();
    }
}
