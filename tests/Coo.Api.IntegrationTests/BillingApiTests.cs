using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Coo.Api.Application.Billing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Coo.Api.IntegrationTests;

public sealed class BillingApiTests(AuthApi api) : IClassFixture<AuthApi>
{
    [Fact]
    public async Task VerifyReceipt_ValidReceipt_GrantsPremiumAndCanBeChecked()
    {
        var googlePlay = Substitute.For<IGooglePlayValidator>();
        googlePlay.VerifyPurchaseAsync("coo_premium", "token123", Arg.Any<CancellationToken>())
            .Returns(new GooglePlayPurchaseInfo("token123", "coo_premium", api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(30), SubscriptionStatus.Active, true));

        var factory = api.Factory.WithWebHostBuilder(host =>
        {
            host.ConfigureTestServices(services => services.AddSingleton(googlePlay));
        });

        using var client = factory.CreateClient();

        var session = await Coo.Api.IntegrationTests.SyncCalls.SignInWithConsentAsync(client, "billing-user1", Coo.Api.IntegrationTests.Calls.Id(9001), Coo.Api.IntegrationTests.Calls.Id(9002));
        var token = session.AccessToken;
        var householdId = session.HouseholdId;

        // Ensure not premium initially
        var entitlementReq1 = await Coo.Api.IntegrationTests.Calls.SendAsync(client, HttpMethod.Get, new Uri($"/api/billing/entitlements/{householdId}", UriKind.Relative), token, null);
        var status1 = await entitlementReq1.Content.ReadFromJsonAsync<EntitlementResponse>();
        Assert.False(status1!.IsPremium);

        // Verify receipt
        var req = new { HouseholdId = householdId, ProductId = "coo_premium", PurchaseToken = "token123" };
        var res = await Coo.Api.IntegrationTests.Calls.SendAsync(client, HttpMethod.Post, new Uri("/api/billing/verify-receipt", UriKind.Relative), token, req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Ensure premium after verification
        var entitlementReq2 = await Coo.Api.IntegrationTests.Calls.SendAsync(client, HttpMethod.Get, new Uri($"/api/billing/entitlements/{householdId}", UriKind.Relative), token, null);
        var status2 = await entitlementReq2.Content.ReadFromJsonAsync<EntitlementResponse>();
        Assert.True(status2!.IsPremium);
    }

    [Fact]
    public async Task RTDN_ValidPayload_UpdatesSubscription()
    {
        var googlePlay = Substitute.For<IGooglePlayValidator>();
        googlePlay.VerifyPurchaseAsync("coo_premium", "token456", Arg.Any<CancellationToken>())
            .Returns(new GooglePlayPurchaseInfo("token456", "coo_premium", api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(30), SubscriptionStatus.Active, true));

        var factory = api.Factory.WithWebHostBuilder(host =>
        {
            host.ConfigureTestServices(services => services.AddSingleton(googlePlay));
        });

        using var client = factory.CreateClient();

        var session = await Coo.Api.IntegrationTests.SyncCalls.SignInWithConsentAsync(client, "billing-user2", Coo.Api.IntegrationTests.Calls.Id(9003), Coo.Api.IntegrationTests.Calls.Id(9004));
        var token = session.AccessToken;
        var householdId = session.HouseholdId;

        // Verify receipt first to create the subscription in DB
        var req = new { HouseholdId = householdId, ProductId = "coo_premium", PurchaseToken = "token456" };
        await Coo.Api.IntegrationTests.Calls.SendAsync(client, HttpMethod.Post, new Uri("/api/billing/verify-receipt", UriKind.Relative), token, req);

        // Now mock google play to return canceled status
        googlePlay.VerifyPurchaseAsync("coo_premium", "token456", Arg.Any<CancellationToken>())
            .Returns(new GooglePlayPurchaseInfo("token456", "coo_premium", api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(-1), SubscriptionStatus.Expired, false));

        // Send RTDN webhook (anonymous)
        var payload = new
        {
            Message = new
            {
                Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    SubscriptionNotification = new { PurchaseToken = "token456" }
                })))
            }
        };

        var rtdnRes = await client.PostAsJsonAsync("/api/billing/rtdn", payload);
        Assert.Equal(HttpStatusCode.OK, rtdnRes.StatusCode);

        // Check if premium was revoked due to cancellation
        var entitlementReq = await Coo.Api.IntegrationTests.Calls.SendAsync(client, HttpMethod.Get, new Uri($"/api/billing/entitlements/{householdId}", UriKind.Relative), token, null);
        var status = await entitlementReq.Content.ReadFromJsonAsync<EntitlementResponse>();
        Assert.False(status!.IsPremium);
    }

    private sealed record EntitlementResponse(bool IsPremium);
}
