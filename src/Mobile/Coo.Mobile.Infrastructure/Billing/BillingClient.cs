using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coo.Mobile.Application.Billing;

namespace Coo.Mobile.Infrastructure.Billing;

internal sealed class BillingClient(HttpClient http) : IBillingApi
{
    private static readonly Uri VerifyUri = new("api/billing/verify-receipt", UriKind.Relative);

    public async Task<bool?> CheckEntitlementAsync(Guid householdId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri($"api/billing/entitlements/{householdId}", UriKind.Relative), cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            var body = await response.Content.ReadFromJsonAsync(BillingJsonContext.Default.EntitlementDto, cancellationToken);
            return body?.IsPremium;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<bool> VerifyReceiptAsync(Guid householdId, string productId, string purchaseToken, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsync(
                VerifyUri,
                JsonContent.Create(new VerifyReceiptDto(householdId, productId, purchaseToken), BillingJsonContext.Default.VerifyReceiptDto),
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

internal sealed record VerifyReceiptDto(Guid HouseholdId, string ProductId, string PurchaseToken);

internal sealed record EntitlementDto(bool IsPremium);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VerifyReceiptDto))]
[JsonSerializable(typeof(EntitlementDto))]
internal partial class BillingJsonContext : JsonSerializerContext
{
}
