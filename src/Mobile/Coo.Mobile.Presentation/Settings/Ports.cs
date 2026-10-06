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

    /// <summary>Android's share sheet with a text (the invitation, spec §3; unshared entries, §8.2). Closing it sends nothing.</summary>
    Task ShareTextAsync(string text, string title);
}

/// <summary>What the consent sheet came to (Spec 011b §5.1, review ruling: "couldn't ask" is not "said no").</summary>
public enum ConsentAnswer
{
    /// <summary>The sheet could not be shown (Coo in the background, another sheet opening, no window), or the account signed out while it showed.</summary>
    NotAsked,

    /// <summary>Consent is given once the sheet closed.</summary>
    Given,

    /// <summary>The person closed the sheet without agreeing (Not now, Back).</summary>
    Declined,
}

/// <summary>Spec 011 §3's consent sheet, shown where the person is (Spec 011b §3, §5.1).</summary>
public interface IConsentPrompt
{
    Task<ConsentAnswer> AskAsync();
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
