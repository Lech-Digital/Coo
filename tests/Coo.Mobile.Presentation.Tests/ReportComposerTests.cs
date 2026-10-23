using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Growth;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Reports;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Reports;
using FluentAssertions;

namespace Coo.Mobile.Presentation.Tests;

[Collection(nameof(StringsState))]
public sealed class ReportComposerTests : IDisposable
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.FromHours(2));
    private long _wall = 1;

    public ReportComposerTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void Compose_DailyVitaminDAndOneFever_GroupsTheDosesAndKeepsTheFever()
    {
        // Arrange: vitamin D every morning for five days, and a fever with a note.
        var health = Enumerable.Range(0, 5)
            .Select(i => Make(MedicineDetails.Create("Vitamin D", "400 IU").Value, Now.AddDays(-5 + i).AddHours(-5)))
            .Append(Make(TemperatureDetails.Create(38.6m).Value, Now.AddHours(-2), "after the jabs"))
            .ToList();

        // Act
        var document = ReportComposer.Compose(Report(health: health), new Formats(true, VolumeUnit.Millilitres));

        // Assert
        var rows = document.Sections.Single(s => s.Heading == "Health").Table!.Rows;
        rows.Should().HaveCount(2);
        rows[0].Should().Equal("18 Oct – 22 Oct (5×)", "Vitamin D · 400 IU", "");
        rows[1][1].Should().Be("Fever · 38.6 °C");
        rows[1][2].Should().Be("after the jabs");
    }

    [Fact]
    public void Compose_GrowthWithAChosenStandard_ShowsPercentilesInTheHouseholdsUnits()
    {
        // Arrange: 5.1 kg at two months, about the WHO median for a girl.
        var growth = Make(GrowthDetails.Create(5_100, null, null).Value, new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(2)));

        // Act
        var document = ReportComposer.Compose(Report(growth: [(growth, (GrowthDetails)growth.Details)], sex: ChartSex.Girl), new Formats(true, VolumeUnit.Millilitres));

        // Assert
        var section = document.Sections.Single(s => s.Heading == "Growth");
        section.Paragraphs.Should().ContainSingle().Which.Should().Contain("girls");
        section.Table!.Rows.Single()[2].Should().Be("5.1 kg (P48)");
        section.Table.Rows.Single()[3].Should().Be("–");
    }

    [Fact]
    public void Compose_NothingLogged_SaysSoInEverySection()
    {
        // Act
        var document = ReportComposer.Compose(Report(), new Formats(true, VolumeUnit.Millilitres));

        // Assert
        document.Title.Should().Be("Care report: Lena");
        document.Sections.Select(s => s.Heading).Should().Equal("Day by day", "Growth", "Health");
        document.Sections.Should().OnlyContain(s => s.Table == null && s.Paragraphs.Count == 1);
    }

    [Fact]
    public void Compose_InTheDoctorsLanguage_IsGermanWhileTheAppStaysEnglish()
    {
        // Arrange
        Strings.Use(new LanguagePreference("en"), null);
        ReportDocument document;

        // Act
        using (Strings.InLanguage("de"))
            document = ReportComposer.Compose(Report(), new Formats(true, VolumeUnit.Millilitres));

        // Assert
        document.Sections.Select(s => s.Heading).Should().Contain("Tag für Tag");
        Strings.Get("Report.Daily").Should().Be("Day by day", "the app's own language is untouched");
    }

    [Fact]
    public void InLanguage_UnsupportedLanguage_ChangesNothing()
    {
        // Arrange
        Strings.Use(new LanguagePreference("en"), null);

        // Act
        using var scope = Strings.InLanguage("tr");

        // Assert
        Strings.Language.Should().Be("en");
    }

    [Theory]
    [InlineData("Lena", "coo-lena-2026-10-23.pdf")]
    [InlineData("Zoë-Łucja", "coo-zoeucja-2026-10-23.pdf")]
    [InlineData("李", "coo-baby-2026-10-23.pdf")]
    public void FileName_AnyName_IsPlainAscii(string name, string expected)
    {
        // Act
        var file = ReportComposer.FileName(Report(name: name));

        // Assert
        file.Should().Be(expected);
    }

    private static CareReport Report(
        IReadOnlyList<CareEntry>? health = null, IReadOnlyList<(CareEntry, GrowthDetails)>? growth = null, ChartSex? sex = null, string name = "Lena") =>
        new(
            BabyProfile.Create(Baby, Household, name, [], new DateOnly(2026, 8, 20), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 23)).Value,
            HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value,
            new DateOnly(2026, 10, 10),
            new DateOnly(2026, 10, 23),
            Now,
            [],
            growth ?? [],
            sex,
            health ?? []);

    private CareEntry Make(EntryDetails details, DateTimeOffset at, string? note = null)
    {
        var entry = CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(Baby), details, EntryTiming.Point(at),
            PersonRef.ThisDevicesPerson, EntrySource.Manual, new HlcStamp(++_wall, 0, Device), Now).Value;
        if (note is not null)
            entry.SetNote(Note.From(note).Value, new HlcStamp(++_wall, 0, Device));
        return entry;
    }
}
