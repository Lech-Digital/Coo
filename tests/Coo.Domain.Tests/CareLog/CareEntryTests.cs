using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.CareLog.Plausibility;

namespace Coo.Domain.Tests.CareLog;

public sealed class CareEntryTests
{
    [Fact]
    public void Create_Diaper_TakesKindFromDetailsType()
    {
        // Arrange
        var details = new DiaperDetails(DiaperContents.Dirty);

        // Act
        var entry = CareEntry.Create(A.EntryId(), A.Household, A.BabySubject(), details, EntryTiming.Point(A.Now),
            PersonRef.User(A.Parent), EntrySource.Manual, A.Stamp(), A.Now).Value;

        // Assert
        entry.KindCode.Value.Should().Be("diaper");
        entry.Timing.OccurredAt.Should().Be(A.Now);
    }

    [Fact]
    public void Create_PumpWithBabySubject_FailsWithSubjectNotAllowed()
    {
        // Arrange
        var timing = EntryTiming.Session(TimeRange.Open(A.Now));

        // Act
        var result = CareEntry.Create(A.EntryId(), A.Household, A.BabySubject(), new PumpDetails(), timing,
            PersonRef.User(A.Parent), EntrySource.Manual, A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(EntryErrors.SubjectNotAllowed);
    }

    [Fact]
    public void Create_DiaperWithSessionTiming_FailsWithTimingShapeNotAllowed()
    {
        // Arrange
        var timing = EntryTiming.Session(TimeRange.Open(A.Now));

        // Act
        var result = CareEntry.Create(A.EntryId(), A.Household, A.BabySubject(), new DiaperDetails(DiaperContents.Wet), timing,
            PersonRef.User(A.Parent), EntrySource.Manual, A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(EntryErrors.TimingShapeNotAllowed);
    }

    [Fact]
    public void Create_BreastFeedWithExplicitTiming_FailsWithTimingDerived()
    {
        // Arrange
        var feed = BreastFeed.Start(BreastSide.Left, A.Now);

        // Act
        var result = CareEntry.Create(A.EntryId(), A.Household, A.BabySubject(), feed, EntryTiming.Session(TimeRange.Open(A.Now)),
            PersonRef.User(A.Parent), EntrySource.Timer, A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(EntryErrors.TimingDerived);
    }

    [Fact]
    public void Create_Instant25HoursAhead_FailsWithTooFarInFuture()
    {
        // Arrange
        var at = A.Now.AddHours(25);

        // Act
        var result = CareEntry.Create(A.EntryId(), A.Household, A.BabySubject(), new DiaperDetails(DiaperContents.Wet),
            EntryTiming.Point(at), PersonRef.User(A.Parent), EntrySource.Manual, A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(EntryErrors.TooFarInFuture);
    }

    [Fact]
    public void Create_Instant10MinutesAhead_SucceedsWithInFutureWarning()
    {
        // Arrange
        var at = A.Now.AddMinutes(10);

        // Act
        var entry = A.Diaper(at);
        var warnings = PlausibilityCheck.For(entry, new PlausibilityContext(A.Now));

        // Assert
        warnings.Should().Contain(PlausibilityCodes.InFuture);
    }

    [Fact]
    public void Revise_WrongDetailsType_FailsWithDetailsTypeMismatch()
    {
        // Arrange
        var entry = A.Diaper(A.Now);

        // Act
        var result = entry.Revise<SleepDetails>(d => d.WithKindOverride(SleepKindOverride.Nap), A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(EntryErrors.DetailsTypeMismatch);
    }

    [Fact]
    public void Revise_OnDeletedEntry_FailsWithRecordDeleted()
    {
        // Arrange
        var entry = A.Diaper(A.Now);
        entry.Delete(A.Stamp());

        // Act
        var result = entry.Revise<DiaperDetails>(d => d.WithContents(DiaperContents.Mixed), A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(Coo.Domain.Common.RecordErrors.Deleted);
    }

    [Fact]
    public void Revise_DiaperContents_ChangesDetailsAndVersion()
    {
        // Arrange
        var entry = A.Diaper(A.Now);
        var version = A.Stamp();

        // Act
        var result = entry.Revise<DiaperDetails>(d => d.WithContents(DiaperContents.Mixed), version, A.Now);

        // Assert
        result.IsSuccess.Should().BeTrue();
        ((DiaperDetails)entry.Details).Contents.Should().Be(DiaperContents.Mixed);
        entry.Version.Should().Be(version);
    }

    [Fact]
    public void Retime_BreastFeed_FailsWithTimingDerived()
    {
        // Arrange
        var entry = A.Breast(A.Now.AddHours(-1));

        // Act
        var result = entry.Retime(EntryTiming.Session(TimeRange.Open(A.Now)), A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(EntryErrors.TimingDerived);
    }
}
