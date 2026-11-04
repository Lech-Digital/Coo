using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Coo.Mobile.Application.Tests;

/// <summary>The nanny's end-of-day handover: her day for the parent, her own day for her.</summary>
public sealed class HandoverTests
{
    private static readonly HouseholdId Household = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000a1"));
    private static readonly BabyId Laura = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b1"));
    private static readonly BabyId Twin = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000b2"));
    private static readonly DeviceId Device = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));
    private static readonly UserId Parent = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c1"));
    private static readonly UserId Nanny = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000c2"));
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 17, 0, 0, TimeSpan.FromHours(1));

    private readonly IFamilyStore _families = Substitute.For<IFamilyStore>();
    private readonly ISessionStore _sessions = Substitute.For<ISessionStore>();
    private long _wall = 1;

    public HandoverTests() =>
        _families.ListMemberNamesAsync(Household, Arg.Any<CancellationToken>())
            .Returns([new FamilyMemberName(Parent, "Kasia", Role.Owner), new FamilyMemberName(Nanny, "Anna", Role.Caregiver)]);

    [Fact]
    public async Task Execute_AParentAfterTheNannysDay_SeesHerFeedsNapsDiapersAndNotes()
    {
        // Arrange
        SignedIn(Parent, Role.Owner);
        var day = NannysDay();

        // Act
        var view = await Use().ExecuteAsync(Household, Laura, day, Now, CancellationToken.None);

        // Assert
        view.Mine.Should().BeNull();
        var anna = view.FromCaregivers.Should().ContainSingle().Subject;
        anna.Name.Should().Be("Anna");
        (anna.Feeds, anna.Bottles, anna.BottleMl).Should().Be((2, 2, 300m));
        (anna.Naps, anna.Sleep).Should().Be((1, TimeSpan.FromMinutes(90)));
        (anna.Diapers, anna.Dirty).Should().Be((2, 1));
        anna.Notes.Should().Equal("ate well at lunch");
        (anna.First, anna.Last).Should().Be((Now.AddHours(-8), Now.AddHours(-1)));
    }

    [Fact]
    public async Task Execute_TheNannyHerself_GetsHerOwnDayToShare()
    {
        // Arrange
        SignedIn(Nanny, Role.Caregiver);

        // Act
        var view = await Use().ExecuteAsync(Household, Laura, NannysDay(), Now, CancellationToken.None);

        // Assert
        view.FromCaregivers.Should().BeEmpty();
        view.Mine!.Name.Should().Be("Anna");
        view.Mine.Diapers.Should().Be(2, "the parent's own diaper this morning is not hers, nor the twin's");
    }

    [Fact]
    public async Task Execute_AViewerOrSignedOut_HasNoHandover()
    {
        // Arrange
        SignedIn(Parent, Role.Viewer);

        // Act
        var viewer = await Use().ExecuteAsync(Household, Laura, NannysDay(), Now, CancellationToken.None);
        _sessions.GetAsync(Arg.Any<CancellationToken>()).Returns((StoredSession?)null);
        var signedOut = await Use().ExecuteAsync(Household, Laura, NannysDay(), Now, CancellationToken.None);

        // Assert
        viewer.Should().Be(HandoverView.None);
        signedOut.Should().Be(HandoverView.None);
    }

    private GetHandover Use() => new(_families, _sessions);

    private void SignedIn(UserId user, Role role)
    {
        _sessions.GetAsync(Arg.Any<CancellationToken>()).Returns(
            new StoredSession("access", Now.AddHours(1), "refresh", new AccountProfile(user, "Someone", "someone@example.com"), Household, false));
        _families.ListAsync(user, Arg.Any<CancellationToken>()).Returns([new FamilyMembership(Household, role, Now.AddDays(-30), "Kasia", 2)]);
    }

    /// <summary>The nanny: two bottles, a nap of 90 minutes, two diapers (one dirty), a note; the parent: one diaper; the twin: one.</summary>
    private List<CareEntry> NannysDay()
    {
        var anna = new UserPerson(Nanny);
        var kasia = new UserPerson(Parent);
        return
        [
            Make(Laura, kasia, new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(Now.AddHours(-10))),
            Make(Laura, anna, BottleFeed.Of(Milk.ExpressedBreastMilk, Volume.FromMillilitres(150).Value).Value, EntryTiming.Point(Now.AddHours(-8)), "ate well at lunch"),
            Make(Laura, anna, new DiaperDetails(DiaperContents.Dirty), EntryTiming.Point(Now.AddHours(-7))),
            Make(Laura, anna, new SleepDetails(), EntryTiming.Session(TimeRange.Create(Now.AddHours(-6), Now.AddHours(-4.5)).Value)),
            Make(Laura, anna, BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(150).Value).Value, EntryTiming.Point(Now.AddHours(-3))),
            Make(Laura, anna, new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(Now.AddHours(-1))),
            Make(Twin, anna, new DiaperDetails(DiaperContents.Wet), EntryTiming.Point(Now.AddHours(-2))),
        ];
    }

    private CareEntry Make(BabyId baby, PersonRef by, EntryDetails details, EntryTiming timing, string? note = null)
    {
        var entry = CareEntry.Create(
            new EntryId(Guid.CreateVersion7(Now.AddMilliseconds(++_wall))), Household, EntrySubject.Baby(baby), details, timing, by, EntrySource.Manual,
            new HlcStamp(++_wall, 0, Device), Now).Value;
        if (note is not null)
            entry.SetNote(Note.From(note).Value, new HlcStamp(++_wall, 0, Device));
        return entry;
    }
}
