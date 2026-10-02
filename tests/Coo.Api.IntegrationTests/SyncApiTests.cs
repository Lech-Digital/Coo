using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Coo.Contracts.Auth;
using Coo.Contracts.Sync;
using Npgsql;

namespace Coo.Api.IntegrationTests;

internal static class SyncCalls
{
    public static readonly Uri Push = new("/api/sync/push", UriKind.Relative);
    public static readonly Uri Consents = new("/api/consents", UriKind.Relative);

    /// <summary>A wall clock safely in the past of the API's real clock: stamps are never "in the future".</summary>
    public const long Wall = 1_790_000_000_000;

    public static readonly DateTimeOffset At = new(2026, 9, 20, 9, 0, 0, TimeSpan.FromHours(2));

    public static Uri Pull(Guid household, long cursor, long? head = null, int schema = SchemaVersions.Current) => new(
        FormattableString.Invariant($"/api/sync/pull?household={household}&cursor={cursor}{(head is { } h ? $"&bootstrapHead={h}" : "")}&schemaVersion={schema}"),
        UriKind.Relative);

    public static Uri Membership(Guid household) => new($"/api/households/{household}/membership", UriKind.Relative);

    public static async Task<SessionResponse> SignInWithConsentAsync(HttpClient client, string subject, Guid device, Guid household)
    {
        var session = await Calls.SignInAsync(client, subject, device, household);
        var consent = await Calls.SendAsync(client, HttpMethod.Post, Consents, session.AccessToken,
            new ConsentRequest(CareDataConsent.Purpose, CareDataConsent.CurrentWording, "0.11.0", "en"));
        consent.StatusCode.Should().Be(HttpStatusCode.NoContent);
        return session;
    }

    public static SyncChangeDto Diaper(Guid change, Guid entry, long wall, Guid device, string? note = null) => new(
        change, SyncEntityTypes.CareEntry, entry, SyncOperations.Upsert, new HlcStampDto(wall, 0, device),
        CareEntry: new CareEntryDto("diaper", Calls.Id(5000), null, false, At, null, "user:7a1c0000-0000-7000-8000-000000000001",
            "manual", null, note, JsonDocument.Parse("""{"v":1,"contents":"wet"}""").RootElement.Clone()));

    public static Task<HttpResponseMessage> PushAsync(HttpClient client, string token, Guid household, Guid device, params SyncChangeDto[] changes) =>
        Calls.SendAsync(client, HttpMethod.Post, Push, token, new PushRequest(household, device, SchemaVersions.Current, changes));
}

