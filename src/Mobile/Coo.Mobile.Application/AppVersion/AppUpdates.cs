namespace Coo.Mobile.Application.AppVersion;

/// <summary>The server's word on versions (<c>/api/app-version</c>): below <see cref="Minimum"/> the app must update.</summary>
public sealed record AppVersionPolicy(int Latest, int Minimum);

public interface IAppVersionApi
{
    /// <summary>Null when the server cannot be asked.</summary>
    Task<AppVersionPolicy?> GetAsync(CancellationToken cancellationToken);
}

public enum UpdateAction
{
    None,

    /// <summary>Downloads in the background while the app is used; a restart installs it.</summary>
    Flexible,

    /// <summary>Play's full-screen update, which the app cannot be used past.</summary>
    Immediate,

    /// <summary>Play has no in-app update to offer (not installed from Play, or not rolled out yet) but the server
    /// requires one: the app sends the user to the store.</summary>
    OpenStore,
}

/// <summary>When to update, from what Play offers, its priority (0–5, set per release) and the server's minimum.</summary>
public static class UpdatePolicy
{
    /// <summary>Play's update priority from which an update is immediate.</summary>
    public const int ImmediatePriority = 4;

    public static UpdateAction Decide(
        int versionCode, AppVersionPolicy? server, bool playOffersUpdate, int playPriority, bool immediateAllowed, bool flexibleAllowed, bool flexibleOfferedThisSession)
    {
        var required = server is { } s && versionCode < s.Minimum;
        if (playOffersUpdate)
        {
            if ((required || playPriority >= ImmediatePriority) && immediateAllowed)
                return UpdateAction.Immediate;
            if (flexibleAllowed && !flexibleOfferedThisSession)
                return UpdateAction.Flexible;
            return required && !immediateAllowed && !flexibleAllowed ? UpdateAction.OpenStore : UpdateAction.None;
        }

        return required ? UpdateAction.OpenStore : UpdateAction.None;
    }
}

/// <summary>
/// When to ask for a Play review: never in the first week, never at night, only after a log was saved (a moment that
/// went right, not one of worry), and rarely. Play also limits how often its dialog really shows.
/// </summary>
public static class ReviewPolicy
{
    public static readonly TimeSpan FirstWeek = TimeSpan.FromDays(7);
    public static readonly TimeSpan BetweenAsks = TimeSpan.FromDays(90);
    public const int MinimumEntries = 40;
    public const int MostAsks = 3;

    public static bool ShouldAsk(DateTimeOffset firstUse, DateTimeOffset now, int entries, DateTimeOffset? lastAsked, int asked, bool isQuietHours) =>
        !isQuietHours
        && now - firstUse >= FirstWeek
        && entries >= MinimumEntries
        && asked < MostAsks
        && (lastAsked is not { } last || now - last >= BetweenAsks);
}
