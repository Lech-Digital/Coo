using System.Net;
using System.Net.Http.Headers;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>
/// Adds the access token, refreshing it when it is about to expire, and once more after a <c>401</c> (Spec 010 §3.3).
/// Signed out, it answers <c>401</c> itself without touching the network. A retried request carries the same body.
/// </summary>
internal sealed class AuthenticatingHandler(TokenRefresher refresher) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await refresher.GetAccessTokenAsync(cancellationToken);
        if (token is null)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };

        // Read the body before the first send: a request is sent once, so a retry needs its own copy.
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        string? fresh;
        try
        {
            fresh = await refresher.RefreshAsync(token, cancellationToken);
        }
        catch
        {
            // "Try later" (AuthUnavailableException) or cancelled: the session stays, the refused answer goes.
            response.Dispose();
            throw;
        }

        if (fresh is null)
            return response;

        response.Dispose();
        var retry = Copy(request, body);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(retry, cancellationToken);
    }

    /// <summary>The retry travels with its response (<c>response.RequestMessage</c>), like the original.</summary>
    private static HttpRequestMessage Copy(HttpRequestMessage request, byte[]? body)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var option in request.Options)
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;
        foreach (var header in request.Headers.Where(h => h.Key != "Authorization"))
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers.Where(h => h.Key != "Content-Length"))
                copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }
}
