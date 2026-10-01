using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using NSubstitute;

namespace Coo.Mobile.Application.Tests;

/// <summary>Spec 009 §3.1–§3.3. Only the outbound ports are mocked.</summary>
public sealed class SettingsUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));

    private readonly IHouseholdStore _households = Substitute.For<IHouseholdStore>();
    private readonly IVersionClock _clock = Substitute.For<IVersionClock>();
    private readonly FixedTime _time = new(Now);
    private long _wall = 1_790_000_000_000;

    public SettingsUseCaseTests() =>
        _clock.NextAsync(Arg.Any<CancellationToken>()).Returns(_ => new HlcStamp(++_wall, 0, Device));

    [Fact]
    public async Task ExecuteAsync_ValidBabyChange_SavesOnceWithANewVersion()
    {
        // Arrange
        var baby = Born();
        var before = baby.Version;
        var useCase = new UpdateBabyProfile(_households, _clock, _time);

        // Act
        var result = await useCase.ExecuteAsync(baby, new BabyProfileChange("Zofia", ["Zo"], Today.AddMonths(-2), null), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        baby.Version.Should().BeGreaterThan(before);
        await _households.Received(1).SaveBabyAsync(baby, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DueDateOutOfRange_ReturnsErrorAndSavesNothing()
    {
        // Arrange
        var useCase = new UpdateBabyProfile(_households, _clock, _time);

        // Act
        var result = await useCase.ExecuteAsync(Born(), new BabyProfileChange("Zosia", [], Today.AddMonths(-2), Today.AddYears(1)), CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(BabyErrors.DueDateOutOfRange);
        await _households.DidNotReceiveWithAnyArgs().SaveBabyAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_NewUnit_SavesTheSettings()
    {
        // Arrange
        var settings = Settings();
        var useCase = new ChangeVolumeUnit(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(settings, VolumeUnit.Ounces, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        settings.VolumeUnit.Should().Be(VolumeUnit.Ounces);
        await _households.Received(1).SaveSettingsAsync(settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SameUnit_WritesNothing()
    {
        // Arrange
        var useCase = new ChangeVolumeUnit(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(Settings(), VolumeUnit.Millilitres, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_HidePump_SavesItAmongTheHiddenKinds()
    {
        // Arrange
        var settings = Settings();
        var useCase = new SetHiddenKinds(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(settings, Code("pump"), hidden: true, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        settings.HiddenKinds.Should().Equal(Code("pump"));
        await _households.Received(1).SaveSettingsAsync(settings, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HideTheLastVisibleKind_FailsWithAllKindsHidden()
    {
        // Arrange
        var settings = Settings(hidden: ["feeding", "diaper", "sleep"]);
        var useCase = new SetHiddenKinds(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(settings, Code("pump"), hidden: true, CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be(SettingsErrors.AllKindsHidden);
        await _households.DidNotReceiveWithAnyArgs().SaveSettingsAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownCodeFromANewerClient_IsKept()
    {
        // Arrange
        var settings = Settings(hidden: ["medication"]);
        var useCase = new SetHiddenKinds(_households, _clock);

        // Act
        await useCase.ExecuteAsync(settings, Code("pump"), hidden: true, CancellationToken.None);

        // Assert
        settings.HiddenKinds.Should().BeEquivalentTo([Code("medication"), Code("pump")]);
    }

    [Fact]
    public async Task ExecuteAsync_ShowKindWhenAllHidden_Succeeds()
    {
        // Arrange: a newer client hid every kind this build knows.
        var settings = Settings(hidden: ["feeding", "diaper", "sleep", "pump"]);
        var useCase = new SetHiddenKinds(_households, _clock);

        // Act
        var result = await useCase.ExecuteAsync(settings, Code("sleep"), hidden: false, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        settings.HiddenKinds.Should().NotContain(Code("sleep"));
    }

    private static EntryKindCode Code(string value) => EntryKindCode.From(value).Value;

    private static BabyProfile Born() =>
        BabyProfile.Create(Baby, Household, "Zosia", [], Today.AddMonths(-2), null, new HlcStamp(1, 0, Device), Today).Value;

    private static HouseholdSettings Settings(string[]? hidden = null) =>
        HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device),
            (hidden ?? []).Select(Code)).Value;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("plus2", now.Offset, "plus2", "plus2");
    }
}
