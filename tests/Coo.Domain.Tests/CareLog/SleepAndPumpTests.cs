using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.CareLog.Plausibility;

namespace Coo.Domain.Tests.CareLog;

public sealed class SleepAndPumpTests
{
    [Fact]
    public void Retime_EndSleepWithin24Hours_Succeeds()
    {
        // Arrange
        var start = A.Now.AddHours(-24);
        var sleep = A.Sleep(start, null);

        // Act
        var result = sleep.Retime(EntryTiming.Session(TimeRange.Create(start, A.Now).Value), A.Stamp(), A.Now);

        // Assert
        result.IsSuccess.Should().BeTrue();
        sleep.Timing.IsInProgress.Should().BeFalse();
    }

    [Fact]
    public void Retime_SleepOver24Hours_FailsWithTooLong()
    {
        // Arrange
        var start = A.Now.AddHours(-24).AddMinutes(-1);
        var sleep = A.Sleep(start, null);

        // Act
        var result = sleep.Retime(EntryTiming.Session(TimeRange.Create(start, A.Now).Value), A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(SleepErrors.TooLong);
    }

    [Fact]
    public void Retime_EndedByDifferentCaregiver_KeepsLoggedBy()
    {
        // Arrange
        var start = A.Now.AddHours(-1);
        var sleep = A.Sleep(start, null);
        var startedBy = sleep.LoggedBy;

        // Act: the other parent ends it; the write carries their device's stamp, not their name.
        sleep.Retime(EntryTiming.Session(TimeRange.Create(start, A.Now).Value), A.Stamp(), A.Now);

        // Assert
        sleep.LoggedBy.Should().Be(startedBy);
    }

    [Fact]
    public void For_OpenSleepOver16Hours_WarnsRunningLong()
    {
        // Arrange
        var sleep = A.Sleep(A.Now.AddHours(-17), null);

        // Act
        var warnings = PlausibilityCheck.For(sleep, new PlausibilityContext(A.Now));

        // Assert
        warnings.Should().Contain(PlausibilityCodes.RunningLong);
    }

    [Fact]
    public void Create_PumpCompletedWithoutYield_Succeeds()
    {
        // Arrange
        var start = A.Now.AddMinutes(-30);

        // Act
        var pump = A.Pump(start, A.Now);

        // Assert
        ((PumpDetails)pump.Details).Yield.Should().BeNull();
    }

    [Fact]
    public void BySide_NoSides_FailsWithYieldEmpty()
    {
        // Arrange
        Volume? none = null;

        // Act
        var result = PumpYield.BySide(none, none);

        // Assert
        result.Error!.Code.Should().Be(PumpErrors.YieldEmpty);
    }

    [Fact]
    public void BySide_LeftAndRight_TotalIsSum()
    {
        // Arrange
        var left = A.Ml(70);
        var right = A.Ml(55);

        // Act
        var yield = PumpYield.BySide(left, right).Value;

        // Assert
        yield.Total.Should().Be(A.Ml(125));
    }

    [Fact]
    public void Create_PumpOver4Hours_FailsWithTooLong()
    {
        // Arrange
        var timing = EntryTiming.Session(TimeRange.Create(A.Now.AddHours(-4).AddMinutes(-1), A.Now).Value);

        // Act
        var result = CareEntry.Create(A.EntryId(), A.Household, EntrySubject.Caregiver(PersonRef.User(A.Parent)), new PumpDetails(),
            timing, PersonRef.User(A.Parent), EntrySource.Manual, A.Stamp(), A.Now);

        // Assert
        result.Error!.Code.Should().Be(PumpErrors.TooLong);
    }
}
