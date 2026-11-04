using System.Net.Http.Json;
using Coo.Mobile.Application.Advisor;

namespace Coo.Mobile.Infrastructure.Advisor;

internal sealed class AdvisorClient(HttpClient http) : IAdvisorApi
{
    /// <summary>How long the phone waits for an Advisor answer; the request tells the server (<c>WaitSeconds</c>).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly Uri AskUri = new("api/advisor/ask", UriKind.Relative);
    private static readonly Uri SleepPlanUri = new("api/advisor/sleep-plan", UriKind.Relative);
    private static readonly Uri GenerateAvatarUri = new("api/advisor/generate-avatar", UriKind.Relative);

    public async Task<AskAdvisorResponse> AskAsync(AskAdvisorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.WaitSeconds ??= (int)Timeout.TotalSeconds;
        using var message = new HttpRequestMessage(HttpMethod.Post, AskUri)
        {
            Content = JsonContent.Create(request, AdvisorJsonContext.Default.AskAdvisorRequest)
        };
        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync(AdvisorJsonContext.Default.AskAdvisorResponse, cancellationToken);
        return answer ?? new AskAdvisorResponse();
    }

    public async Task<SleepPlanWordsResponse> WriteSleepPlanAsync(SleepPlanWordsRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, SleepPlanUri)
        {
            Content = JsonContent.Create(request, AdvisorJsonContext.Default.SleepPlanWordsRequest)
        };
        using var response = await http.SendAsync(message, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            return new SleepPlanWordsResponse { ErrorCode = "premium_required" };
        response.EnsureSuccessStatusCode();
        var words = await response.Content.ReadFromJsonAsync(AdvisorJsonContext.Default.SleepPlanWordsResponse, cancellationToken);
        return words ?? new SleepPlanWordsResponse();
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

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new GenerateAvatarResponse { ErrorCode = $"Server error ({(int)response.StatusCode}): {errorBody}" };
        }

        var result = await response.Content.ReadFromJsonAsync(AdvisorJsonContext.Default.GenerateAvatarResponse, cancellationToken);
        return result ?? new GenerateAvatarResponse();
    }
}
