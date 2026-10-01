namespace Coo.Mobile.Presentation.Settings;

/// <summary>What About shows about the build and the phone (Spec 009 §3.6).</summary>
public interface IAboutInfo
{
    /// <summary>The assembly's informational version, for example <c>0.9.0+e03dbdf…</c>.</summary>
    string Informational { get; }

    string OsVersion { get; }
}

/// <summary>Leaving the app: mail, browser, clipboard (Spec 009 §3.6).</summary>
public interface IExternalLauncher
{
    /// <summary>False when the phone has no mail app.</summary>
    Task<bool> TryComposeEmailAsync(string address, string subject, string body);

    Task OpenBrowserAsync(Uri uri);

    Task CopyAsync(string text);
}

public static class AppVersionText
{
    /// <summary>"0.9.0+e03dbdfb…" → ("0.9.0", "e03dbdf"), the way the API's /health reports it.</summary>
    public static (string Version, string Commit) Parse(string informational)
    {
        ArgumentNullException.ThrowIfNull(informational);
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0
            ? (informational, "")
            : (informational[..plus], informational[(plus + 1)..][..Math.Min(7, informational.Length - plus - 1)]);
    }
}
