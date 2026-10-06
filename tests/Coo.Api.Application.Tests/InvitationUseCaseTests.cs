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
        row.Hash.Should().Be(HouseholdKit.Hasher.Hash("ABCDEFGH"));
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
        _kit.Store.LockedSets.Last().Should().Equal(Family);
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

    [Fact]
    public async Task ExecuteAsync_NonMemberAsksForCaregiver_IsForbiddenNotRoleUnsupported()
    {
        // Act
        var result = await _kit.Create().ExecuteAsync(Partner, Family, "caregiver", CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task ExecuteAsync_RemovedMemberInvites_IsForbidden()
    {
        // Arrange
        _kit.Store.AddMember(Family, Partner, Role.Parent, Start.AddDays(-1));
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var result = await _kit.Create().ExecuteAsync(Partner, Family, "parent", CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
        _kit.Store.Invitations.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RemovedMemberWithdraws_IsForbidden()
    {
        // Arrange
        _kit.Store.AddMember(Family, Partner, Role.Parent, Start.AddDays(-1));
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var result = await _kit.Withdraw().ExecuteAsync(Partner, Family, created.Id, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(HouseholdErrors.Forbidden);
        _kit.Store.Invitations.Single().Invitation.IsOpen.Should().BeTrue();
    }

    private async Task<string> InviteAsync() =>
        (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value.Code.Display;

    [Fact]
    public async Task ExecuteAsync_PreviewAValidCode_NamesTheInviterAndTheBabies()
    {
        // Arrange
        _kit.Store.Babies[Family] = ["Maja"];
        var code = await InviteAsync();

        // Act
        var preview = await _kit.Preview().ExecuteAsync(Partner, code.ToLowerInvariant(), CancellationToken.None);

        // Assert
        preview.Value.Should().BeEquivalentTo(new InvitationPreview(Family, "Andrzej", ["Maja"], Role.Parent, Start.AddHours(72), AlreadyMember: false));
    }

    [Theory]
    [InlineData("ZZZZ-ZZZZ")]
    [InlineData("not a code")]
    [InlineData(null)]
    public async Task ExecuteAsync_PreviewUnknownOrMalformed_IsInvalid(string? code)
    {
        // Act
        var preview = await _kit.Preview().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        preview.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_PreviewByAMember_SaysAlreadyMember()
    {
        // Arrange
        var code = await InviteAsync();

        // Act
        var preview = await _kit.Preview().ExecuteAsync(Owner, code, CancellationToken.None);

        // Assert
        preview.Value.AlreadyMember.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptWithConsent_JoinsAsParentAndUsesTheCode()
    {
        // Arrange
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Value.Should().Be(new AcceptResult(Family, Role.Parent, AcceptOutcome.Joined));
        _kit.Store.Members.Should().ContainSingle(m => m.User == Partner && m.Household == Family && m.Role == Role.Parent && m.RevokedAt == null);
        _kit.Store.Invitations.Single().Invitation.WasAcceptedBy(Partner).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptWithoutConsent_IsConsentRequiredAndChangesNothing()
    {
        // Arrange
        var code = await InviteAsync();

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(Contracts.Sync.SyncErrors.ConsentRequired);
        _kit.Store.Members.Should().NotContain(m => m.User == Partner);
        _kit.Store.Invitations.Single().Invitation.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptRetriedAfterALostAnswer_AnswersJoinedAgain()
    {
        // Arrange: spec §5.2, the same user re-presenting a code they used, still a member.
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromMinutes(1));

        // Act
        var again = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        again.Value.Outcome.Should().Be(AcceptOutcome.Joined);
        _kit.Store.Members.Count(m => m.User == Partner).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptACodeSomeoneElseUsed_IsInvalid()
    {
        // Arrange
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        await _kit.GiveConsentAsync(Nanny);
        await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Act
        var second = await _kit.Accept().ExecuteAsync(Nanny, code, CancellationToken.None);

        // Assert
        second.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptOwnUsedCodeAfterBeingRemoved_IsInvalid()
    {
        // Arrange
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);

        // Act
        var again = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        again.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptANewCodeAfterBeingRemoved_Rejoins()
    {
        // Arrange
        await _kit.GiveConsentAsync(Partner);
        await _kit.Accept().ExecuteAsync(Partner, await InviteAsync(), CancellationToken.None);
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromHours(1));

        // Act
        var rejoined = await _kit.Accept().ExecuteAsync(Partner, await InviteAsync(), CancellationToken.None);

        // Assert
        rejoined.Value.Outcome.Should().Be(AcceptOutcome.Rejoined);
        var row = _kit.Store.Members.Single(m => m.User == Partner);
        row.RevokedAt.Should().BeNull();
        row.JoinedAt.Should().Be(Start.AddHours(1));
    }

    [Fact]
    public async Task ExecuteAsync_AcceptACodeCreatedBeforeBeingRemoved_IsInvalidAndStaysRemoved()
    {
        // Arrange: spec §10.4, a removed member needs a new invitation, not one that was already out when they were removed.
        _kit.Store.AddMember(Family, Partner, Role.Parent, Start.AddDays(-1));
        var code = await InviteAsync();
        _kit.Time.Advance(TimeSpan.FromHours(1));
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromHours(1));
        await _kit.GiveConsentAsync(Partner);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
        _kit.Store.Members.Single(m => m.User == Partner).RevokedAt.Should().Be(Start.AddHours(1));
        _kit.Store.Invitations.Single().Invitation.AcceptedBy.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptACodeCreatedAfterBeingRemoved_Rejoins()
    {
        // Arrange
        _kit.Store.AddMember(Family, Partner, Role.Parent, Start.AddDays(-1));
        await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);
        _kit.Time.Advance(TimeSpan.FromHours(1));
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Value.Outcome.Should().Be(AcceptOutcome.Rejoined);
        _kit.Store.Members.Single(m => m.User == Partner).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptByAMember_IsAlreadyMemberAndLeavesTheCodeUnused()
    {
        // Arrange
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Owner);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Owner, code, CancellationToken.None);

        // Assert
        accepted.Value.Outcome.Should().Be(AcceptOutcome.AlreadyMember);
        _kit.Store.Invitations.Single().Invitation.IsOpen.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_AcceptExpiredOrWithdrawn_IsInvalid(bool expired)
    {
        // Arrange
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;
        await _kit.GiveConsentAsync(Partner);
        if (expired)
            _kit.Time.Advance(TimeSpan.FromHours(72));
        else
            await _kit.Withdraw().ExecuteAsync(Owner, Family, created.Id, CancellationToken.None);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, created.Code.Value, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_FamilyDeletedBeforeTheLock_IsInvalid()
    {
        // Arrange: the found invitation's family goes before the accept locks it (a concurrent account deletion).
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        _kit.Store.InsideNextTransaction = () => _kit.Store.Households.Remove(Family);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_InvitationBelongsToAnotherFamilyAfterTheLock_IsInvalidAndJoinsNobody()
    {
        // Arrange: the re-read under the lock finds the code on a family that is not the one locked.
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        _kit.Store.InsideNextTransaction = () =>
        {
            var row = _kit.Store.Invitations.Single();
            var i = row.Invitation;
            _kit.Store.Invitations[0] = row with
            {
                Invitation = Invitation.Rehydrate(i.Id, Other, i.Role, i.CreatedBy, i.CreatedAt, i.ExpiresAt, i.AcceptedBy, i.AcceptedAt, i.RevokedAt),
            };
        };

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
        _kit.Store.Members.Should().NotContain(m => m.User == Partner);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("withdrawn")]
    [InlineData("used by another")]
    [InlineData("used by the caller, since removed")]
    public async Task ExecuteAsync_PreviewACodeThatIsNoLongerUsable_IsInvalid(string why)
    {
        // Arrange
        var created = (await _kit.Create().ExecuteAsync(Owner, Family, "parent", CancellationToken.None)).Value;
        await _kit.GiveConsentAsync(Partner);
        await _kit.GiveConsentAsync(Nanny);
        switch (why)
        {
            case "expired":
                _kit.Time.Advance(TimeSpan.FromHours(72));
                break;
            case "withdrawn":
                await _kit.Withdraw().ExecuteAsync(Owner, Family, created.Id, CancellationToken.None);
                break;
            case "used by another":
                await _kit.Accept().ExecuteAsync(Nanny, created.Code.Value, CancellationToken.None);
                break;
            default:
                await _kit.Accept().ExecuteAsync(Partner, created.Code.Value, CancellationToken.None);
                await _kit.Remove().ExecuteAsync(Owner, Family, Partner, CancellationToken.None);
                break;
        }

        // Act
        var preview = await _kit.Preview().ExecuteAsync(Partner, created.Code.Value, CancellationToken.None);

        // Assert
        preview.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task ExecuteAsync_PreviewACodeTheCallerUsedWhileStillAMember_SaysAlreadyMember()
    {
        // Arrange
        var code = await InviteAsync();
        await _kit.GiveConsentAsync(Partner);
        await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Act
        var preview = await _kit.Preview().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        preview.Value.AlreadyMember.Should().BeTrue();
    }

    [Theory]
    [InlineData("ZZZZ-ZZZZ")]
    [InlineData("not a code")]
    [InlineData(null)]
    public async Task ExecuteAsync_AcceptUnknownOrMalformed_IsInvalidAndChangesNothing(string? code)
    {
        // Arrange
        await _kit.GiveConsentAsync(Partner);

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
        _kit.Store.Members.Should().NotContain(m => m.User == Partner);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_AcceptWithoutConsentAnExpiredOrUsedCode_IsInvalidNotConsentRequired(bool expired)
    {
        // Arrange: §5.2, no existence oracle for a code that is no longer usable.
        var code = await InviteAsync();
        if (expired)
            _kit.Time.Advance(TimeSpan.FromHours(72));
        else
        {
            await _kit.GiveConsentAsync(Nanny);
            await _kit.Accept().ExecuteAsync(Nanny, code, CancellationToken.None);
        }

        // Act
        var accepted = await _kit.Accept().ExecuteAsync(Partner, code, CancellationToken.None);

        // Assert
        accepted.Error!.Code.Should().Be(HouseholdErrors.InvitationInvalid);
    }
}
