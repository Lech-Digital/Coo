using Coo.Mobile.Presentation.Log;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Twins and siblings in Stats: each baby's average day, lined up by measure.</summary>
public sealed class SideBySideTests
{
    [Fact]
    public void SideBySide_TwinsWithDifferentKinds_LinesUpEachMeasureWithADashWhereOneHasNone()
    {
        // Arrange: Lena is breastfed, Mia has bottles; both sleep and have diapers.
        IReadOnlyList<AverageRow> lena =
        [
            new("feeding", "Feeds", "8", ""),
            new("sleep", "Sleep", "14 h 10 m", ""),
            new("diaper", "Diapers", "7", ""),
        ];
        IReadOnlyList<AverageRow> mia =
        [
            new("feeding", "Milk from bottles", "720 ml", ""),
            new("feeding", "Feeds", "6", ""),
            new("sleep", "Sleep", "13 h 40 m", ""),
        ];

        // Act
        var rows = AveragesViewModel.SideBySide([lena, mia]);

        // Assert
        rows.Select(r => r.Name).Should().Equal("Feeds", "Sleep", "Diapers", "Milk from bottles");
        rows.Single(r => r.Name == "Sleep").Values.Should().Equal("14 h 10 m", "13 h 40 m");
        rows.Single(r => r.Name == "Diapers").Values.Should().Equal("7", "–");
        rows.Single(r => r.Name == "Milk from bottles").Values.Should().Equal("–", "720 ml");
    }

    [Fact]
    public void SideBySide_NothingLogged_IsEmpty()
    {
        // Act
        var rows = AveragesViewModel.SideBySide([[], []]);

        // Assert
        rows.Should().BeEmpty();
    }
}
