using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Coo.Api.Application.Billing;
using Coo.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Coo.Api.Hosting;

public static class BillingEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void MapBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/billing");

        group.MapPost("/verify-receipt", async (
            HttpRequest http,
            ClaimsPrincipal user,
            [FromServices] VerifyReceipt useCase,
            CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<VerifyReceiptRequest>(http, cancellationToken);
            if (request == null || request.HouseholdId == Guid.Empty || string.IsNullOrWhiteSpace(request.ProductId) || string.IsNullOrWhiteSpace(request.PurchaseToken))
                return Results.BadRequest();

            var outcome = await useCase.ExecuteAsync(
                new HouseholdId(request.HouseholdId), AuthEndpoints.UserOf(user), request.ProductId, request.PurchaseToken, cancellationToken);
            return outcome switch
            {
                VerifyReceiptOutcome.Verified => Results.Ok(),
                VerifyReceiptOutcome.NotAMember => Results.Forbid(),
                VerifyReceiptOutcome.OtherHousehold => Results.Conflict(new { Code = "billing.other-household" }),
                VerifyReceiptOutcome.UnknownProduct => Results.BadRequest(new { Code = "billing.unknown-product" }),
                _ => Results.BadRequest(new { Code = "billing.invalid-purchase" }),
            };
        }).RequireAuthorization();

        group.MapGet("/entitlements/{householdId:guid}", async (
            Guid householdId,
            ClaimsPrincipal user,
            [FromServices] GetEntitlementStatus useCase,
            CancellationToken cancellationToken) =>
        {
            var isPremium = await useCase.ExecuteAsync(new HouseholdId(householdId), AuthEndpoints.UserOf(user), cancellationToken);
            return isPremium is { } premium ? Results.Ok(new { IsPremium = premium }) : Results.Forbid();
        }).RequireAuthorization();

        // Google Pub/Sub pushes here (RTDN). The push subscription's URL carries ?token=<Billing:RtdnToken>, so nobody
        // else can make the server ask Google about tokens. Without a configured token the webhook is off.
        group.MapPost("/rtdn", async (
            HttpRequest http,
            [FromQuery] string? token,
            [FromServices] IConfiguration configuration,
            [FromServices] HandleRTDN useCase,
            CancellationToken cancellationToken) =>
        {
            var expected = configuration["Billing:RtdnToken"];
            if (string.IsNullOrEmpty(expected) || token is null
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
                return Results.Unauthorized();

            var payload = await AuthEndpoints.TryReadJsonAsync<RtdnPayload>(http, cancellationToken);
            if (payload?.Message?.Data == null) return Results.Ok();

            DeveloperNotification? notification;
            try
            {
                notification = JsonSerializer.Deserialize<DeveloperNotification>(Encoding.UTF8.GetString(Convert.FromBase64String(payload.Message.Data)), JsonOptions);
            }
            catch (Exception e) when (e is FormatException or JsonException)
            {
                // Acknowledged, so Pub/Sub does not retry a message that can never be read.
                return Results.Ok();
            }

            // A subscription change, a one-time (lifetime) purchase, or a refund of either: each names its token, and
            // the purchase is read again from Google.
            var changed = notification?.SubscriptionNotification?.PurchaseToken
                ?? notification?.OneTimeProductNotification?.PurchaseToken
                ?? notification?.VoidedPurchaseNotification?.PurchaseToken;
            if (changed is { Length: > 0 } purchaseToken)
                await useCase.ExecuteAsync(purchaseToken, cancellationToken);

            return Results.Ok();
        });
    }
}

public sealed record VerifyReceiptRequest(Guid HouseholdId, string ProductId, string PurchaseToken);
public sealed record RtdnPayload(RtdnMessage? Message);
public sealed record RtdnMessage(string? Data);
public sealed record DeveloperNotification(
    SubscriptionNotification? SubscriptionNotification,
    SubscriptionNotification? OneTimeProductNotification = null,
    SubscriptionNotification? VoidedPurchaseNotification = null);
public sealed record SubscriptionNotification(string? PurchaseToken);
