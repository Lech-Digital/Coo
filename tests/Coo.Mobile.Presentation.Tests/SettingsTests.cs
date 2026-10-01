using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Theme;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 009 §3. Ports are substitutes; the use cases and Domain objects are real.</summary>
public sealed class SettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly string[] HiddenAllButPump = ["feeding", "diaper", "sleep"];
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly IDevicePreferences _preferences = Substitute.For<IDevicePreferences>();
    private readonly IAboutInfo _about = Substitute.For<IAboutInfo>();
    private readonly IExternalLauncher _launcher = Substitute.For<IExternalLauncher>();
    private readonly FixedTime _time = new(Now);
    private readonly HouseholdSettings _settings;
    private readonly BabyProfile _baby;
    private long _wall = 10;

    public SettingsTests()
    {
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));
        _settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device),
            HiddenAllButPump.Select(c => EntryKindCode.From(c).Value)).Value;
        _baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        _households.ListHouseholdsAsync(Arg.Any<CancellationToken>()).Returns([_settings]);
        _households.ListBabiesAsync(Household, Arg.Any<CancellationToken>()).Returns([_baby]);
        _preferences.GetAppearanceAsync(Arg.Any<CancellationToken>()).Returns(AppearancePreferences.Default);
        _about.Informational.Returns("0.9.0+e03dbdfb884414aa26d8c9f9e01726c9c7a08c35");
        _about.OsVersion.Returns("16");
    }

    [Theory]
    [InlineData("0.9.0+e03dbdfb884414aa26d8c9f9e01726c9c7a08c35", "0.9.0", "e03dbdf")]
    [InlineData("0.9.0", "0.9.0", "")]
    public void Parse_InformationalVersion_SplitsVersionAndShortCommit(string informational, string version, string commit)
    {
        // Arrange
        var text = informational;

        // Act
        var parsed = AppVersionText.Parse(text);

        // Assert
        parsed.Should().Be((version, commit));
    }

    [Fact]
    public async Task RefreshAsync_OnlyPumpVisible_LocksThePumpSwitch()
    {
        // Arrange
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        var pump = model.Kinds.Single(k => k.Code == "pump");
        pump.IsShown.Should().BeTrue();
        pump.CanChange.Should().BeFalse();
        model.Kinds.Where(k => k.Code != "pump").Should().OnlyContain(k => !k.IsShown && k.CanChange);
    }

    [Fact]
    public async Task SetUnit_Ounces_SavesAndShowsOunces()
    {
        // Arrange
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.SetUnitCommand.ExecuteAsync(VolumeUnit.Ounces);

        // Assert
        model.IsOunces.Should().BeTrue();
        await _households.Received(1).SaveSettingsAsync(_settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_Version_ShowsVersionAndShortCommit()
    {
        // Arrange
        var model = Model();

        // Act
        await model.RefreshAsync();

        // Assert
        model.VersionLine.Should().Contain("0.9.0").And.Contain("e03dbdf");
    }

    [Fact]
    public async Task SendFeedback_NoMailApp_CopiesTheAddressAndSaysSo()
    {
        // Arrange
        _launcher.TryComposeEmailAsync(default!, default!, default!).ReturnsForAnyArgs(false);
        var model = Model();
        await model.RefreshAsync();

        // Act
        await model.SendFeedbackCommand.ExecuteAsync(null);

        // Assert
        await _launcher.Received(1).CopyAsync(SettingsViewModel.FeedbackAddress);
        model.Toast.Should().Contain(SettingsViewModel.FeedbackAddress);
    }

    [Fact]
    public async Task SaveAsync_NameOfSpaces_ShowsTheErrorAndDoesNotRaiseSaved()
    {
        // Arrange
        var model = new BabyProfileViewModel(_households, new UpdateBabyProfile(_households, _clock, _time), _time);
        await model.LoadAsync();
        var saved = false;
        model.Saved += (_, _) => saved = true;
        model.Name = "   ";

        // Act
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().NotBeEmpty();
        saved.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_TwiceInARow_SucceedsBothTimes()
    {
        // Arrange
        var model = new BabyProfileViewModel(_households, new UpdateBabyProfile(_households, _clock, _time), _time);
        await model.LoadAsync();
        model.Nicknames = "Zo, Mimi";

        // Act
        await model.SaveCommand.ExecuteAsync(null);
        await model.SaveCommand.ExecuteAsync(null);

        // Assert
        model.Error.Should().BeEmpty();
        _baby.Nicknames.Should().Equal("Zo", "Mimi");
        await _households.Received(2).SaveBabyAsync(_baby, Arg.Any<CancellationToken>());
    }

    private SettingsViewModel Model() => new(
        _households, new ChangeVolumeUnit(_households, _clock), new SetHiddenKinds(_households, _clock),
        new ThemeController(_preferences, Substitute.For<IOsAppearance>(), _time), new LanguageController(_preferences),
        _about, _launcher, _time);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
