using System.Net;
using System.Text;
using Coo.Contracts.Households;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Infrastructure.Households;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011b §10.1 on the phone: only the server's own codes are refusals; anything else is "unavailable".</summary>
public sealed class HouseholdClientTests
{
    private static readonly HouseholdId Family = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly InvitationCode Code = InvitationCode.Parse("ABCDEFGH").Value;

    [Fact]
    public async Task PreviewAsync_Answer_MapsItAndSendsTheCodeInTheBody()
    {
        // Arrange
        HttpRequestMessage? sent = null;
        string? body = null;
        var client = Client(async r =>
        {
            (sent, body) = (r, await r.Content!.ReadAsStringAsync());
            return await Json(HttpStatusCode.OK,
                $$"""{"householdId":"{{Family.Value}}","inviterName":"Andrzej","babyNames":["Lena"],"role":"parent","expiresAt":"2026-10-09T12:02:00+00:00","alreadyMember":false}""");
        });

        // Act
        var preview = await client.PreviewAsync(Code, CancellationToken.None);

        // Assert
        sent!.RequestUri!.ToString().Should().Be("https://api.test/api/invitations/preview", "the code never travels in a path or query");
        body.Should().Be("""{"code":"ABCDEFGH"}""");
        preview.Should().BeEquivalentTo(new InvitationPreview(Family, "Andrzej", ["Lena"], Role.Parent, new DateTimeOffset(2026, 10, 9, 12, 2, 0, TimeSpan.Zero), false));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"code":"invitation.invalid"}""", HouseholdErrors.InvitationInvalid)]
    [InlineData((HttpStatusCode)429, """{"code":"auth.rate-limited"}""", HouseholdErrors.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"sync.consent-required"}""", "sync.consent-required")]
    [InlineData(HttpStatusCode.Conflict, """{"code":"household.last-family"}""", HouseholdErrors.LastFamily)]
    public async Task AcceptAsync_AServerCode_IsARefusalWithThatCode(HttpStatusCode status, string json, string code)
    {
        // Arrange
        var client = Client(_ => Json(status, json));

        // Act
        var act = async () => await client.AcceptAsync(Code, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<HouseholdRefusedException>()).Which.Code.Should().Be(code);
    }

    [Fact]
    public async Task PreviewAsync_HtmlPage_IsUnavailable()
    {
        // Arrange: Review Focus 5, a captive portal answers 200 with its own page.
        var client = Client(_ => Raw(HttpStatusCode.OK, "text/html", "<html>Log in to the Wi-Fi</html>"));

        // Act
        var act = async () => await client.PreviewAsync(Code, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.BadGateway, "<html>502</html>")]
    [InlineData(HttpStatusCode.NotFound, """{"code":"something.else"}""")]
    public async Task AcceptAsync_Bare403_IsUnavailable(HttpStatusCode status, string body)
    {
        // Arrange: a proxy's bare 403, a 502, an unknown code: none of them is the server refusing the invitation.
        var client = Client(_ => Raw(status, "text/plain", body));

        // Act
        var act = async () => await client.AcceptAsync(Code, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Fact]
    public async Task CreateInvitationAsync_Created_ReturnsTheCodeAndOurLink()
    {
        // Arrange
        string? body = null;
        var client = Client(async r =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.Created,
                """{"invitationId":"7a1c0000-0000-7000-8000-0000000000e1","code":"ABCDEFGH","link":"https://api.coo.lechdigital.nl/i/ABCDEFGH","expiresAt":"2026-10-09T12:02:00+00:00"}""");
        });

        // Act
        var created = await client.CreateInvitationAsync(Family, Role.Parent, CancellationToken.None);

        // Assert
        body.Should().Be("""{"role":"parent"}""");
        created.Code.Display.Should().Be("ABCD-EFGH");
        created.Link.Should().Be(new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"));
    }

    [Fact]
    public async Task ListMembersAsync_Answer_MapsMembersAndInvitations()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.OK,
            """
            {"members":[{"userId":"7a1c0000-0000-7000-8000-0000000000c1","name":"Andrzej","email":"a@example.com","role":"owner","joinedAt":"2026-09-01T00:00:00+00:00","isYou":true}],
             "invitations":[{"invitationId":"7a1c0000-0000-7000-8000-0000000000e1","role":"parent","createdBy":"7a1c0000-0000-7000-8000-0000000000c1","createdAt":"2026-10-06T12:02:00+00:00","expiresAt":"2026-10-09T12:02:00+00:00"}]}
            """));

        // Act
        var members = await client.ListMembersAsync(Family, CancellationToken.None);

        // Assert
        members.Members.Single().Should().Match<FamilyMember>(m => m.Role == Role.Owner && m.IsYou && m.Name == "Andrzej");
        members.Invitations.Single().ExpiresAt.Should().Be(new DateTimeOffset(2026, 10, 9, 12, 2, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task LeaveAsync_Deleted_IsDeletedAndSendsConfirm()
    {
        // Arrange
        string? body = null;
        var client = Client(async r =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK, """{"outcome":"deleted"}""");
        });

        // Act
        var outcome = await client.LeaveAsync(Family, confirm: true, CancellationToken.None);

        // Assert
        outcome.Should().Be(LeaveOutcome.Deleted);
        body.Should().Be("""{"confirm":true}""");
    }

    [Fact]
    public async Task CreateHouseholdAsync_IdTaken_IsARefusal()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.Conflict, """{"code":"household.id-taken"}"""));

        // Act
        var act = async () => await client.CreateHouseholdAsync(Family, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<HouseholdRefusedException>()).Which.Code.Should().Be(HouseholdErrors.IdTaken);
    }

    [Fact]
    public async Task ListMembersAsync_UnknownRole_IsUnavailable()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.OK,
            """{"members":[{"userId":"7a1c0000-0000-7000-8000-0000000000c1","name":"A","email":"a@example.com","role":"admin","joinedAt":"2026-09-01T00:00:00+00:00","isYou":true}],"invitations":[]}"""));

        // Act
        var act = async () => await client.ListMembersAsync(Family, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Fact]
    public async Task PreviewAsync_NoNetwork_IsUnavailable()
    {
        // Arrange
        var client = Client(_ => throw new HttpRequestException("offline"));

        // Act
        var act = async () => await client.PreviewAsync(Code, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Fact]
    public async Task PreviewAsync_CallerCancels_PropagatesTheCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var client = Client(r =>
        {
            cts.Cancel();
            r.Content!.Dispose();
            throw new TaskCanceledException();
        });

        // Act
        var act = async () => await client.PreviewAsync(Code, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("owner", Role.Owner)]
    [InlineData("parent", Role.Parent)]
    [InlineData("caregiver", Role.Caregiver)]
    [InlineData("admin", null)]
    [InlineData(null, null)]
    public void Parse_RoleText_MapsKnownRolesOnly(string? text, Role? expected)
    {
        // Arrange & Act
        var role = RoleNames.Parse(text);

        // Assert
        role.Should().Be(expected);
    }

    [Theory]
    [InlineData(Role.Owner, "owner")]
    [InlineData(Role.Parent, "parent")]
    [InlineData(Role.Caregiver, "caregiver")]
    public void Text_Role_IsTheWireName(Role role, string expected)
    {
        // Arrange & Act
        var text = RoleNames.Text(role);

        // Assert
        text.Should().Be(expected);
    }

    [Theory]
    [InlineData(HouseholdErrors.InvitationInvalid)]
    [InlineData(HouseholdErrors.InvitationRoleUnsupported)]
    [InlineData(HouseholdErrors.InvitationTooMany)]
    [InlineData(HouseholdErrors.Forbidden)]
    [InlineData(HouseholdErrors.MemberNotFound)]
    [InlineData(HouseholdErrors.OwnerCannotLeave)]
    [InlineData(HouseholdErrors.LastFamily)]
    [InlineData(HouseholdErrors.ConfirmationRequired)]
    [InlineData(HouseholdErrors.IdTaken)]
    [InlineData(HouseholdErrors.AlreadyHasFamily)]
    [InlineData(HouseholdErrors.Malformed)]
    [InlineData(HouseholdErrors.RateLimited)]
    [InlineData("sync.consent-required")]
    public async Task PreviewAsync_EveryServerCode_IsARefusalWithThatCode(string code)
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.BadRequest, $$"""{"code":"{{code}}"}"""));

        // Act
        var act = async () => await client.PreviewAsync(Code, CancellationToken.None);

        // Assert
        (await act.Should().ThrowAsync<HouseholdRefusedException>()).Which.Code.Should().Be(code);
    }

    [Fact]
    public async Task PreviewAsync_Timeout_IsUnavailable()
    {
        // Arrange: the caller did not cancel.
        var client = Client(_ => throw new TaskCanceledException());

        // Act
        var act = async () => await client.PreviewAsync(Code, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Fact]
    public async Task PreviewAsync_SessionCannotRefresh_IsUnavailable()
    {
        // Arrange
        var client = Client(_ => throw new Coo.Mobile.Application.Auth.AuthUnavailableException("no refresh"));

        // Act
        var act = async () => await client.PreviewAsync(Code, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Fact]
    public async Task PreviewAsync_Bare401_IsUnavailable()
    {
        // Arrange: after the refresh, TokenRefresher signs out; here it is only "nothing decided".
        var client = Client(_ => Raw(HttpStatusCode.Unauthorized, "text/plain", ""));

        // Act
        var act = async () => await client.PreviewAsync(Code, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("remove")]
    [InlineData("create")]
    public async Task BodylessCall_PortalAnswers200Page_IsUnavailable(string call)
    {
        // Arrange: a captive portal's 200 is not the server's 204 or 201.
        var client = Client(_ => Raw(HttpStatusCode.OK, "text/html", "<html>Log in to the Wi-Fi</html>"));

        // Act
        var act = async () => await Call(client, call);

        // Assert
        await act.Should().ThrowAsync<HouseholdUnavailableException>();
    }

    [Theory]
    [InlineData("withdraw", "DELETE", "https://api.test/api/households/7a1c0000-0000-7000-8000-0000000000a1/invitations/7a1c0000-0000-7000-8000-0000000000e1", HttpStatusCode.NoContent)]
    [InlineData("remove", "DELETE", "https://api.test/api/households/7a1c0000-0000-7000-8000-0000000000a1/members/7a1c0000-0000-7000-8000-0000000000c1", HttpStatusCode.NoContent)]
    [InlineData("create", "POST", "https://api.test/api/households", HttpStatusCode.Created)]
    public async Task BodylessCall_ServerStatus_SucceedsOnTheRoute(string call, string method, string uri, HttpStatusCode status)
    {
        // Arrange
        HttpRequestMessage? sent = null;
        var client = Client(r =>
        {
            sent = r;
            return Task.FromResult(new HttpResponseMessage(status));
        });

        // Act
        await Call(client, call);

        // Assert
        (sent!.Method.Method, sent.RequestUri!.ToString()).Should().Be((method, uri));
    }

    [Fact]
    public async Task LeaveAsync_And_CreateInvitationAsync_UseTheirRoutes()
    {
        // Arrange
        var uris = new List<string>();
        var client = Client(r =>
        {
            uris.Add(r.Method.Method + " " + r.RequestUri);
            return Json(r.RequestUri!.AbsolutePath.EndsWith("leave", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.Created,
                r.RequestUri.AbsolutePath.EndsWith("leave", StringComparison.Ordinal)
                    ? """{"outcome":"left"}"""
                    : """{"invitationId":"7a1c0000-0000-7000-8000-0000000000e1","code":"ABCDEFGH","link":"x","expiresAt":"2026-10-09T12:02:00+00:00"}""");
        });

        // Act
        await client.LeaveAsync(Family, confirm: false, CancellationToken.None);
        await client.CreateInvitationAsync(Family, Role.Parent, CancellationToken.None);

        // Assert
        uris.Should().Equal(
            "POST https://api.test/api/households/7a1c0000-0000-7000-8000-0000000000a1/leave",
            "POST https://api.test/api/households/7a1c0000-0000-7000-8000-0000000000a1/invitations");
    }

    private static Task Call(HouseholdClient client, string call) => call switch
    {
        "withdraw" => client.WithdrawInvitationAsync(Family, Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"), CancellationToken.None),
        "remove" => client.RemoveMemberAsync(Family, new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), CancellationToken.None),
        _ => client.CreateHouseholdAsync(Family, CancellationToken.None),
    };

    private static HouseholdClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
        new(new HttpClient(new FakeServer(server)) { BaseAddress = new Uri("https://api.test/") });

    private static Task<HttpResponseMessage> Raw(HttpStatusCode status, string mediaType, string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
        return Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}
