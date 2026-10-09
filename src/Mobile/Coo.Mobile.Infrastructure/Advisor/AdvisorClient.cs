using System.Net.Http.Json;
using Coo.Mobile.Application.Advisor;

namespace Coo.Mobile.Infrastructure.Advisor;

internal sealed class AdvisorClient(HttpClient http) : IAdvisorApi
{
    private static readonly Uri AskUri = new("api/advisor/ask", UriKind.Relative);
    private static readonly Uri GenerateAvatarUri = new("api/advisor/generate-avatar", UriKind.Relative);

    public async Task<AskAdvisorResponse> AskAsync(AskAdvisorRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, AskUri)
        {
            Content = JsonContent.Create(request, AdvisorJsonContext.Default.AskAdvisorRequest)
        };
        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync(AdvisorJsonContext.Default.AskAdvisorResponse, cancellationToken);
        return answer ?? new AskAdvisorResponse();
    }

    public async Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, GenerateAvatarUri)
        {
            Content = JsonContent.Create(request, AdvisorJsonContext.Default.GenerateAvatarRequest)
        };
        using var response = await http.SendAsync(message, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            return new GenerateAvatarResponse { ErrorCode = "premium_required" };

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AdvisorJsonContext.Default.GenerateAvatarResponse, cancellationToken);
        return result ?? new GenerateAvatarResponse();
    }

    public async Task<bool> CheckEntitlementAsync(Guid householdId, CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, $"api/billing/entitlements/{householdId}");
            using var response = await http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("isPremium", out var prop) && prop.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
