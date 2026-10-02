using System.Net;
using System.Text;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Sync;

namespace Coo.Mobile.Infrastructure.Tests;

/// <summary>Spec 011 §7.1 on the phone: only the server's own codes are refusals; anything else is a failed cycle.</summary>
public sealed class SyncClientTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly Guid Change = Guid.Parse("7a1c0000-0000-7000-8000-000000000c01");
    private static readonly Guid Entry = Guid.Parse("7a1c0000-0000-7000-8000-000000000e01");

    private static OutgoingChange Upsert(string details = """{"v":1,"contents":"wet"}""") => new(
        Change, SyncEntityTypes.CareEntry, Entry, false, new HlcStamp(1_790_000_000_000, 2, Phone), 1,
        new CareEntryPayload("diaper", Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"), null, false,
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(2)), null, "user:7a1c0000-0000-7000-8000-0000000000c1", "manual", null, "n", details));

    [Fact]
    public async Task PushAsync_Body_CarriesTheChangeWithItsDetailsAsAnObject()
    {
        // Arrange
        string? sent = null;
        var client = Client(async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK, $$"""{"results":[{"changeId":"{{Change}}","result":"applied","reason":null}]}""");
        });

        // Act
        await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        sent.Should().Contain("\"schemaVersion\":1").And.Contain("\"operation\":\"upsert\"")
            .And.Contain("\"details\":{\"v\":1,\"contents\":\"wet\"}").And.Contain("\"wallClockMs\":1790000000000");
    }

    [Fact]
    public async Task PushAsync_Delete_SendsNoPayload()
    {
        // Arrange
        string? sent = null;
        var client = Client(async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            return await Json(HttpStatusCode.OK, """{"results":[]}""");
        });

        // Act
        await client.PushAsync(Household, Phone, [Upsert() with { IsDelete = true, Payload = null }], CancellationToken.None);

        // Assert
        sent.Should().Contain("\"operation\":\"delete\"").And.Contain("\"careEntry\":null");
    }

    [Fact]
    public async Task PushAsync_Results_AreMappedAndAnUnknownResultIsLeftUndecided()
    {
        // Arrange
        var other = Guid.Parse("7a1c0000-0000-7000-8000-000000000c02");
        var client = Client(_ => Json(HttpStatusCode.OK,
            $$"""{"results":[{"changeId":"{{Change}}","result":"rejected","reason":"time.end-not-after-start"},{"changeId":"{{other}}","result":"postponed","reason":null}]}"""));

        // Act
        var results = await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        results.Should().Equal(new PushResult(Change, PushOutcome.Rejected, "time.end-not-after-start"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.TooManyRequests, "")]
    [InlineData(HttpStatusCode.Forbidden, "<html>Blocked by the network</html>")]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"something-else"}""")]
    public async Task PushAsync_Bare403OrAnOutage_IsUnavailable(HttpStatusCode status, string body)
    {
        // Arrange: a proxy's 403 deletes nothing (ADR 0002 §10 step 1; Review Focus 3).
        var client = Client(_ => Raw(status, body.StartsWith('{') ? "application/json" : "text/html", body));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Fact]
    public async Task PushAsync_RevokedProblemForThisHousehold_IsMembershipRevoked()
    {
        // Arrange
        var client = Client(_ => Raw(HttpStatusCode.Forbidden, "application/problem+json",
            $$"""{"type":"{{SyncErrors.MembershipRevokedType}}","title":"membership-revoked","status":403,"householdId":"{{Household}}"}"""));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        var refused = (await act.Should().ThrowExactlyAsync<SyncRefusedException>()).Which;
        refused.Refusal.Should().Be(SyncRefusal.MembershipRevoked);
        refused.Household.Should().Be(Household);
    }

    [Fact]
    public async Task PushAsync_RevokedProblemForAnotherHousehold_IsUnavailable()
    {
        // Arrange
        var client = Client(_ => Raw(HttpStatusCode.Forbidden, "application/problem+json",
            $$"""{"type":"{{SyncErrors.MembershipRevokedType}}","status":403,"householdId":"7a1c0000-0000-7000-8000-0000000000a9"}"""));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"sync.consent-required"}""", SyncRefusal.ConsentRequired)]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"sync.not-a-member"}""", SyncRefusal.NotAMember)]
    [InlineData((HttpStatusCode)426, """{"code":"client-too-old"}""", SyncRefusal.ClientTooOld)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"sync.batch-too-large"}""", SyncRefusal.Malformed)]
    public async Task PushAsync_ServerCode_IsTheMatchingRefusal(HttpStatusCode status, string body, SyncRefusal refusal)
    {
        // Arrange
        var client = Client(_ => Raw(status, "application/json", body));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        (await act.Should().ThrowExactlyAsync<SyncRefusedException>()).Which.Refusal.Should().Be(refusal);
    }

    [Fact]
    public async Task PullAsync_HtmlBody_IsUnavailable()
    {
        // Arrange: a captive portal's 200 page (Review Focus 3).
        var client = Client(_ => Raw(HttpStatusCode.OK, "text/html", "<html>Sign in to the Wi-Fi</html>"));

        // Act
        Func<Task> act = async () => await client.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Fact]
    public async Task PullAsync_NoNetworkOrTheRefreshFailing_IsUnavailable()
    {
        // Arrange: the refreshing handler throws AuthUnavailableException when it cannot refresh.
        var offline = Client(_ => throw new HttpRequestException("Network is unreachable"));
        var refresh = Client(_ => throw new AuthUnavailableException("refresh failed"));

        // Act
        Func<Task> first = async () => await offline.PullAsync(Household, 0, null, CancellationToken.None);
        Func<Task> second = async () => await refresh.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        await first.Should().ThrowExactlyAsync<SyncUnavailableException>();
        await second.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Fact]
    public async Task PullAsync_Page_KeepsTheDetailsAsReceivedAndDropsRecordsItCannotUse()
    {
        // Arrange: an unknown type, a record with no payload and a tombstone with none are dropped; the cursor still moves past them (ruling, Task 13).
        Uri? asked = null;
        var client = Client(r =>
        {
            asked = r.RequestUri;
            return Json(HttpStatusCode.OK,
                $$"""
                {"records":[
                  {"entityType":"CareEntry","entityId":"{{Entry}}","version":{"wallClockMs":5,"counter":1,"deviceId":"{{Phone}}"},"isDeleted":false,"seq":7,
                   "careEntry":{"kind":"medication","subjectBabyId":"7a1c0000-0000-7000-8000-0000000000b1","subjectPerson":null,"isSession":false,
                     "startAt":"2026-10-01T09:00:00+02:00","endAt":null,"loggedBy":"user:7a1c0000-0000-7000-8000-0000000000c1","source":"manual",
                     "sourcePromptVersion":null,"note":null,"details":{"v":1,"drug":"vitamin D","doseDrops":1} } },
                  {"entityType":"Milestone","entityId":"{{Entry}}","version":{"wallClockMs":6,"counter":0,"deviceId":"{{Phone}}"},"isDeleted":false,"seq":8},
                  {"entityType":"CareEntry","entityId":"{{Entry}}","version":{"wallClockMs":7,"counter":0,"deviceId":"{{Phone}}"},"isDeleted":false,"seq":9},
                  {"entityType":"BabyProfile","entityId":"{{Entry}}","version":{"wallClockMs":8,"counter":0,"deviceId":"{{Phone}}"},"isDeleted":true,"seq":10}
                ],"cursor":10,"hasMore":true,"bootstrapHead":9}
                """);
        });

        // Act
        var page = await client.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        asked!.PathAndQuery.Should().Be($"/api/sync/pull?household={Household}&cursor=0&schemaVersion=1");
        var record = page.Records.Should().ContainSingle().Subject;
        ((CareEntryPayload)record.Payload).DetailsJson.Should().Be("""{"v":1,"drug":"vitamin D","doseDrops":1}""");
        ((CareEntryPayload)record.Payload).StartAt.Offset.Should().Be(TimeSpan.FromHours(2));
        page.Should().Match<PulledPage>(p => p.Cursor == 10 && p.HasMore && p.BootstrapHead == 9);
    }

    [Fact]
    public async Task PullAsync_DuringABootstrap_SendsTheHead()
    {
        // Arrange
        Uri? asked = null;
        var client = Client(r =>
        {
            asked = r.RequestUri;
            return Json(HttpStatusCode.OK, """{"records":[],"cursor":500,"hasMore":false,"bootstrapHead":0}""");
        });

        // Act
        await client.PullAsync(Household, 500, 0, CancellationToken.None);

        // Assert
        asked!.Query.Should().Contain("&bootstrapHead=0");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"status":"revoked"}""", MembershipStatus.Revoked)]
    [InlineData(HttpStatusCode.OK, """{"status":"active"}""", MembershipStatus.Active)]
    [InlineData(HttpStatusCode.NotFound, "", MembershipStatus.Unknown)]
    [InlineData(HttpStatusCode.OK, "<html>portal</html>", MembershipStatus.Unknown)]
    [InlineData(HttpStatusCode.OK, """{"status":"maybe"}""", MembershipStatus.Unknown)]
    public async Task GetMembershipAsync_Answer_OnlyAClearStatementCounts(HttpStatusCode status, string body, MembershipStatus expected)
    {
        // Arrange: delete only on a 200 that says revoked (ADR 0002 §10 step 3).
        var client = Client(_ => Raw(status, body.StartsWith('{') ? "application/json" : "text/html", body));

        // Act
        var answer = await client.GetMembershipAsync(Household, CancellationToken.None);

        // Assert
        answer.Should().Be(expected);
    }

    [Fact]
    public async Task RecordAsync_Consent_SendsTheCurrentWordingAppVersionAndLanguage()
    {
        // Arrange
        string? sent = null;
        var client = Client(async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        // Act
        await client.RecordAsync("0.11.0", "pl", CancellationToken.None);

        // Assert
        sent.Should().Be("""{"purpose":"care-data","wording":"care-data-v1","appVersion":"0.11.0","language":"pl"}""");
    }

    [Fact]
    public async Task PullAsync_EmptyDeviceOrNoDetails_DropsTheRecord()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.OK,
            $$"""
            {"records":[
              {"entityType":"CareEntry","entityId":"{{Entry}}","version":{"wallClockMs":5,"counter":1,"deviceId":"00000000-0000-0000-0000-000000000000"},"isDeleted":false,"seq":1,
               "careEntry":{"kind":"diaper","isSession":false,"startAt":"2026-10-01T09:00:00+02:00","loggedBy":"x","source":"manual","details":{"v":1} } },
              {"entityType":"CareEntry","entityId":"{{Entry}}","version":{"wallClockMs":5,"counter":1,"deviceId":"{{Phone}}"},"isDeleted":false,"seq":2,
               "careEntry":{"kind":"diaper","isSession":false,"startAt":"2026-10-01T09:00:00+02:00","loggedBy":"x","source":"manual" } }
            ],"cursor":2,"hasMore":false,"bootstrapHead":null}
            """));

        // Act
        var page = await client.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        page.Records.Should().BeEmpty();
        page.Cursor.Should().Be(2);
    }

    [Fact]
    public async Task PullAsync_TombstoneWithItsPayload_IsKept()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.OK,
            $$"""
            {"records":[
              {"entityType":"DuplicateDismissal","entityId":"{{Entry}}","version":{"wallClockMs":5,"counter":1,"deviceId":"{{Phone}}"},"isDeleted":true,"seq":1,
               "duplicateDismissal":{"entryA":"{{Entry}}","entryB":"{{Change}}" } }
            ],"cursor":1,"hasMore":false,"bootstrapHead":null}
            """));

        // Act
        var page = await client.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        page.Records.Should().ContainSingle().Which.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task PushAsync_InvalidDetailsJson_IsUnavailableNotARawJsonException()
    {
        // Arrange
        var client = Client(_ => Json(HttpStatusCode.OK, """{"results":[]}"""));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert("{not json")], CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Fact]
    public async Task PullAsync_UnauthorizedAfterRefresh_IsUnavailable()
    {
        // Arrange
        var client = Client(_ => Raw(HttpStatusCode.Unauthorized, "application/json", "{}"));

        // Act
        Func<Task> act = async () => await client.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Fact]
    public async Task PullAsync_HttpClientTimeout_IsUnavailable()
    {
        // Arrange: the caller's token is not cancelled, so this is the client's own timeout.
        var client = Client(_ => throw new TaskCanceledException("timeout", new TimeoutException()));

        // Act
        Func<Task> act = async () => await client.PullAsync(Household, 0, null, CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    [Fact]
    public async Task PullAsync_CallerCancelled_PropagatesCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Client(_ => throw new TaskCanceledException("cancelled"));

        // Act
        Func<Task> act = async () => await client.PullAsync(Household, 0, null, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PushAsync_SyncMalformed_IsMalformed()
    {
        // Arrange
        var client = Client(_ => Raw(HttpStatusCode.BadRequest, "application/json", """{"code":"sync.malformed"}"""));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        (await act.Should().ThrowExactlyAsync<SyncRefusedException>()).Which.Refusal.Should().Be(SyncRefusal.Malformed);
    }

    [Theory]
    [InlineData("""{"results":[{"changeId":""")]
    [InlineData("not json at all")]
    public async Task PushAsync_OkWithBrokenJson_IsUnavailable(string body)
    {
        // Arrange
        var client = Client(_ => Raw(HttpStatusCode.OK, "application/json", body));

        // Act
        Func<Task> act = async () => await client.PushAsync(Household, Phone, [Upsert()], CancellationToken.None);

        // Assert
        await act.Should().ThrowExactlyAsync<SyncUnavailableException>();
    }

    private static SyncClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> server) =>
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
