using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Theme;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

public sealed class ThemeControllerTests
{
    private readonly IDevicePreferences _preferences = Substitute.For<IDevicePreferences>();
    private readonly FakeOs _os = new();

    [Fact]
    public async Task StartAsync_AutoAtThreeAm_PicksNight()
    {
        // Arrange
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        using var controller = new ThemeController(_preferences, _os, new FixedTime(new(2026, 10, 1, 3, 12, 0, TimeSpan.FromHours(2))));

        // Act
        await controller.StartAsync(CancellationToken.None);

        // Assert
        controller.Current.Should().Be(PaletteKind.Night);
    }

    [Fact]
    public async Task OsDarkModeChange_DuringTheDay_RaisesPaletteChanged()
    {
        // Arrange
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        using var controller = new ThemeController(_preferences, _os, new FixedTime(new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2))));
        await controller.StartAsync(CancellationToken.None);
        var raised = new List<PaletteKind>();
        controller.PaletteChanged += (_, kind) => raised.Add(kind);

        // Act
        _os.Toggle(dark: true);

        // Assert
        raised.Should().Equal(PaletteKind.Night);
    }

    [Fact]
    public async Task SetAsync_SameResultingPalette_DoesNotRaise()
    {
        // Arrange: daytime, Auto gives Day; switching to Day changes nothing visible.
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        using var controller = new ThemeController(_preferences, _os, new FixedTime(new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2))));
        await controller.StartAsync(CancellationToken.None);
        var raised = 0;
        controller.PaletteChanged += (_, _) => raised++;

        // Act
        await controller.SetAsync(AppearancePreferences.Default with { Mode = AppearanceMode.Day }, CancellationToken.None);

        // Assert
        raised.Should().Be(0);
        await _preferences.Received(1).SetAppearanceAsync(Arg.Any<AppearancePreferences>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAsync_WithACurtain_CoversThenAnnouncesThenReveals()
    {
        // Arrange: daytime; switching to Night changes the palette under the cover.
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        var steps = new List<string>();
        var curtain = new RecordingCurtain(steps);
        using var controller = new ThemeController(_preferences, _os, new FixedTime(new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2))), curtain);
        await controller.StartAsync(CancellationToken.None);
        controller.PaletteChanged += (_, kind) => steps.Add($"changed {kind}");

        // Act
        await controller.SetAsync(AppearancePreferences.Default with { Mode = AppearanceMode.Night }, CancellationToken.None);

        // Assert
        steps.Should().Equal("cover Night", "changed Night", "reveal");
        controller.Current.Should().Be(PaletteKind.Night);
    }

    [Fact]
    public async Task StartAsync_WithACurtain_DoesNotCover()
    {
        // Arrange: nothing is on screen at start-up to hide.
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        var steps = new List<string>();
        using var controller = new ThemeController(_preferences, _os, new FixedTime(new(2026, 10, 1, 3, 0, 0, TimeSpan.FromHours(2))), new RecordingCurtain(steps));

        // Act
        await controller.StartAsync(CancellationToken.None);

        // Assert
        steps.Should().BeEmpty();
        controller.Current.Should().Be(PaletteKind.Night);
    }

    [Fact]
    public async Task SetAsync_SamePaletteWithACurtain_DoesNotCover()
    {
        // Arrange: daytime, Auto gives Day; Day changes nothing visible.
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        var steps = new List<string>();
        using var controller = new ThemeController(_preferences, _os, new FixedTime(new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2))), new RecordingCurtain(steps));
        await controller.StartAsync(CancellationToken.None);

        // Act
        await controller.SetAsync(AppearancePreferences.Default with { Mode = AppearanceMode.Day }, CancellationToken.None);

        // Assert
        steps.Should().BeEmpty();
    }

    private sealed class RecordingCurtain(List<string> steps) : IPaletteCurtain
    {
        public Task CoverAsync(PaletteKind palette)
        {
            steps.Add($"cover {palette}");
            return Task.CompletedTask;
        }

        public Task RevealAsync()
        {
            steps.Add("reveal");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ToggleAsync_AutoAtThreeAm_ShowsDayUntilTheNextBoundaryAndStoresIt()
    {
        // Arrange: 03:00, quiet hours 22:00–07:00, so Auto shows night.
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        using var controller = Controller(at: new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.FromHours(2)));
        await controller.StartAsync(CancellationToken.None);

        // Act
        await controller.ToggleAsync(CancellationToken.None);

        // Assert
        controller.Current.Should().Be(PaletteKind.Day);
        controller.Settings.Mode.Should().Be(AppearanceMode.Auto);
        controller.Settings.Override.Should().Be(new AppearanceOverride(AppearanceMode.Day, new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.FromHours(2))));
        await _preferences.Received(1).SetAppearanceAsync(controller.Settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToggleAsync_FixedNight_StoresDayMode()
    {
        // Arrange
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default with { Mode = AppearanceMode.Night });
        using var controller = Controller(at: new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.FromHours(2)));
        await controller.StartAsync(CancellationToken.None);

        // Act
        await controller.ToggleAsync(CancellationToken.None);

        // Assert
        controller.Settings.Mode.Should().Be(AppearanceMode.Day);
        controller.Settings.Override.Should().BeNull();
        controller.Current.Should().Be(PaletteKind.Day);
    }

    [Fact]
    public async Task ToggleAsync_TwiceInAuto_ClearsTheOverride()
    {
        // Arrange
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        using var controller = Controller(at: new DateTimeOffset(2026, 10, 1, 3, 0, 0, TimeSpan.FromHours(2)));
        await controller.StartAsync(CancellationToken.None);

        // Act
        await controller.ToggleAsync(CancellationToken.None);
        await controller.ToggleAsync(CancellationToken.None);

        // Assert
        controller.Current.Should().Be(PaletteKind.Night);
        controller.Settings.Override.Should().BeNull();
    }

    private ThemeController Controller(DateTimeOffset at) => new(_preferences, _os, new FixedTime(at));

    private sealed class FakeOs : IOsAppearance
    {
        public bool IsDark { get; private set; }

        public event EventHandler? Changed;

        public void Toggle(bool dark)
        {
            IsDark = dark;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
