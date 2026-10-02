using Coo.Domain.Common;

namespace Coo.Api.Application.Auth;

public sealed record GoogleSignInCommand(string IdToken, string DeviceName, DeviceId DeviceId, HouseholdId ProvisionalHouseholdId);

/// <summary>Which case of Spec 010 §3.2 happened.</summary>
public enum SignInOutcome
{
    Adopted,
    Created,
    Existing,
    Joined,
}

public sealed record IssuedSession(DeviceSessionId Id, AccessToken AccessToken, string RefreshToken);

public sealed record SignInResult(IssuedSession Session, UserAccount User, HouseholdId Household, SignInOutcome Outcome);

/// <summary><c>POST /api/auth/nonce</c>: a fresh single-use nonce. Only its hash is stored.</summary>
public sealed class IssueNonce(IAuthNonceStore nonces, TimeProvider time)
{
    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        var nonce = AuthSecrets.NewToken();
        await nonces.AddAsync(AuthSecrets.Hash(nonce), time.GetUtcNow(), cancellationToken);
        return nonce;
    }
}

/// <summary><c>POST /api/auth/google</c> (Spec 010 §3).</summary>
public sealed class SignInWithGoogle(
    IExternalIdentityVerifier verifier, IAuthNonceStore nonces, IAccountStore accounts, IDeviceSessionStore sessions,
    ITokenIssuer tokens, TimeProvider time)
{
    public const int DeviceNameMaxLength = 60;
    public static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(5);

    public async Task<Result<SignInResult>> ExecuteAsync(GoogleSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Fails closed (ADR 0003 §5): a token the verifier does not vouch for, or a nonce that is not fresh and unused,
        // creates nothing. The nonce is consumed only once the token has verified.
        var identity = await verifier.VerifyAsync(command.IdToken, cancellationToken);
        if (identity is null)
            return Result<SignInResult>.Failure(AuthErrors.GoogleTokenInvalid);
        var now = time.GetUtcNow();
        if (!await nonces.TryConsumeAsync(AuthSecrets.Hash(identity.Nonce), now - NonceLifetime, now, cancellationToken))
            return Result<SignInResult>.Failure(AuthErrors.GoogleTokenInvalid);

        var (user, household, outcome) = await FindOrCreateAsync(identity, command, now, cancellationToken);
        var session = await StartSessionAsync(sessions, tokens, user.Id, command.DeviceId, DeviceName(command.DeviceName), now, cancellationToken);
        return new SignInResult(session, user, household, outcome);
    }

    /// <summary>A new device session and its first tokens. A previous session of this user on this device is replaced.</summary>
    private static async Task<IssuedSession> StartSessionAsync(
        IDeviceSessionStore sessions, ITokenIssuer tokens, UserId user, DeviceId device, string deviceName, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(tokens);
        var id = new DeviceSessionId(Guid.CreateVersion7(now));
        var refreshToken = AuthSecrets.NewToken();
        await sessions.CreateAsync(new DeviceSession(id, user, device, deviceName, now, now, null), AuthSecrets.Hash(refreshToken), cancellationToken);
        return new IssuedSession(id, tokens.Issue(user, id, now), refreshToken);
    }

    private async Task<(UserAccount User, HouseholdId Household, SignInOutcome Outcome)> FindOrCreateAsync(
        ExternalIdentity identity, GoogleSignInCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var user = await accounts.FindByGoogleSubjectAsync(identity.Subject, cancellationToken);
        if (user is null)
        {
            var (household, outcome) = await HouseholdForNewAccountAsync(command.ProvisionalHouseholdId, now, cancellationToken);
            user = new UserAccount(new UserId(Guid.CreateVersion7(now)), identity.Email, identity.DisplayName);
            await accounts.CreateAccountAsync(
                new NewAccount(user.Id, identity.Subject, identity.Email, identity.DisplayName, household, now), cancellationToken);
            return (user, household, outcome);
        }

        if (user.Email != identity.Email || user.DisplayName != identity.DisplayName)
        {
            user = user with { Email = identity.Email, DisplayName = identity.DisplayName };
            await accounts.UpdateProfileAsync(user, cancellationToken);
        }

        var membership = await accounts.FindEarliestMembershipAsync(user.Id, cancellationToken);
        if (membership is null)
        {
            var (household, outcome) = await HouseholdForNewAccountAsync(command.ProvisionalHouseholdId, now, cancellationToken);
            await accounts.CreateHouseholdAsync(household, user.Id, now, cancellationToken);
            return (user, household, outcome);
        }

        // The same phone again: it already holds the account's household, or it has had a session for this user.
        var samePhone = membership.HouseholdId == command.ProvisionalHouseholdId
            || await sessions.ExistsForDeviceAsync(user.Id, command.DeviceId, cancellationToken);
        return (user, membership.HouseholdId, samePhone ? SignInOutcome.Existing : SignInOutcome.Joined);
    }

    /// <summary>The phone's provisional ID when it is unused (ADR 0003 §7), else a new one.</summary>
    private async Task<(HouseholdId Household, SignInOutcome Outcome)> HouseholdForNewAccountAsync(
        HouseholdId provisional, DateTimeOffset now, CancellationToken cancellationToken) =>
        await accounts.HouseholdExistsAsync(provisional, cancellationToken)
            ? (new HouseholdId(Guid.CreateVersion7(now)), SignInOutcome.Created)
            : (provisional, SignInOutcome.Adopted);

    private static string DeviceName(string? sent)
    {
        var name = (sent ?? "").Trim();
        if (name.Length == 0)
            return "Phone";
        return name.Length <= DeviceNameMaxLength ? name : name[..DeviceNameMaxLength];
    }
}
