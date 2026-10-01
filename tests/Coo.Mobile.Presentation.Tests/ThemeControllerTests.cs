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
