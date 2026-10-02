using Coo.Api.Application.Auth;
using Google.Apis.Auth;
using Microsoft.Extensions.Logging;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>
/// Verifies a Google ID token (ADR 0003 §5) with Google's published keys, which the library caches. The signature,
/// <c>iss</c>, <c>aud</c> (Coo's Web client ID) and expiry are checked by the library, <c>email_verified</c> and the
/// presence of a nonce here. Every failure, including Google's keys being unreachable or timing out, returns null: fail
/// closed. Only the caller's own cancellation (the request was aborted) is let through.
/// </summary>
internal sealed partial class GoogleIdTokenVerifier : IExternalIdentityVerifier
{
    private readonly string? _webClientId;
    private readonly TimeProvider _time;
    private readonly ILogger<GoogleIdTokenVerifier> _logger;
    private readonly Func<string, GoogleJsonWebSignature.ValidationSettings, Task<GoogleJsonWebSignature.Payload>> _validate;

    public GoogleIdTokenVerifier(string? webClientId, TimeProvider time, ILogger<GoogleIdTokenVerifier> logger)
        : this(webClientId, time, logger, GoogleJsonWebSignature.ValidateAsync)
    {
    }

    /// <summary>For tests: <paramref name="validate"/> stands in for the library, so its failures can be simulated.</summary>
    internal GoogleIdTokenVerifier(
        string? webClientId, TimeProvider time, ILogger<GoogleIdTokenVerifier> logger,
        Func<string, GoogleJsonWebSignature.ValidationSettings, Task<GoogleJsonWebSignature.Payload>> validate)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(validate);
        _webClientId = webClientId;
        _time = time;
        _logger = logger;
        _validate = validate;
    }

    public async Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_webClientId) || string.IsNullOrWhiteSpace(idToken))
            return null;
        try
        {
            // The library takes no CancellationToken; its keys request has its own timeout.
            var payload = await _validate(idToken, new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = [_webClientId],
                Clock = new InjectedClock(_time),
                IssuedAtClockTolerance = TimeSpan.FromMinutes(1),
                ExpirationTimeClockTolerance = TimeSpan.FromMinutes(1),
            });
            return ToIdentity(payload);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogRefused(_logger, e.GetType().Name);
            return null;
        }
    }

    internal static ExternalIdentity? ToIdentity(GoogleJsonWebSignature.Payload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Issuer is not ("accounts.google.com" or "https://accounts.google.com")
            || payload.EmailVerified != true
            || string.IsNullOrEmpty(payload.Subject) || string.IsNullOrEmpty(payload.Email) || string.IsNullOrEmpty(payload.Nonce))
            return null;
        return new ExternalIdentity(payload.Subject, payload.Email, string.IsNullOrWhiteSpace(payload.Name) ? payload.Email : payload.Name, payload.Nonce);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Google ID token refused ({Reason})")]
    private static partial void LogRefused(ILogger logger, string reason);

    /// <summary>Google's library asks for the time through its own interface; it gets the injected clock.</summary>
    private sealed class InjectedClock(TimeProvider time) : Google.Apis.Util.IClock
    {
        public DateTime Now => time.GetLocalNow().DateTime;

        public DateTime UtcNow => time.GetUtcNow().UtcDateTime;
    }
}
