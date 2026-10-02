using System.Net.Http.Json;
using Coo.Contracts.Auth;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary><c>/api/auth</c> without an access token (Spec 010 §4.1). Contracts are mapped to the application's records here.</summary>
internal sealed class AuthClient(HttpClient http) : IAuthApi
{
    private static readonly Uri NonceUri = new("api/auth/nonce", UriKind.Relative);
    private static readonly Uri GoogleUri = new("api/auth/google", UriKind.Relative);
    private static readonly Uri RefreshUri = new("api/auth/refresh", UriKind.Relative);

    public async Task<string> GetNonceAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, NonceUri);
        using var response = await AuthHttp.SendAsync(http, request, cancellationToken);
        return (await AuthHttp.ReadAsync(response, AuthJsonContext.Default.NonceResponse, cancellationToken)).Nonce;
    }

    public async Task<SignInGrant> SignInWithGoogleAsync(GoogleSignIn request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new GoogleSignInRequest(request.IdToken, request.DeviceName, request.DeviceId.Value, request.ProvisionalHouseholdId.Value);
        using var message = new HttpRequestMessage(HttpMethod.Post, GoogleUri) { Content = JsonContent.Create(body, AuthJsonContext.Default.GoogleSignInRequest) };
        using var response = await AuthHttp.SendAsync(http, message, cancellationToken);
        var s = await AuthHttp.ReadAsync(response, AuthJsonContext.Default.SessionResponse, cancellationToken);
        return new SignInGrant(
            new SessionTokens(s.AccessToken, s.AccessTokenExpiresAt, s.RefreshToken),
            new AccountProfile(new UserId(s.User.Id), s.User.DisplayName, s.User.Email),
            new HouseholdId(s.HouseholdId),
            Outcome(s.Outcome));
    }

    public async Task<SessionTokens> RefreshAsync(string refreshToken, string nextRefreshToken, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, RefreshUri)
        {
            Content = JsonContent.Create(new RefreshRequest(refreshToken, nextRefreshToken), AuthJsonContext.Default.RefreshRequest),
        };
        using var response = await AuthHttp.SendAsync(http, message, cancellationToken);
        var t = await AuthHttp.ReadAsync(response, AuthJsonContext.Default.TokensResponse, cancellationToken);
        return new SessionTokens(t.AccessToken, t.AccessTokenExpiresAt, t.RefreshToken);
    }

    private static SignInOutcome Outcome(string value) => value switch
    {
        AuthOutcomes.Adopted => SignInOutcome.Adopted,
        AuthOutcomes.Created => SignInOutcome.Created,
        AuthOutcomes.Existing => SignInOutcome.Existing,
        AuthOutcomes.Joined => SignInOutcome.Joined,
        _ => throw new AuthUnavailableException("The server reported an outcome this version does not know."),
    };
}