/// <summary>Spec 011 §7.1 and §10 (Api integration row), end to end with a fake Google.</summary>
public sealed class SyncApiTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task Push_ThenPull_RoundTripsTheEntryWithItsDetails()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6001), Calls.Id(6002));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-roundtrip", device, household);

        // Act
        var pushed = await SyncCalls.PushAsync(client, session.AccessToken, household, device,
            SyncCalls.Diaper(Calls.Id(6003), Calls.Id(6004), SyncCalls.Wall, device, "first"));
        var pulled = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken))
            .Content.ReadFromJsonAsync<PullResponse>();

        // Assert
        (await pushed.Content.ReadFromJsonAsync<PushResponse>())!.Results.Single().Result.Should().Be(ChangeResults.Applied);
        var record = pulled!.Records.Single();
        record.EntityId.Should().Be(Calls.Id(6004));
        record.CareEntry!.Note.Should().Be("first");
        record.CareEntry.Details.GetProperty("contents").GetString().Should().Be("wet");
        pulled.Cursor.Should().Be(1);
        pulled.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Push_SameBatchTwice_ReturnsTheStoredResults()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6011), Calls.Id(6012));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-retry", device, household);
        var change = SyncCalls.Diaper(Calls.Id(6013), Calls.Id(6014), SyncCalls.Wall, device);
        await SyncCalls.PushAsync(client, session.AccessToken, household, device, change);

        // Act
        var again = await SyncCalls.PushAsync(client, session.AccessToken, household, device, change);

        // Assert
        (await again.Content.ReadFromJsonAsync<PushResponse>())!.Results.Single().Result.Should().Be(ChangeResults.Applied);
        var page = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken)).Content.ReadFromJsonAsync<PullResponse>();
        page!.Cursor.Should().Be(1, "the retry was not applied a second time");
    }

    [Fact]
    public async Task Push_WithoutConsent_IsRefusedAsConsentRequired()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6021), Calls.Id(6022));
        var session = await Calls.SignInAsync(client, "sync-no-consent", device, household);

        // Act
        var response = await SyncCalls.PushAsync(client, session.AccessToken, household, device);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ConsentRequired);
    }

    [Fact]
    public async Task Push_ToAnotherAccountsHousehold_IsRefusedAsNotAMember()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        await SyncCalls.SignInWithConsentAsync(client, "sync-owner", Calls.Id(6031), Calls.Id(6032));
        var stranger = await SyncCalls.SignInWithConsentAsync(client, "sync-stranger", Calls.Id(6033), Calls.Id(6034));

        // Act
        var response = await SyncCalls.PushAsync(client, stranger.AccessToken, Calls.Id(6032), Calls.Id(6033));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.NotAMember);
    }

    [Fact]
    public async Task Pull_AfterTheMembershipWasRevoked_IsTheProblemForThatHousehold()
    {
        // Arrange: the access token stays valid; the membership is checked on every request (Spec 011 §7.1).
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6041), Calls.Id(6042));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-revoked", device, household);
        (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RevokeAsync(household);

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        problem!.Type.Should().Be(SyncErrors.MembershipRevokedType);
        problem.HouseholdId.Should().Be(household);
    }

    [Fact]
    public async Task Membership_ActiveRevokedNever_AnswersEachAsTheAdrSays()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6051), Calls.Id(6052));
        var session = await Calls.SignInAsync(client, "sync-membership", device, household);

        // Act
        var active = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Membership(household), session.AccessToken))
            .Content.ReadFromJsonAsync<MembershipStatusResponse>();
        var never = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Membership(Calls.Id(6059)), session.AccessToken);
        await RevokeAsync(household);
        var revoked = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Membership(household), session.AccessToken))
            .Content.ReadFromJsonAsync<MembershipStatusResponse>();

        // Assert
        active!.Status.Should().Be(MembershipStatuses.Active);
        never.StatusCode.Should().Be(HttpStatusCode.NotFound);
        revoked!.Status.Should().Be(MembershipStatuses.Revoked);
    }

    [Fact]
    public async Task Push_SchemaOlderThanSupported_Is426ClientTooOld()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6061), Calls.Id(6062));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-old", device, household);

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, SyncCalls.Push, session.AccessToken,
            new PushRequest(household, device, SchemaVersions.Oldest - 1, [SyncCalls.Diaper(Calls.Id(6063), Calls.Id(6064), SyncCalls.Wall, device)]));

        // Assert: refused before the batch runs, so nothing was stored.
        response.StatusCode.Should().Be((HttpStatusCode)426);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ClientTooOld);
        var page = await (await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken)).Content.ReadFromJsonAsync<PullResponse>();
        page!.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Push_201Changes_IsBatchTooLarge()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6071), Calls.Id(6072));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-large", device, household);
        var changes = Enumerable.Range(0, SyncLimits.MaxPushChanges + 1)
            .Select(i => SyncCalls.Diaper(Calls.Id(100_000 + i), Calls.Id(200_000 + i), SyncCalls.Wall + i, device)).ToArray();

        // Act
        var response = await SyncCalls.PushAsync(client, session.AccessToken, household, device, changes);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.BatchTooLarge);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"householdId":"00000000-0000-0000-0000-000000000000","deviceId":"7a1c0000-0000-7000-8000-000000000001","schemaVersion":1,"changes":[]}""")]
    public async Task Push_UnreadableRequest_IsMalformed(string body)
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-malformed-" + body.Length, Calls.Id(6081 + body.Length), Calls.Id(7081 + body.Length));
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncCalls.Push) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", session.AccessToken);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.Malformed);
    }

    [Fact]
    public async Task Pull_WithoutAToken_Is401()
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.GetAsync(SyncCalls.Pull(Calls.Id(6091), 0));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_AfterConsent_ReportsTheCurrentWording()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-me", Calls.Id(6101), Calls.Id(6102));

        // Act
        var me = await (await Calls.SendAsync(client, HttpMethod.Get, Calls.Me, session.AccessToken)).Content.ReadFromJsonAsync<MeResponse>();

        // Assert
        me!.Consent!.Wording.Should().Be(CareDataConsent.CurrentWording);
    }

    [Fact]
    public async Task Consents_UnknownWording_IsRefused()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await Calls.SignInAsync(client, "sync-wording", Calls.Id(6111), Calls.Id(6112));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Post, SyncCalls.Consents, session.AccessToken,
            new ConsentRequest(CareDataConsent.Purpose, "care-data-v0"));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ConsentUnknownWording);
    }

    [Fact]
    public async Task Push_AfterTheMembershipWasRevoked_IsTheProblemAndStoresNothing()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6121), Calls.Id(6122));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-revoked-push", device, household);
        (await SyncCalls.PushAsync(client, session.AccessToken, household, device)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RevokeAsync(household);

        // Act
        var response = await SyncCalls.PushAsync(client, session.AccessToken, household, device,
            SyncCalls.Diaper(Calls.Id(6123), Calls.Id(6124), SyncCalls.Wall, device));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        problem.Should().BeEquivalentTo(new ProblemResponse(SyncErrors.MembershipRevokedType, 403, household));
        (await CountChangeResultsAsync(Calls.Id(6123))).Should().Be(0);
    }

    [Fact]
    public async Task Pull_WithoutConsent_IsRefusedAsConsentRequired()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6131), Calls.Id(6132));
        var session = await Calls.SignInAsync(client, "sync-no-consent-pull", device, household);

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0), session.AccessToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ConsentRequired);
    }

    [Fact]
    public async Task Pull_AnotherAccountsHousehold_IsRefusedAsNotAMember()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        await SyncCalls.SignInWithConsentAsync(client, "sync-owner-pull", Calls.Id(6141), Calls.Id(6142));
        var stranger = await SyncCalls.SignInWithConsentAsync(client, "sync-stranger-pull", Calls.Id(6143), Calls.Id(6144));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(Calls.Id(6142), 0), stranger.AccessToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.NotAMember);
    }

    [Fact]
    public async Task Pull_SchemaOlderThanSupported_Is426ClientTooOld()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var (device, household) = (Calls.Id(6151), Calls.Id(6152));
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-old-pull", device, household);

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, SyncCalls.Pull(household, 0, schema: SchemaVersions.Oldest - 1), session.AccessToken);

        // Assert
        response.StatusCode.Should().Be((HttpStatusCode)426);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.ClientTooOld);
    }

    [Theory]
    [InlineData("cursor=0&schemaVersion=1")]
    [InlineData("household=7a1c0000-0000-7000-8000-000000006162&cursor=-1&schemaVersion=1")]
    [InlineData("household=7a1c0000-0000-7000-8000-000000006162&cursor=0")]
    [InlineData("household=7a1c0000-0000-7000-8000-000000006162&cursor=0&bootstrapHead=x&schemaVersion=1")]
    [InlineData("household=00000000-0000-0000-0000-000000000000&cursor=0&schemaVersion=1")]
    public async Task Pull_UnreadableQuery_IsMalformed(string query)
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-malformed-pull", Calls.Id(6161), Calls.Id(6162));

        // Act
        var response = await Calls.SendAsync(client, HttpMethod.Get, new Uri("/api/sync/pull?" + query, UriKind.Relative), session.AccessToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.Malformed);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(2, """{"householdId":"7a1c0000-0000-7000-8000-000000007200","deviceId":"7a1c0000-0000-7000-8000-000000006200","schemaVersion":1}""")]
    [InlineData(3, """{"householdId":"7a1c0000-0000-7000-8000-000000007200","deviceId":"7a1c0000-0000-7000-8000-000000006200","schemaVersion":1,"changes":[null]}""")]
    [InlineData(4, """{"householdId":"7a1c0000-0000-7000-8000-000000007200","deviceId":"7a1c0000-0000-7000-8000-000000006200","schemaVersion":1,"changes":[{"changeId":"7a1c0000-0000-7000-8000-000000000001","entityType":"CareEntry","entityId":"7a1c0000-0000-7000-8000-000000000002","operation":"delete"}]}""")]
    [InlineData(5, """{"householdId":"7a1c0000-0000-7000-8000-000000007200","deviceId":"7a1c0000-0000-7000-8000-000000006200","schemaVersion":1,"changes":[{"changeId":"00000000-0000-0000-0000-000000000000","entityType":"CareEntry","entityId":"7a1c0000-0000-7000-8000-000000000002","operation":"delete","version":{"wallClockMs":1,"counter":0,"deviceId":"7a1c0000-0000-7000-8000-000000006200"}}]}""")]
    [InlineData(6, """{"householdId":"7a1c0000-0000-7000-8000-000000007200","deviceId":"7a1c0000-0000-7000-8000-000000006200","schemaVersion":"one","changes":[]}""")]
    public async Task Push_RequestWithoutAnAnswerableChange_IsMalformed(int n, string body)
    {
        // Arrange: no changes list, a null change, a change without a version, a change without an ID, a wrong type.
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-unanswerable-" + n, Calls.Id(6200 + n), Calls.Id(7200 + n));
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncCalls.Push) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", session.AccessToken);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.Malformed);
    }

    [Fact]
    public async Task Push_UnknownCharset_IsMalformed()
    {
        // Arrange
        using var client = api.Factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "sync-charset", Calls.Id(6181), Calls.Id(6182));
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncCalls.Push) { Content = new ByteArrayContent("{}"u8.ToArray()) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=no-such-charset");
        request.Headers.Authorization = new("Bearer", session.AccessToken);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Calls.CodeAsync(response)).Should().Be(SyncErrors.Malformed);
    }

    [Fact]
    public async Task Membership_WithoutAToken_Is401()
    {
        // Arrange
        using var client = api.Factory.CreateClient();

        // Act
        var response = await client.GetAsync(SyncCalls.Membership(Calls.Id(6171)));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<long> CountChangeResultsAsync(Guid change)
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM applied_changes WHERE change_id = @c;", connection);
        command.Parameters.AddWithValue("c", change);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task RevokeAsync(Guid household)
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE memberships SET revoked_at = now() WHERE household_id = @h;", connection);
        command.Parameters.AddWithValue("h", household);
        await command.ExecuteNonQueryAsync();
    }
}
