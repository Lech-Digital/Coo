using System.Net;
using System.Net.Http.Json;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;
using Coo.Contracts.Sync;

namespace Coo.Api.IntegrationTests;

internal static class HouseholdCalls
{
    public static readonly Uri Preview = new("/api/invitations/preview", UriKind.Relative);
    public static readonly Uri Accept = new("/api/invitations/accept", UriKind.Relative);
    public static readonly Uri Households = new("/api/households", UriKind.Relative);

    public static Uri Invitations(Guid household) => new($"/api/households/{household}/invitations", UriKind.Relative);

    public static Uri Invitation(Guid household, Guid invitation) => new($"/api/households/{household}/invitations/{invitation}", UriKind.Relative);

    public static Uri Members(Guid household) => new($"/api/households/{household}/members", UriKind.Relative);

    public static Uri Member(Guid household, Guid user) => new($"/api/households/{household}/members/{user}", UriKind.Relative);

    public static Uri Leave(Guid household) => new($"/api/households/{household}/leave", UriKind.Relative);

    public static async Task<InvitationCreatedResponse> InviteAsync(HttpClient client, string token, Guid household)
    {
        var response = await Calls.SendAsync(client, HttpMethod.Post, Invitations(household), token, new CreateInvitationRequest(AuthRoles.Parent));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<InvitationCreatedResponse>())!;
    }

    public static async Task<AcceptInvitationResponse> AcceptAsync(HttpClient client, string token, string code)
    {
        var response = await Calls.SendAsync(client, HttpMethod.Post, Accept, token, new InvitationCodeRequest(code));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AcceptInvitationResponse>())!;
    }
}

