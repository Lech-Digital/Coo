using System.Globalization;
using Coo.Api.Application.Auth;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Microsoft.Extensions.Time.Testing;

namespace Coo.Api.Application.Tests;

// The outbound ports, in memory. Only ports are faked; Domain objects are real.

/// <summary>Google, as far as the use cases can tell: a token it issued verifies; any other does not.</summary>
internal sealed class FakeGoogle : IExternalIdentityVerifier
{
    private readonly Dictionary<string, ExternalIdentity> _tokens = new(StringComparer.Ordinal);

    public string Issue(string subject, string nonce)
    {
        var token = "token-" + (_tokens.Count + 1).ToString(CultureInfo.InvariantCulture);
        _tokens[token] = new ExternalIdentity(subject, subject + "@example.com", "Parent " + subject, nonce);
        return token;
    }

    public Task<ExternalIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken) =>
        Task.FromResult(_tokens.GetValueOrDefault(idToken));
}

internal sealed class FakeNonces : IAuthNonceStore
{
    public Dictionary<string, (DateTimeOffset IssuedAt, DateTimeOffset? ConsumedAt)> Rows { get; } = new(StringComparer.Ordinal);

    public Task AddAsync(string nonceHash, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        Rows[nonceHash] = (issuedAt, null);
        return Task.CompletedTask;
    }

    public Task<bool> TryConsumeAsync(string nonceHash, DateTimeOffset issuedNotBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!Rows.TryGetValue(nonceHash, out var row) || row.ConsumedAt is not null || row.IssuedAt < issuedNotBefore)
            return Task.FromResult(false);
        Rows[nonceHash] = (row.IssuedAt, now);
        return Task.FromResult(true);
    }

    public Task<int> DeleteIssuedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var old = Rows.Where(r => r.Value.IssuedAt < cutoff).Select(r => r.Key).ToList();
        foreach (var key in old)
            Rows.Remove(key);
        return Task.FromResult(old.Count);
    }
}

internal sealed class FakeSessions : IDeviceSessionStore
{
    public List<Row> Rows { get; } = [];

    public Task CreateAsync(DeviceSession session, string refreshHash, CancellationToken cancellationToken)
    {
        Rows.RemoveAll(r => r.Session.UserId == session.UserId && r.Session.DeviceId == session.DeviceId);
        Rows.Add(new Row(session, refreshHash, null));
        return Task.CompletedTask;
    }

    /// <summary>Runs once inside the next <see cref="RotateAsync"/>, before its compare-and-swap: a concurrent request winning.</summary>
    public Action? BeforeRotate { get; set; }

    public Task<RefreshLookup?> FindByRefreshHashAsync(string refreshHash, CancellationToken cancellationToken)
    {
        var current = Rows.FirstOrDefault(r => r.RefreshHash == refreshHash);
        if (current is not null)
            return Task.FromResult<RefreshLookup?>(new RefreshLookup(current.Session, RefreshMatch.Current, current.RefreshHash));
        var previous = Rows.FirstOrDefault(r => r.PreviousRefreshHash == refreshHash);
        return Task.FromResult(previous is null ? null : new RefreshLookup(previous.Session, RefreshMatch.Previous, previous.RefreshHash));
    }

    public Task<bool> RotateAsync(DeviceSessionId id, string expectedHash, string newHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hook = BeforeRotate;
        BeforeRotate = null;
        hook?.Invoke();
        var index = Rows.FindIndex(r => r.Session.Id == id && r.RefreshHash == expectedHash && !r.Session.IsRevoked);
        if (index < 0 || Rows.Any(r => r.RefreshHash == newHash))
            return Task.FromResult(false); // The store's refresh hash is unique: a collision is a lost swap.
        var row = Rows[index];
        Rows[index] = new Row(row.Session with { LastUsedAt = now }, newHash, expectedHash);
        return Task.FromResult(true);
    }

    public void RotateDirectly(DeviceSessionId id, string expectedHash, string newHash)
    {
        var index = Rows.FindIndex(r => r.Session.Id == id && r.RefreshHash == expectedHash);
        Rows[index] = new Row(Rows[index].Session, newHash, expectedHash);
    }

    public Task RevokeAsync(DeviceSessionId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var index = Rows.FindIndex(r => r.Session.Id == id && !r.Session.IsRevoked);
        if (index >= 0)
            Rows[index] = Rows[index] with { Session = Rows[index].Session with { RevokedAt = now } };
        return Task.CompletedTask;
    }

    public Task<bool> IsActiveAsync(DeviceSessionId id, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Any(r => r.Session.Id == id && !r.Session.IsRevoked));

