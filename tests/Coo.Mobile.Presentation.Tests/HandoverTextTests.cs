using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The nanny's handover in words.</summary>
[Collection(nameof(StringsState))]
public sealed class HandoverTextTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 17, 0, 0, TimeSpan.Zero);
    private static readonly Formats Ml = new(true, VolumeUnit.Millilitres);

    public HandoverTextTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private static HandoverDay Day(int feeds = 3, int bottles = 2, int dirty = 1, int medicines = 0) => new(
        new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2")), "Anna", feeds, bottles, 300m, 2, TimeSpan.FromMinutes(130), 4, dirty, medicines,
        ["ate well at lunch"], Now.AddHours(-9), Now.AddHours(-1));

    [Fact]
    public void Lines_ANannysDay_ReadsAsAParentHearsItAtPickUp()
    {
        // Act
        var lines = HandoverSection.Lines(Day(), Ml);
        var plain = HandoverSection.Lines(Day(feeds: 1, bottles: 0, dirty: 0, medicines: 1), Ml);

        // Assert
        lines.Should().Equal("3 feeds · 300 ml in bottles", "2 naps · 2 h 10 m asleep", "4 diapers · 1 dirty", "“ate well at lunch”");
        plain.Should().Equal("1 feed", "2 naps · 2 h 10 m asleep", "4 diapers", "1 medicine", "“ate well at lunch”");
    }

    [Fact]
    public void Update_AParentWithTheNannysDay_ShowsACardToMarkSeen()
    {
        // Arrange
        var section = new HandoverSection();
        var household = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
        var stamp = new Coo.Domain.Common.Versioning.HlcStamp(1, 0, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1")));
        var baby = BabyProfile.Create(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), household, "Laura", [], new DateOnly(2026, 5, 1), null,
            stamp, new DateOnly(2026, 11, 4)).Value;
        var settings = HouseholdSettings.Create(household, VolumeUnit.Millilitres, "Europe/Amsterdam", stamp).Value;
        var snapshot = new HomeSnapshot(settings, baby, null, Now, Theme.PaletteKind.Day, Ml, Handover: new HandoverView([Day()], null));

        // Act
        section.Update(snapshot);

        // Assert
        var card = section.Cards.Should().ContainSingle().Subject;
        card.Title.Should().Be("Today with Anna");
        card.IsMine.Should().BeFalse();
        card.ShareText.Should().StartWith("Today with Anna (");
    }

    [Fact]
    public void Lines_Polish_UsesItsPluralForms()
    {
        // Arrange
        Strings.Use(new LanguagePreference("pl"), null);

        // Act
        var lines = HandoverSection.Lines(Day(feeds: 5), Ml);

        // Assert
        lines[0].Should().StartWith("5 karmień");
        lines[1].Should().StartWith("2 drzemki");
        lines[2].Should().StartWith("4 pieluchy");
    }

    [Fact]
    public void NightLines_WithAndWithoutAPlan_ReadAsTheMorningNote()
    {
        // Arrange
        var night = new Coo.Domain.Insights.SleepMeasures(1, 2, TimeSpan.FromMinutes(250), TimeSpan.FromMinutes(620), null, null, null, new TimeOnly(19, 40), new TimeOnly(6, 30));
        var before = night with { NightWakings = 3.4, Bedtime = new TimeOnly(20, 15) };
        var plain = new Coo.Mobile.Application.Insights.LastNight(night, 2, null, []);
        var planned = new Coo.Mobile.Application.Insights.LastNight(night, 2, before, [Coo.Domain.Insights.SleepPlanGoal.FewerNightWakings]);
        var bedtime = new Coo.Mobile.Application.Insights.LastNight(night, 0, before, [Coo.Domain.Insights.SleepPlanGoal.EarlierBedtime]);

        // Act
        var a = LastNightSection.Lines(plain, Ml);
        var b = LastNightSection.Lines(planned, Ml);
        var c = LastNightSection.Lines(bedtime, Ml);

        // Assert
        a.Should().Be(("10 h 20 m asleep · woke 2 times · longest 4 h 10 m", "2 feeds in the night"));
        b.Second.Should().Be("Before the plan: 3.4 wakings a night.");
        c.Second.Should().Be("Bedtime 19:40; before the plan 20:15.");
    }

    [Fact]
    public void Update_ThePartnerDidTheNight_ShowsAWhileYouWereAwayCard()
    {
        // Arrange
        var section = new HandoverSection();
        var household = new HouseholdId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
        var stamp = new Coo.Domain.Common.Versioning.HlcStamp(1, 0, new DeviceId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1")));
        var baby = BabyProfile.Create(new BabyId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1")), household, "Laura", [], new DateOnly(2026, 5, 1), null,
            stamp, new DateOnly(2026, 11, 4)).Value;
        var settings = HouseholdSettings.Create(household, VolumeUnit.Millilitres, "Europe/Amsterdam", stamp).Value;
        var kasia = Day() with { Name = "Kasia" };

        // Act
        section.Update(new HomeSnapshot(settings, baby, null, Now, Theme.PaletteKind.Day, Ml, Away: [kasia, Day() with { Name = "" }]));

        // Assert
        section.Cards.Select(c => c.Title).Should().Equal("While you were away · Kasia", "While you were away · Another parent");
        section.Cards.Should().OnlyContain(c => c.IsAway && !c.IsMine);
    }
}
