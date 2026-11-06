using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011d §5: a feed in parts says so in its title, in every language; the length is the feeding time.</summary>
[Collection(nameof(StringsState))]
public sealed class FeedPartsTitleTests : IDisposable
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2));
    private static readonly Formats Ml = new(true, VolumeUnit.Millilitres);

    public FeedPartsTitleTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void Title_AFeedInThreeParts_NamesTheSidesTheirTimesAndTheParts()
    {
        // Arrange: left 10 m, a pause, right 8 m, a pause, left 2 m.
        var feed = Feed((BreastSide.Left, 0, 10), (BreastSide.Right, 13, 21), (BreastSide.Left, 25, 27));

        // Act
        var title = Ml.Title(feed, T0.AddHours(1));

        // Assert
        title.Should().Be("Breast · L\u00A012\u00A0m · R\u00A08\u00A0m · in 3 parts", "the length is the feeding time, never the 27 minute span");
    }

    [Fact]
    public void Title_ASideSwitchWithoutAPause_HasNoParts()
    {
        // Arrange
        var feed = Feed((BreastSide.Left, 0, 10), (BreastSide.Right, 10, 15));

        // Act
        var title = Ml.Title(feed, T0.AddHours(1));

        // Assert
        title.Should().Be("Breast · L\u00A010\u00A0m · R\u00A05\u00A0m");
    }

    [Fact]
    public void BreastSummary_AFeedInParts_StaysTheFeedingTimeForTheTile()
    {
        // Arrange: Today's Feed tile reads as before (Spec 011d §5).
        var feed = (BreastFeed)Feed((BreastSide.Left, 0, 10), (BreastSide.Right, 13, 21)).Details;

        // Act
        var summary = Formats.BreastSummary(feed);

        // Assert
        summary.Should().Be("L\u00A010\u00A0m · R\u00A08\u00A0m");
    }

    [Theory]
    [InlineData("en", 2, "in 2 parts")]
    [InlineData("de", 2, "in 2 Teilen")]
    [InlineData("fr", 2, "en 2 fois")]
    [InlineData("es", 3, "en 3 partes")]
    [InlineData("it", 2, "in 2 riprese")]
    [InlineData("nl", 3, "in 3 delen")]
    [InlineData("pl", 2, "w 2 częściach")]
    [InlineData("pl", 5, "w 5 częściach")]
    public void Title_AFeedInPartsInEachLanguage_EndsWithThePluralParts(string language, int parts, string expected)
    {
        // Arrange
        Strings.Use(new LanguagePreference(language), null);
        var feed = Feed([.. Enumerable.Range(0, parts).Select(i => (BreastSide.Left, i * 10, (i * 10) + 5))]);

        // Act
        var title = Ml.Title(feed, T0.AddHours(2));

        // Assert
        title.Should().EndWith(" · " + expected);
    }

    [Fact]
    public void FeedPartsOf_AFeedWithSideSwitchesAndPauses_GroupsSegmentsAsPartsCountsThem()
    {
        // Arrange: left, right back to back; a 61 second pause; left; a 60 second pause (still the same part); right.
        var details = BreastFeed.Create(
        [
            new BreastSegment(BreastSide.Left, T0, T0.AddMinutes(5)),
            new BreastSegment(BreastSide.Right, T0.AddMinutes(5), T0.AddMinutes(8)),
            new BreastSegment(BreastSide.Left, T0.AddMinutes(8).AddSeconds(61), T0.AddMinutes(12)),
            new BreastSegment(BreastSide.Right, T0.AddMinutes(13), T0.AddMinutes(15)),
        ], isCompleted: true).Value;

        // Act
        var parts = FeedParts.Of(details);

        // Assert
        parts.Select(p => p.Count).Should().Equal(2, 2);
        parts.Should().HaveCount(details.Parts);
    }

    private static CareEntry Feed(params (BreastSide Side, int From, int To)[] segments) =>
        CareEntry.Create(new EntryId(Guid.CreateVersion7(T0)), Household, EntrySubject.Baby(Baby),
            BreastFeed.Create(segments.Select(s => new BreastSegment(s.Side, T0.AddMinutes(s.From), T0.AddMinutes(s.To))), isCompleted: true).Value,
            null, PersonRef.ThisDevicesPerson, EntrySource.Timer, new HlcStamp(2, 0, Device), T0).Value;
}
