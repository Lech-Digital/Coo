using System.Net;
using Coo.Api.Hosting;
using Coo.Contracts.Households;

namespace Coo.Api.IntegrationTests;

/// <summary>Spec 011b §10.2: per user and per address, failures and successes alike. Each test has its own API (in-memory limiters).</summary>
public sealed class InvitationRateLimitTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task Preview_OverThePerUserLimit_Answers429WithTheCode()
    {
        // Arrange
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(3, 1000, 1000));
        using var client = factory.CreateClient();
        var user = await SyncCalls.SignInWithConsentAsync(client, "rl-user", Calls.Id(7101), Calls.Id(7102));

        // Act
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            codes.Add((await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, user.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"))).StatusCode);
        var fourth = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, user.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));
        var limited = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Accept, user.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));

        // Assert
        codes.Should().Equal(HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound);
        fourth.StatusCode.Should().Be((HttpStatusCode)429);
        limited.StatusCode.Should().Be((HttpStatusCode)429, "preview and accept share one limiter");
        (await Calls.CodeAsync(limited)).Should().Be("auth.rate-limited");
    }

    [Fact]
    public async Task Preview_OverThePerAddressLimit_Answers429ForAnotherUserToo()
    {
        // Arrange: every test client comes from one address.
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(1000, 2, 1000));
        using var client = factory.CreateClient();
        var first = await SyncCalls.SignInWithConsentAsync(client, "ra-1", Calls.Id(7111), Calls.Id(7112));
        var second = await SyncCalls.SignInWithConsentAsync(client, "ra-2", Calls.Id(7113), Calls.Id(7114));
        await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, first.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));
        await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, first.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, second.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));

        // Assert
        response.StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Fact]
    public async Task CreateInvitation_OverTheDailyLimit_Answers429()
    {
        // Arrange
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(1000, 1000, 2));
        using var client = factory.CreateClient();
        var family = Calls.Id(7121);
        var owner = await SyncCalls.SignInWithConsentAsync(client, "rc-owner", Calls.Id(7122), family);
        await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);
        await HouseholdCalls.InviteAsync(client, owner.AccessToken, family);

        // Act
        var third = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Invitations(family), owner.AccessToken,
            new CreateInvitationRequest(Contracts.Auth.AuthRoles.Parent));

        // Assert
        third.StatusCode.Should().Be((HttpStatusCode)429);
    }

    [Fact]
    public async Task Preview_Anonymous_DoesNotConsumeTheAddressBudget()
    {
        // Arrange: every test client comes from one address; the signed-in call below shares it.
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(1000, 2, 1000));
        using var client = factory.CreateClient();
        var user = await SyncCalls.SignInWithConsentAsync(client, "ran-user", Calls.Id(7131), Calls.Id(7132));

        // Act
        var anonymous = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            anonymous.Add((await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, null, new InvitationCodeRequest("ZZZZ-ZZZZ"))).StatusCode);
        anonymous.Add((await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Accept, null, new InvitationCodeRequest("ZZZZ-ZZZZ"))).StatusCode);
        var signedIn = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, user.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));

        // Assert
        anonymous.Should().AllBeEquivalentTo(HttpStatusCode.Unauthorized);
        signedIn.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateInvitation_Anonymous_Answers401EveryTimeAndSharesNoDailyBudget()
    {
        // Arrange: an unauthenticated caller has no user to count against; they must reach the 401, not fill an "anonymous" bucket.
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(1000, 1000, 1));
        using var client = factory.CreateClient();

        // Act
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            codes.Add((await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Invitations(Calls.Id(7161)), null,
                new CreateInvitationRequest(Contracts.Auth.AuthRoles.Parent))).StatusCode);
        }

        // Assert
        codes.Should().AllBeEquivalentTo(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:1111:2222:3333:4444", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    public void AddressKey_IPv4AndIPv6_PartitionsIPv6ByItsSlash64(string address, string expected)
    {
        // Act
        var key = HouseholdEndpoints.AddressKey(IPAddress.Parse(address));

        // Assert
        key.Should().Be(expected);
    }

    [Fact]
    public void AddressKey_NoAddress_IsUnknown()
    {
        // Act
        var key = HouseholdEndpoints.AddressKey(null);

        // Assert
        key.Should().Be("unknown");
    }

    [Fact]
    public async Task Me_AfterTheUsersCodeBudgetIsSpent_StillAnswers()
    {
        // Arrange
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(1, 1000, 1000));
        using var client = factory.CreateClient();
        var user = await SyncCalls.SignInWithConsentAsync(client, "rme-user", Calls.Id(7141), Calls.Id(7142));
        await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, user.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));
        var spent = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Preview, user.AccessToken, new InvitationCodeRequest("ZZZZ-ZZZZ"));

        // Act
        var me = await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, user.AccessToken);

        // Assert
        spent.StatusCode.Should().Be((HttpStatusCode)429);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateInvitation_SecondUser_HasADailyBudgetOfTheirOwn()
    {
        // Arrange
        await using var factory = TestApi.Create(api.ConnectionString, api.Google, limits: new InvitationLimits(1000, 1000, 1));
        using var client = factory.CreateClient();
        var firstFamily = Calls.Id(7151);
        var secondFamily = Calls.Id(7153);
        var first = await SyncCalls.SignInWithConsentAsync(client, "rd-1", Calls.Id(7152), firstFamily);
        var second = await SyncCalls.SignInWithConsentAsync(client, "rd-2", Calls.Id(7154), secondFamily);
        await HouseholdCalls.InviteAsync(client, first.AccessToken, firstFamily);

        // Act
        var firstAgain = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Invitations(firstFamily), first.AccessToken,
            new CreateInvitationRequest(Contracts.Auth.AuthRoles.Parent));
        var secondsFirst = await Calls.SendAsync(client, HttpMethod.Post, HouseholdCalls.Invitations(secondFamily), second.AccessToken,
            new CreateInvitationRequest(Contracts.Auth.AuthRoles.Parent));

        // Assert
        firstAgain.StatusCode.Should().Be((HttpStatusCode)429);
        secondsFirst.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