    public Task<IReadOnlyList<DeviceSession>> ListForUserAsync(UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceSession>>([.. Rows.Where(r => r.Session.UserId == user).Select(r => r.Session)]);

    public Task<bool> ExistsForDeviceAsync(UserId user, DeviceId device, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Any(r => r.Session.UserId == user && r.Session.DeviceId == device));

    public Row Of(DeviceSessionId id) => Rows.Single(r => r.Session.Id == id);

    internal sealed record Row(DeviceSession Session, string RefreshHash, string? PreviousRefreshHash);
}

internal sealed class FakeAccounts : IAccountStore
{
    public Dictionary<string, UserAccount> BySubject { get; } = new(StringComparer.Ordinal);

    public HashSet<HouseholdId> Households { get; } = [];

    public List<Membership> Memberships { get; } = [];

    public (UserId User, AccountDeletionPlan Plan)? Deleted { get; private set; }

    public Task<UserAccount?> FindByGoogleSubjectAsync(string subject, CancellationToken cancellationToken) =>
        Task.FromResult(BySubject.GetValueOrDefault(subject));

    public Task<UserAccount?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        Task.FromResult(BySubject.Values.FirstOrDefault(u => u.Id == id));

    public Task<bool> HouseholdExistsAsync(HouseholdId id, CancellationToken cancellationToken) =>
        Task.FromResult(Households.Contains(id));

    public Task CreateAccountAsync(NewAccount account, CancellationToken cancellationToken)
    {
        BySubject[account.GoogleSubject] = new UserAccount(account.UserId, account.Email, account.DisplayName);
        return CreateHouseholdAsync(account.Household, account.UserId, account.CreatedAt, cancellationToken);
    }

    public Task CreateHouseholdAsync(HouseholdId household, UserId owner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Households.Add(household);
        Memberships.Add(Membership.Create(household, owner, Role.Owner, now));
        return Task.CompletedTask;
    }

    public Task UpdateProfileAsync(UserAccount account, CancellationToken cancellationToken)
    {
        var subject = BySubject.Single(p => p.Value.Id == account.Id).Key;
        BySubject[subject] = account;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Membership>> ListMembershipsOfUserAsync(UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>([.. Memberships.Where(m => m.UserId == user)]);

    public Task<IReadOnlyList<Membership>> ListMembersAsync(HouseholdId household, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Membership>>([.. Memberships.Where(m => m.HouseholdId == household)]);

    public Task DeleteAccountAsync(UserId user, AccountDeletionPlan plan, CancellationToken cancellationToken)
    {
        Deleted = (user, plan);
        foreach (var subject in BySubject.Where(p => p.Value.Id == user).Select(p => p.Key).ToList())
            BySubject.Remove(subject);
        Memberships.RemoveAll(m => m.UserId == user || plan.HouseholdsToDelete.Contains(m.HouseholdId));
        foreach (var household in plan.HouseholdsToDelete)
            Households.Remove(household);
        return Task.CompletedTask;
    }
}

internal sealed class FakeTokens : ITokenIssuer
{
    public AccessToken Issue(UserId user, DeviceSessionId session, DateTimeOffset now) =>
        new("access-" + session.Value.ToString("N", CultureInfo.InvariantCulture), now + TimeSpan.FromMinutes(15));
}

/// <summary>The use cases over the fakes, and a whole sign-in as the phone does it.</summary>
internal sealed class AuthKit
{
    public static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Time { get; } = new(Start);

    public FakeGoogle Google { get; } = new();

    public FakeNonces Nonces { get; } = new();

    public FakeSessions Sessions { get; } = new();

    public FakeAccounts Accounts { get; } = new();

    public FakeTokens Tokens { get; } = new();

    public FakeConsents Consents { get; } = new();

    public IssueNonce IssueNonce() => new(Nonces, Time);

    public SignInWithGoogle SignIn() => new(Google, Nonces, Accounts, Sessions, Tokens, Time);

    public RefreshSession Refresh() => new(Sessions, Tokens, Time);

    public SignOutSession SignOut() => new(Sessions, Time);

    public PurgeAuthNonces Purge() => new(Nonces, Time);

    public GetMe GetMe() => new(Accounts, Sessions, Consents);

    public DeleteAccount DeleteAccount() => new(Accounts);

    /// <summary>A nonce from the server, a Google token carrying it, then <c>/api/auth/google</c>.</summary>
    public async Task<SignInResult> SignInAsync(string subject, DeviceId device, HouseholdId household)
    {
        var nonce = await IssueNonce().ExecuteAsync(CancellationToken.None);
        var result = await SignIn().ExecuteAsync(
            new GoogleSignInCommand(Google.Issue(subject, nonce), "Pixel 10", device, household), CancellationToken.None);
        return result.Value;
    }
}
