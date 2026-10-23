using System.Net;
using System.Net.Http.Json;
using Coo.Contracts.AiParsing;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Api.IntegrationTests;

/// <summary>AI logging end to end with the fake parser (ADR 0005 §7: no key, the flow still runs).</summary>
public sealed class AiParsingApiTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task Parse_StartedOnTheLeft_ProposesABreastTimerAndCountsTheParse()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "ai-user1", Calls.Id(9101), Calls.Id(9102));
        var request = new AiParseRequest(
            session.HouseholdId, Calls.Id(9103), "she started on the left", api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow(), "Europe/Amsterdam", "en", new AiParseContext([], null, "ml"));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, new Uri("/api/ai/parse", UriKind.Relative), session.AccessToken, request);
        var body = await response.Content.ReadFromJsonAsync<AiParseResponse>();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var proposal = Assert.Single(body!.Proposals);
        Assert.Equal(AiActions.BreastStart, proposal.Action);
        Assert.Equal("left", proposal.Side);
        Assert.Null(body.ErrorCode);
    }

    [Fact]
    public async Task Parse_FreeAllowanceUsedUp_SaysSoAfterFiveParses()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "ai-user2", Calls.Id(9104), Calls.Id(9105));
        var request = new AiParseRequest(
            session.HouseholdId, Calls.Id(9106), "wet diaper", api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow(), "Europe/Amsterdam", "en", new AiParseContext([], null, "ml"));
        for (var i = 0; i < 5; i++)
            await Calls.SendAsync(client, HttpMethod.Post, new Uri("/api/ai/parse", UriKind.Relative), session.AccessToken, request);

        // Act
        var sixth = await (await Calls.SendAsync(client, HttpMethod.Post, new Uri("/api/ai/parse", UriKind.Relative), session.AccessToken, request))
            .Content.ReadFromJsonAsync<AiParseResponse>();

        // Assert
        Assert.Equal("ai.free-limit", sixth!.ErrorCode);
        Assert.Empty(sixth.Proposals);
    }

    [Fact]
    public async Task Parse_AnotherFamily_IsForbidden()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var me = await SyncCalls.SignInWithConsentAsync(client, "ai-user3", Calls.Id(9107), Calls.Id(9108));
        var them = await SyncCalls.SignInWithConsentAsync(client, "ai-user4", Calls.Id(9109), Calls.Id(9110));
        var request = new AiParseRequest(
            them.HouseholdId, Calls.Id(9111), "wet diaper", api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow(), "Europe/Amsterdam", "en", new AiParseContext([], null, "ml"));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, new Uri("/api/ai/parse", UriKind.Relative), me.AccessToken, request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
