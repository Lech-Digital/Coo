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

    /// <summary>
    /// The sun or moon on Today (Spec 009 §3.7). In Auto, the other palette until the next quiet-hours boundary (12 h
    /// without quiet hours); with Day or Night fixed, the other mode. Toggling back to what Auto would show anyway
    /// clears the override.
    /// </summary>
    public Task ToggleAsync(CancellationToken cancellationToken)
    {
        var target = Current == PaletteKind.Night ? AppearanceMode.Day : AppearanceMode.Night;
        if (_settings.Mode != AppearanceMode.Auto)
            return SetAsync(_settings with { Mode = target, Override = null }, cancellationToken);

        var now = time.GetLocalNow();
        var auto = AppearanceRule.Resolve(AppearanceMode.Auto, TimeOnly.FromDateTime(now.DateTime), _settings.QuietHours, os.IsDark);
        if ((auto == PaletteKind.Night) == (target == AppearanceMode.Night))
            return SetAsync(_settings with { Override = null }, cancellationToken);

        var until = AppearanceRule.NextChange(AppearanceMode.Auto, now, _settings.QuietHours, time.LocalTimeZone) ?? now.AddHours(12);
        return SetAsync(_settings with { Override = new AppearanceOverride(target, until) }, cancellationToken);
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
        var next = AppearanceRule.Resolve(_settings.Mode, now, _settings.QuietHours, os.IsDark, _settings.Override);
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
        // The earlier of the next quiet-hours boundary and the end of a Today switch.
        var boundary = AppearanceRule.NextChange(_settings.Mode, now, _settings.QuietHours, time.LocalTimeZone);
        DateTimeOffset? until = _settings.Override is { } o && o.Until > now ? o.Until : null;
        if ((boundary, until) switch { ({ } b, { } u) => b < u ? b : u, ({ } b, null) => b, (null, { } u) => u, _ => (DateTimeOffset?)null } is { } at)
        {
            // A second past the boundary, so the rule sees the new side of it.
            var due = at - now + TimeSpan.FromSeconds(1);
            _timer = time.CreateTimer(_ => Evaluate(force: false), null, due, Timeout.InfiniteTimeSpan);
        }
    }
}
