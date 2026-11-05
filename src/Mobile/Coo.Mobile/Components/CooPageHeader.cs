using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Components;

/// <summary>
/// The top bar of the main tabs (Log, Stats, Advisor, Settings): the "mochi" wordmark on the left ("mochi Premium" in
/// gold), the page's own actions, and the day/night switch at the far right. No baby chip: it took the corner and did
/// nothing useful there; the baby is in Settings → Baby, and twins switch on Today (owner, 2026-11-05).
/// </summary>
public sealed class CooPageHeader : Grid
{
    private readonly IServiceProvider _services;
    private readonly ContentView _brandContainer = new() { VerticalOptions = LayoutOptions.Center };
    private readonly ContentView _themeSwitchContainer = new() { VerticalOptions = LayoutOptions.Center };
    private readonly HorizontalStackLayout _actionsLayout = new() { Spacing = 8, VerticalOptions = LayoutOptions.Center };
    private readonly Action? _longPressBrand;

    private IBillingService? _billing;
    private ThemeController? _theme;
    private BabyFocus? _focus;
    private bool? _cachedIsPremium;
    private PaletteKind? _cachedPalette;

    public CooPageHeader(IServiceProvider services, Action? longPressBrand = null, IEnumerable<View>? additionalActions = null)
    {
        _services = services;
        _longPressBrand = longPressBrand;

        ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)];
        ColumnSpacing = 4;
        Padding = PageFrame.HeaderPadding;
        MinimumHeightRequest = PageFrame.HeaderHeight;

        this.Add(_brandContainer, 0);
        this.Add(_actionsLayout, 2);
        this.Add(_themeSwitchContainer, 3);

        if (additionalActions != null)
        {
            foreach (var action in additionalActions)
                _actionsLayout.Add(action);
        }

        AttachServices();
    }

    /// <summary>Twins and siblings: another baby was chosen on Today; the page reloads for her.</summary>
    public event Action? BabyChanged;

    public void AddAction(View action) => _actionsLayout.Add(action);

    public void AttachServices()
    {
        _billing = _services.GetService<IBillingService>();
        _theme = _services.GetService<ThemeController>();
        _focus = _services.GetService<BabyFocus>();

        if (_focus != null)
            _focus.Changed += OnFocusChanged;
        if (_billing != null)
            _billing.EntitlementChanged += OnEntitlementChanged;
        if (_theme != null)
            _theme.PaletteChanged += OnPaletteChanged;

        UpdateBrand();
        UpdateThemeSwitch();
    }

    public void DetachServices()
    {
        if (_focus != null)
            _focus.Changed -= OnFocusChanged;
        if (_billing != null)
            _billing.EntitlementChanged -= OnEntitlementChanged;
        if (_theme != null)
            _theme.PaletteChanged -= OnPaletteChanged;
    }

    public void Refresh()
    {
        UpdateBrand();
        UpdateThemeSwitch();
    }

    private void OnFocusChanged() => Dispatcher.Dispatch(() => BabyChanged?.Invoke());

    private void OnEntitlementChanged() => Dispatcher.Dispatch(UpdateBrand);

    private void OnPaletteChanged(object? sender, PaletteKind e) => Dispatcher.Dispatch(() =>
    {
        UpdateBrand();
        UpdateThemeSwitch();
    });

    private void UpdateBrand()
    {
        var isPremium = _billing?.IsPremiumCached ?? false;
        var palette = _theme?.Current ?? PaletteKind.Day;
        if (_cachedIsPremium == isPremium && _cachedPalette == palette && _brandContainer.Content != null)
            return;
        _cachedIsPremium = isPremium;
        _cachedPalette = palette;
        _brandContainer.Content = CooBrand.CreateMark(isPremium, palette == PaletteKind.Night, _longPressBrand, fontSize: PageFrame.WordmarkSize);
    }

    private void UpdateThemeSwitch()
    {
        if (_theme == null)
            return;
        _themeSwitchContainer.Content = PageFrame.ThemeSwitch(_theme);
    }
}