/// <summary>Spec 011b §10 and §12 (Api integration row), end to end with a fake Google.</summary>
public sealed class HouseholdApiTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task InvitePreviewAccept_SecondUser_BothPushAndPullEachOthersEntries()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (family, ownerPhone, joinerPhone) = (Calls.Id(7001), Calls.Id(7002), Calls.Id(7003));
        var owner = await SyncCalls.SignInWithConsentAsync(client, "hh-owner", ownerPhone, family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "hh-joiner", joinerPhone, Calls.Id(7004));

        // Act
        var invitation = await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);
        var preview = await (await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, joiner.AccessToken,
            new InvitationCodeRequest(invitation.Code))).Content.ReadFromJsonAsync<InvitationPreviewResponse>();
        var accepted = await HouseholdCalls.AcceptAsync(client, joiner.AccessToken, invitation.Code);
        await SyncCalls.PushAsync(client, owner.AccessToken, family, ownerPhone, SyncCalls.Diaper(Calls.Id(7005), Calls.Id(7006), SyncCalls.Wall, ownerPhone, "his"));
        await SyncCalls.PushAsync(client, joiner.AccessToken, family, joinerPhone, SyncCalls.Diaper(Calls.Id(7007), Calls.Id(7008), SyncCalls.Wall, joinerPhone, "hers"));
        var ownersView = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(family, 0), owner.AccessToken)).Content.ReadFromJsonAsync<PullResponse>();
        var joinersView = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(family, 0), joiner.AccessToken)).Content.ReadFromJsonAsync<PullResponse>();

        // Assert
        invitation.Code.Should().MatchRegex("^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{8}$");
        invitation.Link.Should().Be("https://api.coo.lechdigital.nl/i/" + invitation.Code);
        preview!.InviterName.Should().Be("Parent hh-owner");
        preview.AlreadyMember.Should().BeFalse();
        accepted.Should().Be(new AcceptInvitationResponse(family, AuthRoles.Parent, AcceptOutcomes.Joined));
        ownersView!.Records.Select(r => r.CareEntry!.Note).Should().BeEquivalentTo("his", "hers");
        joinersView!.Records.Select(r => r.CareEntry!.Note).Should().BeEquivalentTo("his", "hers");
    }

    [Fact]
    public async Task Push_AParentAndASeededCaregiver_FollowTheRoleRule()
    {
        // Arrange: spec §10.5. A Parent joined through accept may manage the baby; a Caregiver seeded in the database may not.
        using var client = api.Factory.CreateClient();
        var (family, ownerPhone, parentPhone, nannyPhone) = (Calls.Id(7011), Calls.Id(7012), Calls.Id(7013), Calls.Id(7014));
        var owner = await SyncCalls.SignInWithConsentAsync(client, "role-owner", ownerPhone, family);
        var parent = await SyncCalls.SignInWithConsentAsync(client, "role-parent", parentPhone, Calls.Id(7015));
        var nanny = await SyncCalls.SignInWithConsentAsync(client, "role-nanny", nannyPhone, Calls.Id(7016));
        await HouseholdCalls.AcceptAsync(client, parent.AccessToken, (await HouseholdCalls.InviteAsync(client, owner.AccessToken, family)).Code);
        await using (var connection = new Npgsql.NpgsqlConnection(api.ConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new Npgsql.NpgsqlCommand(
                $"INSERT INTO memberships (household_id, user_id, role, joined_at) VALUES ('{family}', '{nanny.User.Id}', 'caregiver', now());", connection);
            await seed.ExecuteNonQueryAsync();
        }

        // Act
        var parentBaby = await ResultAsync(await SyncCalls.PushAsync(client, parent.AccessToken, family, parentPhone, Baby(Calls.Id(7017), Calls.Id(7018), parentPhone)));
        var parentSettings = await ResultAsync(await SyncCalls.PushAsync(client, parent.AccessToken, family, parentPhone, Settings(Calls.Id(7019), family, parentPhone)));
        var nannyBaby = await ResultAsync(await SyncCalls.PushAsync(client, nanny.AccessToken, family, nannyPhone, Baby(Calls.Id(7020), Calls.Id(7021), nannyPhone)));
        var nannySettings = await ResultAsync(await SyncCalls.PushAsync(client, nanny.AccessToken, family, nannyPhone, Settings(Calls.Id(7022), family, nannyPhone, wall: SyncCalls.Wall + 5)));
        var nannyEntry = await ResultAsync(await SyncCalls.PushAsync(client, nanny.AccessToken, family, nannyPhone, SyncCalls.Diaper(Calls.Id(7023), Calls.Id(7024), SyncCalls.Wall, nannyPhone)));

        // Assert
        parentBaby.Result.Should().Be(ChangeResults.Applied);
        parentSettings.Result.Should().Be(ChangeResults.Applied);
        nannyBaby.Should().Be(new ChangeResultDto(Calls.Id(7020), ChangeResults.Rejected, RejectionReasons.RoleForbidden));
        nannySettings.Reason.Should().Be(RejectionReasons.RoleForbidden);
        nannyEntry.Result.Should().Be(ChangeResults.Applied);
    }

    [Fact]
    public async Task Remove_TheJoiner_TheirNextSyncIsMembershipRevokedAndMembershipSaysRevoked()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (family, phone) = (Calls.Id(7031), Calls.Id(7033));
        var owner = await SyncCalls.SignInWithConsentAsync(client, "rm-owner", Calls.Id(7032), family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "rm-joiner", phone, Calls.Id(7034));
        await HouseholdCalls.AcceptAsync(client, joiner.AccessToken, (await HouseholdCalls.InviteAsync(client, owner.AccessToken, family)).Code);
        var stillOpen = await HouseholdCalls.InviteAsync(client, joiner.AccessToken, family);

        // Act
        var removed = await Calls.SendAsync(client, HttpMethod.Delete, HouseholdCalls.Member(family, joiner.User.Id), owner.AccessToken);
        var pull = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(family, 0), joiner.AccessToken);
        var status = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Membership(family), joiner.AccessToken)).Content.ReadFromJsonAsync<MembershipStatusResponse>();
        var members = await (await Calls.SendAsync(client, HttpMethod.Get, HouseholdCalls.Members(family), owner.AccessToken)).Content.ReadFromJsonAsync<MembersResponse>();
        var theirLink = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, owner.AccessToken, new InvitationCodeRequest(stillOpen.Code));

        // Assert
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        pull.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await pull.Content.ReadFromJsonAsync<ProblemResponse>())!.Should().Be(new ProblemResponse(SyncErrors.MembershipRevokedType, 403, family));
        status!.Status.Should().Be(MembershipStatuses.Revoked);
        members!.Members.Should().ContainSingle().Which.IsYou.Should().BeTrue();
        theirLink.StatusCode.Should().Be(HttpStatusCode.NotFound, "a removed member's open invitations are withdrawn with them");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignIn_ARemovedMemberAgain_StaysOutOfTheFamilyThatRemovedThem(bool withTheRemovedFamilyAsProvisional)
    {
        // Arrange: spec §10.3. The removal is permanent until a new invitation; signing in again must not undo it.
        using var client = api.Factory.CreateClient();
        var (family, ownFamily, phone) = (Calls.Id(7101), Calls.Id(7104), Calls.Id(7103));
        var owner = await SyncCalls.SignInWithConsentAsync(client, "re-owner", Calls.Id(7102), family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "re-joiner", phone, ownFamily);
        await HouseholdCalls.AcceptAsync(client, joiner.AccessToken, (await HouseholdCalls.InviteAsync(client, owner.AccessToken, family)).Code);
        await Calls.SendAsync(client, HttpMethod.Delete, HouseholdCalls.Member(family, joiner.User.Id), owner.AccessToken);

        // Act
        var again = await Calls.SignInAsync(client, "re-joiner", phone, withTheRemovedFamilyAsProvisional ? family : Calls.Id(7105));
        var me = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, again.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();
        var pull = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(family, 0), again.AccessToken);

        // Assert
        again.HouseholdId.Should().Be(ownFamily);
        me!.Memberships!.Select(m => m.HouseholdId).Should().Equal(ownFamily);
        me.Household!.HouseholdId.Should().Be(ownFamily);
        pull.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Members_AStranger_IsForbiddenNotTheSyncProblem()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var family = Calls.Id(7041);
        await SyncCalls.SignInWithConsentAsync(client, "mb-owner", Calls.Id(7042), family);
        var stranger = await SyncCalls.SignInWithConsentAsync(client, "mb-stranger", Calls.Id(7043), Calls.Id(7044));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, HouseholdCalls.Members(family), stranger.AccessToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(HouseholdErrors.Forbidden);
    }

    [Fact]
    public async Task Accept_WithoutConsent_IsConsentRequired()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var family = Calls.Id(7051);
        var owner = await SyncCalls.SignInWithConsentAsync(client, "nc-owner", Calls.Id(7052), family);
        var joiner = await Calls.SignInAsync(client, "nc-joiner", Calls.Id(7053), Calls.Id(7054));
        var invitation = await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Accept, joiner.AccessToken, new InvitationCodeRequest(invitation.Code));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ConsentRequired);
    }

    [Fact]
    public async Task WithdrawThenAccept_TheCode_IsInvalid()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var family = Calls.Id(7061);
        var owner = await SyncCalls.SignInWithConsentAsync(client, "wd-owner", Calls.Id(7062), family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "wd-joiner", Calls.Id(7063), Calls.Id(7064));
        var invitation = await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);

        // Act
        var withdrawn = await Calls.SendAsync(client, HttpMethod.Delete, HouseholdCalls.Invitation(family, invitation.InvitationId), owner.AccessToken);
        var again = await Calls.SendAsync(client, HttpMethod.Delete, HouseholdCalls.Invitation(family, invitation.InvitationId), owner.AccessToken);
        var accept = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Accept, joiner.AccessToken, new InvitationCodeRequest(invitation.Code));

        // Assert
        withdrawn.StatusCode.Should().Be(HttpStatusCode.NoContent);
        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        accept.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Calls.CodeAsync(accept)).Should().Be(HouseholdErrors.InvitationInvalid);
    }

    [Fact]
    public async Task Leave_ParentWithTheirOwnFamily_LeavesAndMeListsOneMembership()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (family, ownFamily) = (Calls.Id(7071), Calls.Id(7074));
        var owner = await SyncCalls.SignInWithConsentAsync(client, "lv-owner", Calls.Id(7072), family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "lv-joiner", Calls.Id(7073), ownFamily);
        await HouseholdCalls.AcceptAsync(client, joiner.AccessToken, (await HouseholdCalls.InviteAsync(client, owner.AccessToken, family)).Code);
        var meBefore = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, joiner.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        // Act
        var left = await (await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Leave(family), joiner.AccessToken, new LeaveRequest(false)))
            .Content.ReadFromJsonAsync<LeaveResponse>();
        var meAfter = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, joiner.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();
        var lastFamily = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Leave(ownFamily), joiner.AccessToken, new LeaveRequest(true));

        // Assert
        meBefore!.Memberships!.Select(m => m.HouseholdId).Should().Equal(ownFamily, family);
        meBefore.Household!.HouseholdId.Should().Be(ownFamily, "household stays the earliest active family for 0.11.0 phones");
        left!.Outcome.Should().Be(LeaveOutcomes.Left);
        meAfter!.Memberships!.Should().ContainSingle().Which.HouseholdId.Should().Be(ownFamily);
        lastFamily.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Calls.CodeAsync(lastFamily)).Should().Be(HouseholdErrors.LastFamily);
    }

    [Fact]
    public async Task CreateHousehold_AfterRemovalFromTheOnlyFamily_IsCreatedThenAlreadyHasFamily()
    {
        // Arrange: spec §8.4.
        using var client = api.Factory.CreateClient();
        var family = Calls.Id(7081);
        var owner = await SyncCalls.SignInWithConsentAsync(client, "ch-owner", Calls.Id(7082), family);
        var joiner = await SyncCalls.SignInWithConsentAsync(client, "ch-joiner", Calls.Id(7083), Calls.Id(7084));
        await HouseholdCalls.AcceptAsync(client, joiner.AccessToken, (await HouseholdCalls.InviteAsync(client, owner.AccessToken, family)).Code);
        await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Leave(Calls.Id(7084)), joiner.AccessToken, new LeaveRequest(true));
        await Calls.SendAsync(client, HttpMethod.Delete, HouseholdCalls.Member(family, joiner.User.Id), owner.AccessToken);

        // Act
        var taken = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Households, joiner.AccessToken, new CreateHouseholdRequest(family));
        var created = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Households, joiner.AccessToken, new CreateHouseholdRequest(Calls.Id(7085)));
        var second = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Households, joiner.AccessToken, new CreateHouseholdRequest(Calls.Id(7086)));

        // Assert
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Calls.CodeAsync(second)).Should().Be(HouseholdErrors.AlreadyHasFamily);
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict, "the caller has no active family, so the only refusal left is the taken id");
        (await Calls.CodeAsync(taken)).Should().Be(HouseholdErrors.IdTaken);
    }

    [Fact]
    public async Task CreateInvitation_ANonMember_IsForbiddenWhateverRoleTheyAskFor()
    {
        // Arrange: Task 2's ruling, at HTTP level.
        using var client = api.Factory.CreateClient();
        var family = Calls.Id(7091);
        await SyncCalls.SignInWithConsentAsync(client, "ci-owner", Calls.Id(7092), family);
        var stranger = await SyncCalls.SignInWithConsentAsync(client, "ci-stranger", Calls.Id(7093), Calls.Id(7094));

        // Act
        var asParent = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Invitations(family), stranger.AccessToken, new CreateInvitationRequest(AuthRoles.Parent));
        var asCaregiver = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Invitations(family), stranger.AccessToken, new CreateInvitationRequest(AuthRoles.Caregiver));

        // Assert
        asParent.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(asParent)).Should().Be(HouseholdErrors.Forbidden);
        asCaregiver.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(asCaregiver)).Should().Be(HouseholdErrors.Forbidden);
    }

    [Theory]
    [InlineData("POST", "/api/households")]
    [InlineData("POST", "/api/households/7a1c0000-0000-7000-8000-000000007101/invitations")]
    [InlineData("DELETE", "/api/households/7a1c0000-0000-7000-8000-000000007101/invitations/7a1c0000-0000-7000-8000-000000007102")]
    [InlineData("GET", "/api/households/7a1c0000-0000-7000-8000-000000007101/members")]
    [InlineData("DELETE", "/api/households/7a1c0000-0000-7000-8000-000000007101/members/7a1c0000-0000-7000-8000-000000007102")]
    [InlineData("POST", "/api/households/7a1c0000-0000-7000-8000-000000007101/leave")]
    [InlineData("POST", "/api/invitations/preview")]
    [InlineData("POST", "/api/invitations/accept")]
    public async Task Endpoint_WithoutAToken_IsUnauthorized(string method, string path)
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await Calls.SendAsync(client, new HttpMethod(method), new Uri(path, UriKind.Relative), null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("POST", "/api/households", "text/plain", "hello")]
    [InlineData("POST", "/api/households", "application/json", "{ not json")]
    [InlineData("POST", "/api/households/7a1c0000-0000-7000-8000-000000007201/invitations", "text/plain", "hello")]
    [InlineData("POST", "/api/households/7a1c0000-0000-7000-8000-000000007201/invitations", "application/json", "{ not json")]
    public async Task CreateEndpoints_WithAnUnreadableBody_Answer400Malformed(string method, string path, string contentType, string body)
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "hh-malformed-create-" + path.Length + contentType.Length, Calls.Id(7202 + path.Length + contentType.Length), Calls.Id(7203 + path.Length + contentType.Length));

        // Act
        var response = await SendRawAsync(client, new HttpMethod(method), path, session.AccessToken, contentType, body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be("household.malformed");
    }

    [Theory]
    [InlineData("/api/invitations/preview", "text/plain", "hello")]
    [InlineData("/api/invitations/preview", "application/json", "{ not json")]
    [InlineData("/api/invitations/accept", "text/plain", "hello")]
    [InlineData("/api/invitations/accept", "application/json", "{ not json")]
    public async Task CodeEndpoints_WithAnUnreadableBody_Answer404InvitationInvalid(string path, string contentType, string body)
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "hh-malformed-code-" + path.Length + contentType.Length, Calls.Id(7302 + path.Length + contentType.Length), Calls.Id(7303 + path.Length + contentType.Length));

        // Act
        var response = await SendRawAsync(client, HttpMethod.Post, path, session.AccessToken, contentType, body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Calls.CodeAsync(response)).Should().Be("invitation.invalid");
    }

    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, string path, string token, string contentType, string body)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<ChangeResultDto> ResultAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<PushResponse>())!.Results.Single();

    private static SyncChangeDto Baby(Guid change, Guid baby, Guid device) => new(
        change, SyncEntityTypes.BabyProfile, baby, SyncOperations.Upsert, new HlcStampDto(SyncCalls.Wall, 0, device),
        BabyProfile: new BabyProfileDto("Lena", [], new DateOnly(2026, 8, 20), null));

    private static SyncChangeDto Settings(Guid change, Guid household, Guid device, long wall = SyncCalls.Wall) => new(
        change, SyncEntityTypes.HouseholdSettings, household, SyncOperations.Upsert, new HlcStampDto(wall, 0, device),
        HouseholdSettings: new HouseholdSettingsDto("ml", "Europe/Amsterdam", []));
}
