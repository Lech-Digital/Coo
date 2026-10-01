using Coo.Mobile.Application.Preferences;

namespace Coo.Mobile.Presentation.Theme;

/// <summary>Whether Android is in dark mode, as the host reports it.</summary>
public interface IOsAppearance
{
    bool IsDark { get; }

    event EventHandler? Changed;
}

/// <summary>
/// Decides the palette and announces changes (Spec 005 §6.2). It re-evaluates on start, on resume, when Android's
/// dark mode or the setting changes, and at the next quiet-hours boundary, with one timer rather than polling.
/// The host applies the palette; this class knows nothing about MAUI.
/// </summary>
public sealed class ThemeController(IDevicePreferences preferences, IOsAppearance os, TimeProvider time) : IDisposable
{
    private ITimer? _timer;
    private AppearancePreferences _settings = AppearancePreferences.Default;
    private bool _started;

    public PaletteKind Current { get; private set; } = PaletteKind.Day;

    public AppearancePreferences Settings => _settings;

    /// <summary>Raised with the new palette when it changes.</summary>
    public event EventHandler<PaletteKind>? PaletteChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _settings = await preferences.GetAppearanceAsync(cancellationToken);
        if (!_started)
        {
            os.Changed += OnOsChanged;
            _started = true;
        }

        Evaluate(force: true);
    }

    /// <summary>Call on app resume: the clock may have passed a boundary while the app slept.</summary>
    public void Refresh() => Evaluate(force: false);

    public async Task SetAsync(AppearancePreferences settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await preferences.SetAppearanceAsync(settings, cancellationToken);
        _settings = settings;
        Evaluate(force: false);
    }

    public void Dispose()
    {
        os.Changed -= OnOsChanged;
        _timer?.Dispose();
    }

    private void OnOsChanged(object? sender, EventArgs e) => Evaluate(force: false);

    private void Evaluate(bool force)
    {
        var now = time.GetLocalNow();
        var next = AppearanceRule.Resolve(_settings.Mode, TimeOnly.FromDateTime(now.DateTime), _settings.QuietHours, os.IsDark);
        Schedule(now);

        if (next == Current && !force)
            return;

        Current = next;
        PaletteChanged?.Invoke(this, next);
    }

    private void Schedule(DateTimeOffset now)
    {
        _timer?.Dispose();
        _timer = null;
        if (AppearanceRule.NextChange(_settings.Mode, now, _settings.QuietHours, time.LocalTimeZone) is { } at)
        {
            // A second past the boundary, so the rule sees the new side of it.
            var due = at - now + TimeSpan.FromSeconds(1);
            _timer = time.CreateTimer(_ => Evaluate(force: false), null, due, Timeout.InfiniteTimeSpan);
        }
    }
}
