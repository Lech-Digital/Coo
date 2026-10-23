using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Spec 011b §7.3's labels and §2's member rows.</summary>
[Collection(nameof(StringsState))]
public sealed class FamilyLabelTests : IDisposable
{
    private static readonly HouseholdId Id = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public FamilyLabelTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private static FamilyOnPhone Family(string[] babies, FamilyMembership? membership) => new(Id, babies, membership, true, false, false, true);

    [Theory]
    [InlineData(new[] { "Lena" }, "Lena")]
    [InlineData(new[] { "Lena", "Leo" }, "Lena, Leo")]
    [InlineData(new string[0], "No baby yet")]
    public void Title_Babies_NamesThemOrSaysNoBabyYet(string[] babies, string expected)
    {
        // Act
        var title = FamilyLabels.Title(Family(babies, null));

        // Assert
        title.Should().Be(expected);
    }

    [Fact]
    public void Second_OwnParentOtherAndLocal_ReadAsTheSpecSays()
    {
        // Act
        var own = FamilyLabels.Second(Family(["Lena"], new FamilyMembership(Id, Role.Owner, Now, "Kasia", 1)));
        var theirs = FamilyLabels.Second(Family(["Lena"], new FamilyMembership(Id, Role.Parent, Now, "Andrzej", 2)));
        var local = FamilyLabels.Second(Family(["Lena"], null));

        // Assert
        own.Should().Be("Your family");
        theirs.Should().Be("Family of Andrzej");
        local.Should().Be("On this phone only");
    }

    [Fact]
    public void Second_PolishOwnerName_UsesTheColonFormSoTheNameStaysNominative()
    {
        // Arrange: spec §15.2 Q10.
        Strings.Use(new LanguagePreference("pl"), null);

        // Act
        var theirs = FamilyLabels.Second(Family(["Lena"], new FamilyMembership(Id, Role.Parent, Now, "Andrzej", 2)));

        // Assert
        theirs.Should().Be("Rodzina: Andrzej");
    }

    [Fact]
    public void MemberRows_OwnerAndYou_ReadAsTheSpecSays()
    {
        // Act
        var role = FamilyLabels.RoleText(Role.Owner);
        var name = FamilyLabels.MemberName(new FamilyMember(new UserId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1")), "Andrzej", "a@example.com", Role.Owner, Now, IsYou: true));

        // Assert
        role.Should().Be("Parent · created the family");
        name.Should().Be("Andrzej (you)");
    }

    [Fact]
    public void ShareText_AnInvitation_IsTheSpecsMessage()
    {
        // Arrange
        var invitation = new CreatedInvitation(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"), InvitationCode.Parse("ABCDEFGH").Value,
            new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"), new DateTimeOffset(2026, 10, 9, 14, 2, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 9))));

        // Act
        var text = FamilyLabels.ShareText(invitation, is24Hour: true);

        // Assert
        text.Should().StartWith("Join my family on Coo, so we can keep our baby's log together: https://api.coo.lechdigital.nl/i/ABCDEFGH")
            .And.Contain("enter the code ABCD-EFGH.").And.EndWith("until Fri 9 Oct, 14:02.");
    }

    [Fact]
    public void ShareText_TwelveHourClock_EndsWithTheAmPmTime()
    {
        // Arrange
        var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 9));
        var invitation = new CreatedInvitation(Guid.Parse("7a1c0000-0000-7000-8000-0000000000e1"), InvitationCode.Parse("ABCDEFGH").Value,
            new Uri("https://api.coo.lechdigital.nl/i/ABCDEFGH"), new DateTimeOffset(2026, 10, 9, 14, 2, 0, offset));

        // Act
        var text = FamilyLabels.ShareText(invitation, is24Hour: false);
        var until = FamilyLabels.Until(invitation.ExpiresAt, is24Hour: false);

        // Assert
        text.Should().EndWith("until Fri 9 Oct, 2:02 PM.");
        until.Should().Be("Fri 9 Oct, 2:02 PM");
    }
}
