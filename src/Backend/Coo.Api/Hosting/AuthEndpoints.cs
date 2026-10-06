using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Coo.Api.Application.Auth;
using Coo.Contracts.Auth;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Coo.Api.Hosting;

/// <summary>
/// Sign-in and sessions (Spec 010 §4.1). Contracts are mapped to the use cases here; refusals are codes, never server
/// text. <c>/api/auth/*</c> is rate-limited per client address (forwarded headers are trusted, Spec 008).
/// </summary>
internal static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";
    public const int DefaultPermitsPerMinute = 20;

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, int permitsPerMinute) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, cancellationToken) =>
                new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse(AuthErrors.RateLimited), cancellationToken));
            options.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting(RateLimitPolicy);

        auth.MapPost("/nonce", async (IssueNonce issue, CancellationToken cancellationToken) =>
            Results.Ok(new NonceResponse(await issue.ExecuteAsync(cancellationToken))));

        // The body is read here, not bound: a malformed request (no body, unparsable JSON, a missing token, device ID or
        // household ID) is refused like a bad token, 401 with the code, not the binder's bare 400 (ruling).
        auth.MapPost("/google", async Task<IResult> (HttpRequest http, SignInWithGoogle signIn, CancellationToken cancellationToken) =>
        {
            var request = await TryReadJsonAsync<GoogleSignInRequest>(http, cancellationToken);
            if (request is null || string.IsNullOrWhiteSpace(request.IdToken) || request.DeviceId == Guid.Empty || request.ProvisionalHouseholdId == Guid.Empty)
                return Refused(AuthErrors.GoogleTokenInvalid, StatusCodes.Status401Unauthorized);

            var result = await signIn.ExecuteAsync(
                new GoogleSignInCommand(request.IdToken, request.DeviceName ?? "", new DeviceId(request.DeviceId), new HouseholdId(request.ProvisionalHouseholdId)),
                cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code, StatusCodes.Status401Unauthorized);

            var s = result.Value;
            return Results.Ok(new SessionResponse(
                s.Session.AccessToken.Value, s.Session.AccessToken.ExpiresAt, s.Session.RefreshToken, User(s.User), s.Household.Value, Outcome(s.Outcome)));
        });

        auth.MapPost("/refresh", async Task<IResult> (RefreshRequest request, RefreshSession refresh, CancellationToken cancellationToken) =>
        {
            var result = await refresh.ExecuteAsync(request.RefreshToken ?? "", request.NextRefreshToken, cancellationToken);
            return result.IsFailure
                ? Refused(result.Error!.Code, StatusCodes.Status401Unauthorized)
                : Results.Ok(new TokensResponse(result.Value.AccessToken.Value, result.Value.AccessToken.ExpiresAt, result.Value.RefreshToken));
        });

        auth.MapPost("/sign-out", async Task<IResult> (ClaimsPrincipal principal, SignOutSession signOut, CancellationToken cancellationToken) =>
        {
            await signOut.ExecuteAsync(SessionOf(principal), cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/api/me", async Task<IResult> (ClaimsPrincipal principal, GetMe getMe, CancellationToken cancellationToken) =>
        {
            var current = SessionOf(principal);
            var me = await getMe.ExecuteAsync(UserOf(principal), cancellationToken);
            if (me is null)
                return Results.Unauthorized();
            return Results.Ok(new MeResponse(
                User(me.User),
                me.Membership is null ? null : new MembershipDto(me.Membership.HouseholdId.Value, RoleName(me.Membership.Role)),
                [.. me.Devices.Select(d => new DeviceSessionDto(d.Id.Value, d.DeviceName, d.LastUsedAt, d.Id == current))],
                me.Consent is null ? null : new ConsentDto(me.Consent.Wording, me.Consent.GivenAt),
                [.. (me.Memberships ?? []).OrderBy(m => m.JoinedAt).ThenBy(m => m.HouseholdId.Value).Select(m => new MembershipDto(m.HouseholdId.Value, RoleName(m.Role), m.JoinedAt))]));
        }).RequireAuthorization();

        app.MapDelete("/api/account", async Task<IResult> (
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeleteAccountRequest? request, ClaimsPrincipal principal,
            DeleteAccount deleteAccount, CancellationToken cancellationToken) =>
        {
            var result = await deleteAccount.ExecuteAsync(UserOf(principal), request?.Confirm == true, cancellationToken);
            return result.IsFailure ? Refused(result.Error!.Code, StatusCodes.Status400BadRequest) : Results.NoContent();
        }).RequireAuthorization();
    }

    internal static IResult Refused(string code, int status) => Results.Json(new ErrorResponse(code), statusCode: status);

    /// <summary>
    /// The body as <typeparamref name="T"/> (web JSON options), or null when it is missing, not JSON, does not parse, or
    /// names a charset that cannot be read.
    /// </summary>
    internal static async Task<T?> TryReadJsonAsync<T>(HttpRequest http, CancellationToken cancellationToken) where T : class
    {
        if (!http.HasJsonContentType())
            return null;
        try
        {
            return await http.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: an unknown charset in the content type.
            return null;
        }
    }

    private static UserDto User(UserAccount user) => new(user.Id.Value, user.Email, user.DisplayName);

    // The bearer handler has validated the token and its session (JwtBearerSetup), so both claims are present.
    internal static UserId UserOf(ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirst(AccessTokenClaims.User)!.Value, CultureInfo.InvariantCulture));

    private static DeviceSessionId SessionOf(ClaimsPrincipal principal) =>
        new(Guid.Parse(principal.FindFirst(AccessTokenClaims.Session)!.Value, CultureInfo.InvariantCulture));

    private static string Outcome(SignInOutcome outcome) => outcome switch
    {
        SignInOutcome.Adopted => AuthOutcomes.Adopted,
        SignInOutcome.Created => AuthOutcomes.Created,
        SignInOutcome.Existing => AuthOutcomes.Existing,
        SignInOutcome.Joined => AuthOutcomes.Joined,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown outcome."),
    };

    internal static string RoleName(Role role) => role switch
    {
        Role.Owner => AuthRoles.Owner,
        Role.Parent => AuthRoles.Parent,
        Role.Caregiver => AuthRoles.Caregiver,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role."),
    };
}
