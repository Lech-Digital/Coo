using System.Net.Http.Json;
using Coo.Mobile.Application.Advisor;

namespace Coo.Mobile.Infrastructure.Advisor;

internal sealed class AdvisorClient(HttpClient http) : IAdvisorApi
{
    private static readonly Uri AskUri = new("api/advisor/ask", UriKind.Relative);

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
}
