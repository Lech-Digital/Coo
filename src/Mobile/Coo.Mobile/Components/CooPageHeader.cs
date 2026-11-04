using Coo.Mobile.Application.Billing;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Components;

/// <summary>
/// A shared top header across the main tabs (Today, Log, Advisor, Settings).
/// Consistent height and margins: left brand mark ("coo" / "coo Premium" in gold),
/// center/right theme switch (day/night toggle), baby chip (initial + name · age), and optional action buttons.
/// </summary>
public sealed class CooPageHeader : Grid
{
    private readonly IServiceProvider _services;
    private readonly ContentView _brandContainer = new();
    private readonly ContentView _themeSwitchContainer = new();
    private readonly ContentView _babyChipContainer = new();
    private readonly HorizontalStackLayout _actionsLayout = new() { Spacing = 8, VerticalOptions = LayoutOptions.Center };

    private IBillingService? _billing;
    private ThemeController? _theme;
    private IHouseholdStore? _households;
    private Coo.Mobile.Application.Households.BabyAvatarSyncService? _avatarSync;
    private BabyFocus? _focus;
    private List<Coo.Domain.Households.BabyProfile> _babies = [];
    private Action? _longPressBrand;
    private int _cachedBabyCount;

    /// <summary>Twins and siblings: another baby was chosen (here or on another tab); the page reloads for her.</summary>
    public event Action? BabyChanged;

    private bool? _cachedIsPremium;
    private PaletteKind? _cachedPalette;
    private string? _cachedBabyName;
    private string? _cachedAgeText;
    private string? _cachedAvatarRef;

    public CooPageHeader(IServiceProvider services, Action? longPressBrand = null, IEnumerable<View>? additionalActions = null)
    {
        _services = services;
        _longPressBrand = longPressBrand;

        // Brand at its size; the right side takes what is left and the baby chip shortens its name in it. With two Auto
        // columns, "coo Premium" and a long name overlapped the moon (owner's phone, 2026-11-03).
        ColumnDefinitions =
        [
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Star),
        ];
        ColumnSpacing = 4;
        Padding = new Thickness(24, 16, 24, 8);

        _brandContainer.VerticalOptions = LayoutOptions.Center;
        _themeSwitchContainer.VerticalOptions = LayoutOptions.Center;
        _babyChipContainer.VerticalOptions = LayoutOptions.Center;

        _babyChipContainer.HorizontalOptions = LayoutOptions.End;
        var rightStack = new Grid
        {
            ColumnSpacing = 4,
            VerticalOptions = LayoutOptions.Center,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
        };
        rightStack.Add(_actionsLayout, 1);
        rightStack.Add(_themeSwitchContainer, 2);
        rightStack.Add(_babyChipContainer, 3);

        Grid.SetColumn((BindableObject)_brandContainer, 0);
        Grid.SetColumn((BindableObject)rightStack, 1);
        Children.Add(_brandContainer);
        Children.Add(rightStack);

        if (additionalActions != null)
        {
            foreach (var action in additionalActions)
                _actionsLayout.Add(action);
        }

