using System.Globalization;
using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Presentation.Localization;

/// <summary>
/// Loads, stores and applies the app language (Spec 009 §3.5). It raises <see cref="Changed"/> only when the language
/// in effect changes; the host then rebuilds its pages. It knows nothing about MAUI.
/// </summary>
public sealed class LanguageController(IDevicePreferences preferences)
{
    public LanguagePreference Preference { get; private set; } = LanguagePreference.System;

    /// <summary>Android's language, as the host last reported it.</summary>
    public CultureInfo SystemCulture { get; private set; } = CultureInfo.CurrentUICulture;

    public event EventHandler? Changed;

    /// <summary>Call before the first page is built, with the OS culture captured at process start.</summary>
    public async Task StartAsync(CultureInfo system, CancellationToken cancellationToken)
    {
        SystemCulture = system ?? throw new ArgumentNullException(nameof(system));
        Preference = await preferences.GetLanguageAsync(cancellationToken);
        Strings.Use(Preference, SystemCulture);
    }

    public async Task SetAsync(LanguagePreference preference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preference);
        await preferences.SetLanguageAsync(preference, cancellationToken);
        Apply(() => Preference = preference);
    }

    /// <summary>Android's language changed. An explicit choice in Coo wins over it.</summary>
    public void SystemChanged(CultureInfo system)
    {
        ArgumentNullException.ThrowIfNull(system);
        Apply(() => SystemCulture = system);
    }

    private void Apply(Action change)
    {
        var before = Strings.Culture.Name;
        change();
        Strings.Use(Preference, SystemCulture);
        if (Strings.Culture.Name != before)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
