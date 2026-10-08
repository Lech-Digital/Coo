using System.Security.Claims;
using Coo.Api.Application.Billing;
using Coo.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text;

namespace Coo.Api.Hosting;

public static class BillingEndpoints
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

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

            var userId = AuthEndpoints.UserOf(user);
            
            var success = await useCase.ExecuteAsync(new HouseholdId(request.HouseholdId), userId.Value, request.ProductId, request.PurchaseToken, cancellationToken);
            return success ? Results.Ok() : Results.BadRequest("Verification failed");
        }).RequireAuthorization();

        group.MapGet("/entitlements/{householdId:guid}", async (
            Guid householdId,
            ClaimsPrincipal user,
            [FromServices] GetEntitlementStatus useCase,
            CancellationToken cancellationToken) =>
        {
            var userId = AuthEndpoints.UserOf(user);
            var isPremium = await useCase.ExecuteAsync(new HouseholdId(householdId), cancellationToken);
            return Results.Ok(new { IsPremium = isPremium });
        }).RequireAuthorization();

        group.MapPost("/rtdn", async (
            HttpRequest http,
            [FromServices] HandleRTDN useCase,
            CancellationToken cancellationToken) =>
        {
            var payload = await AuthEndpoints.TryReadJsonAsync<RtdnPayload>(http, cancellationToken);
            if (payload?.Message?.Data == null) return Results.Ok();
            
            var base64EncodedBytes = Convert.FromBase64String(payload.Message.Data);
            var decodedData = Encoding.UTF8.GetString(base64EncodedBytes);
            
            var devNotification = JsonSerializer.Deserialize<DeveloperNotification>(decodedData, _jsonOptions);
            if (devNotification?.SubscriptionNotification != null)
            {
                var token = devNotification.SubscriptionNotification.PurchaseToken;
                if (!string.IsNullOrEmpty(token))
                {
                    await useCase.ExecuteAsync(token, cancellationToken);
                }
            }

            return Results.Ok();
        });
    }
}

public sealed record VerifyReceiptRequest(Guid HouseholdId, string ProductId, string PurchaseToken);
public sealed record RtdnPayload(RtdnMessage? Message);
public sealed record RtdnMessage(string? Data);
public sealed record DeveloperNotification(SubscriptionNotification? SubscriptionNotification);
public sealed record SubscriptionNotification(string? PurchaseToken);
