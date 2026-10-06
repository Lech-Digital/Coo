using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Settings;

/// <summary>Mail, browser and clipboard through MAUI Essentials (Spec 009 §3.6).</summary>
internal sealed class MauiExternalLauncher : IExternalLauncher
{
    public async Task<bool> TryComposeEmailAsync(string address, string subject, string body)
    {
        if (!Email.Default.IsComposeSupported)
            return false;
        try
        {
            await Email.Default.ComposeAsync(new EmailMessage(subject, body, address));
            return true;
        }
        catch (FeatureNotSupportedException)
        {
            return false;
        }
    }

    public Task OpenBrowserAsync(Uri uri) => Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);

    public Task CopyAsync(string text) => Clipboard.Default.SetTextAsync(text);

    public Task ShareTextAsync(string text, string title) => Share.Default.RequestAsync(new ShareTextRequest { Text = text, Title = title });
}
