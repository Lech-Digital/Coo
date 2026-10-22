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
        googlePlay.VerifyPurchaseAsync(Products.Premium, "token123", Arg.Any<CancellationToken>())
            .Returns(new GooglePlayPurchaseInfo("token123", Products.Premium, api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(30), SubscriptionStatus.Active, true));

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
        var req = new { HouseholdId = householdId, ProductId = Products.Premium, PurchaseToken = "token123" };
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
        googlePlay.VerifyPurchaseAsync(Products.Premium, "token456", Arg.Any<CancellationToken>())
            .Returns(new GooglePlayPurchaseInfo("token456", Products.Premium, api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(30), SubscriptionStatus.Active, true));

        var factory = api.Factory.WithWebHostBuilder(host =>
        {
            host.UseSetting("Billing:RtdnToken", "push-secret");
            host.ConfigureTestServices(services => services.AddSingleton(googlePlay));
        });

        using var client = factory.CreateClient();

        var session = await Coo.Api.IntegrationTests.SyncCalls.SignInWithConsentAsync(client, "billing-user2", Coo.Api.IntegrationTests.Calls.Id(9003), Coo.Api.IntegrationTests.Calls.Id(9004));
        var token = session.AccessToken;
        var householdId = session.HouseholdId;

        // Verify receipt first to create the subscription in DB
        var req = new { HouseholdId = householdId, ProductId = Products.Premium, PurchaseToken = "token456" };
        await Coo.Api.IntegrationTests.Calls.SendAsync(client, HttpMethod.Post, new Uri("/api/billing/verify-receipt", UriKind.Relative), token, req);

        // Now mock google play to return canceled status
        googlePlay.VerifyPurchaseAsync(Products.Premium, "token456", Arg.Any<CancellationToken>())
            .Returns(new GooglePlayPurchaseInfo("token456", Products.Premium, api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(-1), SubscriptionStatus.Expired, false));

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

        var rtdnRes = await client.PostAsJsonAsync("/api/billing/rtdn?token=push-secret", payload);
        Assert.Equal(HttpStatusCode.OK, rtdnRes.StatusCode);

        // Check if premium was revoked due to cancellation
        var entitlementReq = await Coo.Api.IntegrationTests.Calls.SendAsync(client, HttpMethod.Get, new Uri($"/api/billing/entitlements/{householdId}", UriKind.Relative), token, null);
        var status = await entitlementReq.Content.ReadFromJsonAsync<EntitlementResponse>();
        Assert.False(status!.IsPremium);
    }

    [Fact]
    public async Task RTDN_WithoutTheConfiguredToken_IsUnauthorized()
    {
        // Arrange
        var factory = api.Factory.WithWebHostBuilder(host => host.UseSetting("Billing:RtdnToken", "push-secret"));
        using var client = factory.CreateClient();
        var payload = new { Message = new { Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")) } };

        // Act
        var none = await client.PostAsJsonAsync("/api/billing/rtdn", payload);
        var wrong = await client.PostAsJsonAsync("/api/billing/rtdn?token=guess", payload);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task VerifyReceipt_ForAHouseholdTheCallerIsNotIn_IsForbiddenAndGrantsNothing()
    {
        // Arrange
        var googlePlay = Substitute.For<IGooglePlayValidator>();
        googlePlay.VerifyPurchaseAsync(Products.Premium, "token789", Arg.Any<CancellationToken>())
            .Returns(new GooglePlayPurchaseInfo("token789", Products.Premium, api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(30), SubscriptionStatus.Active, true));
        var factory = api.Factory.WithWebHostBuilder(host => host.ConfigureTestServices(services => services.AddSingleton(googlePlay)));
        using var client = factory.CreateClient();
        var buyer = await SyncCalls.SignInWithConsentAsync(client, "billing-user3", Calls.Id(9005), Calls.Id(9006));
        var stranger = await SyncCalls.SignInWithConsentAsync(client, "billing-user4", Calls.Id(9007), Calls.Id(9008));

        // Act
        var res = await Calls.SendAsync(client, HttpMethod.Post, new Uri("/api/billing/verify-receipt", UriKind.Relative), buyer.AccessToken,
            new { HouseholdId = stranger.HouseholdId, ProductId = Products.Premium, PurchaseToken = "token789" });
        var peek = await Calls.SendAsync(client, HttpMethod.Get, new Uri($"/api/billing/entitlements/{stranger.HouseholdId}", UriKind.Relative), buyer.AccessToken, null);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, peek.StatusCode);
    }

    [Fact]
    public async Task VerifyReceipt_TokenBoughtForAnotherHousehold_IsRefused()
    {
        // Arrange: Play says the purchase was made for some other family.
        var googlePlay = Substitute.For<IGooglePlayValidator>();
        googlePlay.VerifyPurchaseAsync(Products.Premium, "token790", Arg.Any<CancellationToken>())
            .Returns(new GooglePlayPurchaseInfo("token790", Products.Premium, api.Factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(30), SubscriptionStatus.Active, true, Calls.Id(9999).ToString()));
        var factory = api.Factory.WithWebHostBuilder(host => host.ConfigureTestServices(services => services.AddSingleton(googlePlay)));
        using var client = factory.CreateClient();
        var session = await SyncCalls.SignInWithConsentAsync(client, "billing-user5", Calls.Id(9009), Calls.Id(9010));

        // Act
        var res = await Calls.SendAsync(client, HttpMethod.Post, new Uri("/api/billing/verify-receipt", UriKind.Relative), session.AccessToken,
            new { HouseholdId = session.HouseholdId, ProductId = Products.Premium, PurchaseToken = "token790" });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Entitlements_FamilyOfAComplimentaryAccount_IsPremiumWithoutAPurchase()
    {
        // Arrange: the Play reviewers' test account gets Premium from configuration, not a fake purchase.
        var factory = api.Factory.WithWebHostBuilder(host => host.UseSetting("Billing:ComplimentaryEmails", "someone@else.com, Billing-Reviewer@Example.com"));
        using var client = factory.CreateClient();
        var reviewer = await SyncCalls.SignInWithConsentAsync(client, "billing-reviewer", Calls.Id(9011), Calls.Id(9012));
        var other = await SyncCalls.SignInWithConsentAsync(client, "billing-user6", Calls.Id(9013), Calls.Id(9014));

        // Act
        var forReviewer = await (await Calls.SendAsync(client, HttpMethod.Get, new Uri($"/api/billing/entitlements/{reviewer.HouseholdId}", UriKind.Relative), reviewer.AccessToken, null))
            .Content.ReadFromJsonAsync<EntitlementResponse>();
        var forOther = await (await Calls.SendAsync(client, HttpMethod.Get, new Uri($"/api/billing/entitlements/{other.HouseholdId}", UriKind.Relative), other.AccessToken, null))
            .Content.ReadFromJsonAsync<EntitlementResponse>();

        // Assert
        Assert.True(forReviewer!.IsPremium);
        Assert.False(forOther!.IsPremium);
    }

    private sealed record EntitlementResponse(bool IsPremium);
}
