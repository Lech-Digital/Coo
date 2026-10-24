using Coo.Mobile.Application.Notifications;
using Coo.Mobile.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

namespace Coo.Mobile.Infrastructure.Tests;

public sealed class ReminderSettingsStoreTests
{
    [Fact]
    public void WriteMedicines_ThenRead_SameReminders()
    {
        // Arrange
        MedicineReminder[] medicines =
        [
            new("Vitamin D", null, [new TimeOnly(9, 0)]),
            new("Amoxicillin", "5 ml", [new TimeOnly(8, 0), new TimeOnly(20, 0)], new DateOnly(2026, 10, 30)),
        ];

        // Act
        var read = ReminderSettingsStore.ReadMedicines(ReminderSettingsStore.WriteMedicines(medicines));

        // Assert
        read.Should().BeEquivalentTo(medicines, o => o.WithStrictOrdering());
    }

    [Fact]
    public void WriteMedicines_TabsAndNewlinesInTheName_StayOneReminder()
    {
        // Arrange
        MedicineReminder[] medicines = [new("Iron\tdrops\n", "1\tml", [new TimeOnly(12, 0)])];

        // Act
        var read = ReminderSettingsStore.ReadMedicines(ReminderSettingsStore.WriteMedicines(medicines));

        // Assert
        read.Should().ContainSingle().Which.Should().Match<MedicineReminder>(m => m.Name == "Iron drops" && m.Dose == "1 ml");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no tabs here")]
    [InlineData("Vitamin D\t\tnot-a-time\t")]
    public void ReadMedicines_Unreadable_Nothing(string? text)
    {
        // Act
        var read = ReminderSettingsStore.ReadMedicines(text);

        // Assert
        read.Should().BeEmpty();
    }
}
