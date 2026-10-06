namespace Coo.Api.Application.Auth;

/// <summary>The error codes of Spec 010 §4.1. Once released, a code is never renamed (ADR 0006).</summary>
public static class AuthErrors
{
    public const string GoogleTokenInvalid = "auth.google-token-invalid";
    public const string RefreshInvalid = "auth.refresh-invalid";
    public const string RateLimited = "auth.rate-limited";
    public const string ConfirmationRequired = "account.confirmation-required";
}

/// <summary>The access token's claims (ADR 0003 §4). The key ID travels in the token's header as <c>kid</c>.</summary>
public static class AccessTokenClaims
{
    public const string User = "sub";
    public const string Session = "sid";
}
