using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>
/// How an answer becomes a result (Spec 010 §3.3). A refusal (4xx other than 429) throws <see cref="AuthRejectedException"/>
/// with the server's code; no network, a timeout, 429 and 5xx throw <see cref="AuthUnavailableException"/>: try later.
/// </summary>
internal static class AuthHttp
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new AuthUnavailableException("The server could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthUnavailableException("The server did not answer in time.", e);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
                throw new AuthUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The server answered {status}."));
            throw new AuthRejectedException(await CodeAsync(response, status, cancellationToken));
        }
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken)
                ?? throw new AuthUnavailableException("The server sent an empty answer.");
        }
        catch (JsonException e)
        {
            throw new AuthUnavailableException("The server sent an unreadable answer.", e);
        }
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response, int status, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync(AuthJsonContext.Default.ErrorResponse, cancellationToken);
            if (!string.IsNullOrEmpty(error?.Code))
                return error.Code;
        }
        catch (JsonException)
        {
            // No code in the body (the bearer handler's bare 401): the status stands in for it.
        }

        return string.Create(CultureInfo.InvariantCulture, $"http-{status}");
    }
}
