using System.Globalization;
using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Infrastructure.Auth;

/// <summary>
/// The session as one value in the secret store (Spec 010 §3.3: SecureStorage, backed by the Android Keystore). One
/// value is written atomically; fields are separated by U+001F. Reading is total: anything unreadable is no session, so
/// the person signs in again instead of Coo crashing at start-up.
/// </summary>
internal sealed class SessionStore(ISecretStore secrets) : ISessionStore
{
    public const string Key = "coo.session";
    private const char Separator = '\u001f';
    private const string Version = "2";
    private const long LatestUnixMs = 253_402_300_799_999;

    public async Task<StoredSession?> GetAsync(CancellationToken cancellationToken)
    {
        var text = await secrets.GetAsync(Key, cancellationToken);
        return text is null ? null : Parse(text);
    }

    public Task SaveAsync(StoredSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return secrets.SetAsync(Key, Format(session), cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken) => secrets.RemoveAsync(Key, cancellationToken);

    internal static string Format(StoredSession session) => string.Join(
        Separator,
        Version,
        session.AccessToken,
        session.AccessTokenExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        session.RefreshToken,
        session.Profile.UserId.Value.ToString("D", CultureInfo.InvariantCulture),
        Clean(session.Profile.Name),
        Clean(session.Profile.Email),
        session.AccountHousehold.Value.ToString("D", CultureInfo.InvariantCulture),
        session.JoinPending ? "1" : "0",
        session.PendingRefreshToken ?? "");

    /// <summary>Version 1 (0.10.0) has nine fields and no pending token; version 2 has ten.</summary>
    internal static StoredSession? Parse(string text)
    {
        var f = text.Split(Separator);
        var known = (f[0] == "1" && f.Length == 9) || (f[0] == "2" && f.Length == 10);
        if (!known || f[1].Length == 0 || f[3].Length == 0 || f[8] is not ("0" or "1")
            || !long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires) || expires is < 0 or > LatestUnixMs
            || !Guid.TryParseExact(f[4], "D", out var user) || user == Guid.Empty
            || !Guid.TryParseExact(f[7], "D", out var household) || household == Guid.Empty)
            return null;
        var pending = f.Length == 10 && f[9].Length > 0 ? f[9] : null;
        return new StoredSession(
            f[1], DateTimeOffset.FromUnixTimeMilliseconds(expires), f[3], new AccountProfile(new UserId(user), f[5], f[6]),
            new HouseholdId(household), f[8] == "1", pending);
    }

    private static string Clean(string value) => value.Replace(Separator, ' ');
}
