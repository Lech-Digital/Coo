using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>Walk fix F4: Today's and Log's rows say who logged an entry, now that a family is shared.</summary>
[Collection(nameof(StringsState))]
public sealed class WhoLabelsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 21, 0, 0, TimeSpan.Zero);
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Baby = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly UserId Me = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly UserId Kasia = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));
    private static readonly UserId OtherAndrzej = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c3"));
    private static readonly UserId Removed = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c4"));

    public WhoLabelsTests() => Strings.Use(new LanguagePreference("en"), null);

    public void Dispose() => Strings.Use(LanguagePreference.System, null);

    private static WhoLabels Family() => WhoLabels.For(Me, [
        new FamilyMemberName(Me, "Andrzej Lech"), new FamilyMemberName(Kasia, "Kasia Nowak"), new FamilyMemberName(OtherAndrzej, "Andrzej Kowalski")]);

    private static ReadOnlyEntry LoggedBy(PersonRef? person) =>
        new ReadOnlyEntry(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f1")), "diaper", Now, null, person, "newer app");

    [Fact]
    public void For_LoggedByTheSignedInUser_IsYou()
    {
        // Act
        var who = Family().For(LoggedBy(PersonRef.User(Me)));

        // Assert
        who.Should().Be("You");
    }

    [Fact]
    public void For_LoggedOnThisPhoneBeforeItWasShared_IsYou()
    {
        // Act
        var who = Family().For(LoggedBy(PersonRef.ThisDevicesPerson));

        // Assert
        who.Should().Be("You");
    }

    [Fact]
    public void For_LoggedByAnotherMember_IsTheirFirstName()
    {
        // Act
        var who = Family().For(LoggedBy(PersonRef.User(Kasia)));

        // Assert
        who.Should().Be("Kasia");
    }

    [Fact]
    public void For_TwoMembersShareAFirstName_IsTheFullName()
    {
        // Act: the owner's two test accounts are both "Andrzej".
        var who = Family().For(LoggedBy(PersonRef.User(OtherAndrzej)));

        // Assert
        who.Should().Be("Andrzej Kowalski");
    }

    [Fact]
    public void For_LoggedBySomeoneNotInTheFamilyList_IsAnotherParent()
    {
        // Act: a member removed since.
        var who = Family().For(LoggedBy(PersonRef.User(Removed)));

        // Assert
        who.Should().Be("Another parent");
    }

    [Fact]
    public void For_SignedOut_IsYouAsBefore()
    {
        // Act
        var who = WhoLabels.SignedOut.For(LoggedBy(PersonRef.User(Kasia)));

        // Assert
        who.Should().Be("You");
    }

    [Fact]
    public void Update_TimelineEntryLoggedByAnotherParent_ShowsTheirName()
    {
        // Arrange
        var section = new TimelineSection();

        // Act
        section.Update(TimelineSnapshot(Family()));

        // Assert
        section.Items.Single().Who.Should().Be("Kasia");
    }

    [Fact]
    public void Update_TheNamesArriveAfterTheFirstDraw_TheRowSaysTheName()
    {
        // Arrange: the first family list refresh after this update stores the names.
        var section = new TimelineSection();
        section.Update(TimelineSnapshot(WhoLabels.For(Me, [])));

        // Act
        section.Update(TimelineSnapshot(Family()));

        // Assert
        section.Items.Single().Who.Should().Be("Kasia");
    }

    private static HomeSnapshot TimelineSnapshot(WhoLabels who)
    {
        var entry = CareEntry.Create(new EntryId(Guid.Parse("7a1c0000-0000-7000-8000-0000000000f2")), Household, EntrySubject.Baby(Baby), new Coo.Domain.CareLog.Kinds.Sleep.SleepDetails(),
            EntryTiming.Session(TimeRange.Open(Now.AddHours(-1))), PersonRef.User(Kasia), EntrySource.Timer, new HlcStamp(1, 0, Device), Now).Value;
        var settings = HouseholdSettings.Create(Household, VolumeUnit.Millilitres, "Europe/Amsterdam", new HlcStamp(1, 0, Device)).Value;
        var baby = BabyProfile.Create(Baby, Household, "Zosia", [], new DateOnly(2026, 5, 1), null, new HlcStamp(1, 0, Device), new DateOnly(2026, 10, 1)).Value;
        var summary = new TodaySummary([], null, null, null, null, null, [new LiveEntry(entry)], DayMath.Day(new DateOnly(2026, 10, 6), TimeZoneInfo.Utc));
        return new HomeSnapshot(settings, baby, summary, Now, PaletteKind.Day, new Formats(true, VolumeUnit.Millilitres), Who: who);
    }

    [Fact]
    public async Task ForAsync_SignedIn_UsesTheShownFamilysStoredNames()
    {
        // Arrange
        var sessions = new MemorySessions();
        await sessions.SaveAsync(new StoredSession("a", Now, "r", new AccountProfile(Me, "Andrzej Lech", "a@example.com"), Household, JoinPending: false), CancellationToken.None);
        var store = Substitute.For<IFamilyStore>();
        store.ListMemberNamesAsync(Household, Arg.Any<CancellationToken>()).Returns([new FamilyMemberName(Kasia, "Kasia Nowak")]);

        // Act
        var labels = await new WhoLabelsSource(sessions, store).ForAsync(Household);

        // Assert
        labels.For(LoggedBy(PersonRef.User(Kasia))).Should().Be("Kasia");
        labels.For(LoggedBy(PersonRef.User(Me))).Should().Be("You");
    }
}
