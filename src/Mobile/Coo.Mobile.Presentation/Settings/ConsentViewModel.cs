using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>
/// The consent sheet (Spec 011 §3, wording <c>care-data-v1</c>). Agree records it on the server with the app version and
/// language, then a cycle starts; Not now (or Back, which the page maps here) sends nothing.
/// </summary>
public sealed partial class ConsentViewModel(RecordConsent record, DeclineConsent decline, IExternalLauncher launcher, IAboutInfo about) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public event EventHandler? Closed;

    public static string Title => Strings.Get("Consent.Title");

    public static string Body => Strings.Get("Consent.Body");

    public static string Body2 => Strings.Get("Consent.Body2");

    public static string PrivacyText => Strings.Get("Consent.Privacy");

    public static string AgreeText => Strings.Get("Consent.Agree");

    public static string NotNowText => Strings.Get("Consent.NotNow");

    [RelayCommand]
    private async Task AgreeAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var version = AppVersionText.Parse(about.Informational).Version;
            if (await record.ExecuteAsync(version, Strings.Language, CancellationToken.None))
                Closed?.Invoke(this, EventArgs.Empty);
            else
                Toast = Strings.Get("Consent.Failed");
        }
#pragma warning disable CA1031 // A failed local write must say so, never crash the sheet.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Consent.Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NotNowAsync()
    {
        try
        {
            await decline.ExecuteAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Not now only remembers a choice; if that fails nothing was sent, so the sheet still closes.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private Task OpenPrivacyAsync() => launcher.OpenBrowserAsync(SettingsViewModel.PrivacyUrl);
}
