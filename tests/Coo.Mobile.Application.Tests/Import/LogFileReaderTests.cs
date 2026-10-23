using System.Globalization;
using Coo.Contracts.AiParsing;
using Coo.Mobile.Application.Import;
using FluentAssertions;
using Xunit;

namespace Coo.Mobile.Application.Tests.Import;

public sealed class LogFileReaderTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    [Fact]
    public void Read_HuckleberryExport_ReadsFeedsBottlesDiapersSleepsAndPumps()
    {
        // Arrange: Huckleberry's columns, as its export writes them
        const string csv = """
            Type,Start,End,Duration,Start Condition,Start Location,End Condition,Notes
            Feed,2026-10-20 06:10,2026-10-20 06:28,00:18,00:10R,Breast,00:08L,
            Feed,2026-10-20 09:00,,,Formula,Bottle,120ml,
            Diaper,2026-10-20 09:15,,,,,Both,
            Diaper,2026-10-20 11:40,,,,,Pee,"was fine, no rash"
            Sleep,2026-10-20 12:00,2026-10-20 13:25,01:25,Upset,Crib,Woke up child,
            Pump,2026-10-20 14:00,2026-10-20 14:20,00:20,,Left,60ml,
            Solids,2026-10-20 17:00,,,,Banana,,
            """;

        // Act
        var preview = LogFileReader.Read(csv, Amsterdam, Dutch);

        // Assert
        preview.Source.Should().Be("Huckleberry");
        (preview.Feeds, preview.Diapers, preview.Sleeps, preview.Pumps, preview.Skipped).Should().Be((2, 2, 1, 1, 1));
        var breast = preview.Entries.Single(e => e.Action == AiActions.BreastLog);
        (breast.LeftMinutes, breast.RightMinutes).Should().Be((8, 10));
        breast.At.Should().Be(new DateTimeOffset(2026, 10, 20, 6, 10, 0, TimeSpan.FromHours(2)));
        preview.Entries.Single(e => e.Action == AiActions.BottleLog).Should().Match<AiProposal>(b => b.Millilitres == 120 && b.Milk == "formula");
        preview.Entries.Where(e => e.Action == AiActions.DiaperLog).Select(e => e.Contents).Should().Equal("mixed", "wet");
        preview.Entries.Single(e => e.Action == AiActions.SleepLog).EndAt.Should().Be(new DateTimeOffset(2026, 10, 20, 13, 25, 0, TimeSpan.FromHours(2)));
        preview.Entries.Single(e => e.Action == AiActions.PumpLog).Millilitres.Should().Be(60);
    }

    [Fact]
    public void Read_DayFirstDatesWithDurationsAndOunces_ResolvesEach()
    {
        // Arrange: another app, semicolons, dd/MM dates (13/10 gives it away), a duration instead of an end
        const string csv = """
            Activity;Date;Time;Duration (min);Amount;Side
            Nap;13/10/2026;13:00;95;;
            Bottle;13/10/2026;15:30;;4 oz;
            Breastfeeding;14/10/2026;07:05;20;;Left
            Tummy time;14/10/2026;08:00;10;;
            """;

        // Act
        var preview = LogFileReader.Read(csv, Amsterdam, CultureInfo.GetCultureInfo("en-US"));

        // Assert
        (preview.Feeds, preview.Sleeps, preview.Skipped).Should().Be((2, 1, 1));
        var nap = preview.Entries.Single(e => e.Action == AiActions.SleepLog);
        nap.At.Should().Be(new DateTimeOffset(2026, 10, 13, 13, 0, 0, TimeSpan.FromHours(2)));
        (nap.EndAt - nap.At).Should().Be(TimeSpan.FromMinutes(95));
        preview.Entries.Single(e => e.Action == AiActions.BottleLog).Millilitres.Should().Be(118);
        preview.Entries.Single(e => e.Action == AiActions.BreastLog).LeftMinutes.Should().Be(20);
        preview.NoSide.Should().Be(0);
    }

    [Fact]
    public void Read_NoStartColumn_IsNothingAndAllSkipped()
    {
        // Act
        var preview = LogFileReader.Read("Name,Weight\nLena,4.2\n", Amsterdam, Dutch);

        // Assert
        preview.Entries.Should().BeEmpty();
        preview.Skipped.Should().Be(1);
    }

    [Fact]
    public void Read_SleepWithoutAnEnd_IsSkippedNotGuessed()
    {
        // Act
        var preview = LogFileReader.Read("Type,Start\nSleep,2026-10-20 20:00\n", Amsterdam, Dutch);

        // Assert
        preview.Sleeps.Should().Be(0);
        preview.Skipped.Should().Be(1);
    }

    [Theory]
    [InlineData("1:25", 85)]
    [InlineData("01:25:00", 85)]
    [InlineData("95", 95)]
    [InlineData("1h 20m", 80)]
    [InlineData("45 min", 45)]
    public void Length_CommonForms_AreMinutes(string text, int minutes)
    {
        // Act
        var length = LogFileReader.Length(text);

        // Assert
        length.Should().Be(TimeSpan.FromMinutes(minutes));
    }
}