        AttachServices();
    }

    public void AddAction(View action) => _actionsLayout.Add(action);

    public void AttachServices()
    {
        _billing = _services.GetService<IBillingService>();
        _theme = _services.GetService<ThemeController>();
        _households = _services.GetService<IHouseholdStore>();
        _avatarSync = _services.GetService<Coo.Mobile.Application.Households.BabyAvatarSyncService>();
        _focus = _services.GetService<BabyFocus>();

        if (_focus != null)
            _focus.Changed += OnFocusChanged;

        if (_billing != null)
            _billing.EntitlementChanged += OnEntitlementChanged;

        if (_theme != null)
            _theme.PaletteChanged += OnPaletteChanged;

        if (_avatarSync != null)
            _avatarSync.AvatarChanged += OnAvatarChanged;

        UpdateBrand();
        UpdateThemeSwitch();
        _ = UpdateBabyChipAsync();
    }

    public void DetachServices()
    {
        if (_focus != null)
            _focus.Changed -= OnFocusChanged;

        if (_billing != null)
            _billing.EntitlementChanged -= OnEntitlementChanged;

        if (_theme != null)
            _theme.PaletteChanged -= OnPaletteChanged;

        if (_avatarSync != null)
            _avatarSync.AvatarChanged -= OnAvatarChanged;
    }

    private void OnFocusChanged() => Dispatcher.Dispatch(() =>
    {
        _ = UpdateBabyChipAsync();
        BabyChanged?.Invoke();
    });

    /// <summary>The chip's picker, with two or more babies: which one this phone shows.</summary>
    private async Task ChooseBabyAsync()
    {
        if (_focus is null || _babies.Count < 2 || FindPage() is not { } page)
            return;
        var others = _babies.Skip(1).ToList();
        var choice = await CooDialog.ChooseAsync(page, Strings.Get("Sibling.Choose"), [.. others.Select(b => b.Name)]);
        if (choice >= 0 && choice < others.Count)
            await _focus.FocusAsync(others[choice].Id, CancellationToken.None);
    }

    private Page? FindPage()
    {
        Element? element = this;
        while (element is not null and not Page)
            element = element.Parent;
        return element as Page;
    }

    private void OnAvatarChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(() =>
    {
        _ = UpdateBabyChipAsync();
    });

    private void OnEntitlementChanged() => Dispatcher.Dispatch(() =>
    {
        UpdateBrand();
    });

    private void OnPaletteChanged(object? sender, PaletteKind e) => Dispatcher.Dispatch(() =>
    {
        UpdateBrand();
        UpdateThemeSwitch();
    });

    public void Refresh()
    {
        UpdateBrand();
        UpdateThemeSwitch();
        _ = UpdateBabyChipAsync();
    }

    private void UpdateBrand()
    {
        var isPremium = _billing?.IsPremiumCached ?? false;
        var palette = _theme?.Current ?? PaletteKind.Day;
        if (_cachedIsPremium == isPremium && _cachedPalette == palette && _brandContainer.Content != null)
            return;
        _cachedIsPremium = isPremium;
        _cachedPalette = palette;
        _brandContainer.Content = CooBrand.CreateMark(isPremium, palette == PaletteKind.Night, _longPressBrand, fontSize: 24);
    }

    private void UpdateThemeSwitch()
    {
        if (_theme == null)
            return;

        var isNight = _theme.Current == PaletteKind.Night;
        var newIcon = new Icon(isNight ? IconGeometry.Sun : IconGeometry.Sleep, "Ink2")
            .Tap(i => { i.HorizontalOptions = LayoutOptions.Center; i.VerticalOptions = LayoutOptions.Center; });

        if (_themeSwitchContainer.Content is ContentView btn)
        {
            btn.Content = newIcon;
            SemanticProperties.SetDescription(btn, Strings.Get(isNight ? "Theme.ToDay" : "Theme.ToNight"));
            return;
        }

        var button = new ContentView
        {
            Content = newIcon,
            WidthRequest = 44,
            HeightRequest = 44,
            VerticalOptions = LayoutOptions.Center,
        };
        SemanticProperties.SetDescription(button, Strings.Get(isNight ? "Theme.ToDay" : "Theme.ToNight"));
        button.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await _theme.ToggleAsync(CancellationToken.None))
        });

        _themeSwitchContainer.Content = button;
    }

    private async Task UpdateBabyChipAsync()
    {
        if (_households == null)
            return;

        try
        {
            var household = (await _households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
            if (household == null)
            {
                _cachedBabyName = null;
                _cachedAgeText = null;
                _cachedAvatarRef = null;
                Dispatcher.Dispatch(() => _babyChipContainer.Content = null);
                return;
            }

            var babies = (await _households.ListBabiesAsync(household.Id, CancellationToken.None)).Where(b => !b.IsDeleted).ToList();
            if (babies.Count == 0)
            {
                _cachedBabyName = null;
                _cachedAgeText = null;
                _cachedAvatarRef = null;
                Dispatcher.Dispatch(() => _babyChipContainer.Content = null);
                return;
            }

            _babies = babies;
            var baby = babies[0];
            var babyName = baby.Name;
            var initialText = babyName.Length > 0 ? babyName[..1].ToUpper(Strings.Culture) : "";
            var time = _services.GetService<TimeProvider>() ?? TimeProvider.System;
            var ageText = baby.IsBorn
                ? Formats.Age(baby, DateOnly.FromDateTime(time.GetLocalNow().DateTime))
                : Strings.Get("Expecting.Eyebrow");

            var preferences = _services.GetService<Coo.Mobile.Application.Preferences.IDevicePreferences>();
            var avatarRef = preferences != null
                ? await preferences.GetBabyAvatarAsync(baby.Id.Value, CancellationToken.None)
                : null;

            if (babyName == _cachedBabyName && ageText == _cachedAgeText && avatarRef == _cachedAvatarRef && babies.Count == _cachedBabyCount
                && _babyChipContainer.Content != null)
                return;

            _cachedBabyCount = babies.Count;
            var hasSiblings = babies.Count > 1;

            _cachedBabyName = babyName;
            _cachedAgeText = ageText;
            _cachedAvatarRef = avatarRef;

            Dispatcher.Dispatch(() =>
            {
                var avatar = CooAvatar.Create(avatarRef, initialText, 38);

                var chipContent = new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        avatar,
                        Ui.Text(hasSiblings ? $"{babyName} · {ageText} ▾" : $"{babyName} · {ageText}", "BodyStrong").Tap(l =>
                        {
                            l.VerticalOptions = LayoutOptions.Center;
                            // One line, cut with "…": a long name pushed into the brand and the moon.
                            l.MaxLines = 1;
                            l.LineBreakMode = LineBreakMode.TailTruncation;
                            l.MaximumWidthRequest = 140;
                        })
                    }
                };

                var chip = Ui.Rounded(chipContent, "Surface", 999, new Thickness(5, 5, 14, 5), "Line");
                if (hasSiblings)
                    chip.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await ChooseBabyAsync()) });
                _babyChipContainer.Content = chip;
            });
        }
        catch (Exception)
        {
            // Keep existing chip on any store reading error
        }
    }
}
