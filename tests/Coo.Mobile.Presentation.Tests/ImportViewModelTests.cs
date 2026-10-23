using Coo.Contracts.AiParsing;
using Coo.Mobile.Application.Import;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using FluentAssertions;
using Xunit;

namespace Coo.Mobile.Presentation.Tests;

public sealed class ImportViewModelTests : IDisposable
{
    public ImportViewModelTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    [Fact]
    public void Describe_HuckleberryFile_SaysWhatWasFoundAndWhatWasNot()
    {
        // Arrange
        var at = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(2));
        var entry = new AiProposal(AiActions.DiaperLog, at, null, null, null, null, null, null, "wet", "", []);
        var preview = new ImportPreview([entry, entry with { At = at.AddDays(72) }], 0, 2, 0, 0, 3, 1, at, at.AddDays(72), "Huckleberry");

        // Act
        var text = ImportViewModel.Describe(preview);

        // Assert
        text.Should().Contain("Found 2 entries from Huckleberry, 12 Aug 2026 – 23 Oct 2026.");
        text.Should().Contain("3 rows could not be used");
        text.Should().Contain("1 breastfeed has no side");
    }

    [Fact]
    public void Describe_NothingFound_SaysSo()
    {
        // Act
        var text = ImportViewModel.Describe(ImportPreview.Empty);

        // Assert
        text.Should().StartWith("Coo found nothing");
    }

    [Fact]
    public void Describe_Result_CountsAddedAndAlreadyThere()
    {
        // Act
        var text = ImportViewModel.Describe(new ImportResult(800, 12, 0));

        // Assert
        text.Should().Be("Added 800 entries. 12 were already in Coo.");
    }
}
