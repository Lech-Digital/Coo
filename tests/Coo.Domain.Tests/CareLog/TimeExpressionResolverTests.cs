using Coo.Domain.CareLog.Plausibility;
using Coo.Domain.CareLog.Time;

namespace Coo.Domain.Tests.CareLog;

public sealed class TimeExpressionResolverTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly TimeSpan Summer = TimeSpan.FromHours(2);
    private static readonly TimeSpan Winter = TimeSpan.FromHours(1);

    [Fact]
    public void Resolve_Clock1400CapturedAt0100_ReturnsYesterday1400()
    {
        // Arrange
        var captured = new DateTimeOffset(2026, 9, 30, 1, 0, 0, Summer);

        // Act
        var resolved = TimeExpressionResolver.Resolve(TimeExpression.Clock(14, 0), captured, Warsaw).Value;

        // Assert
        resolved.At.Should().Be(new DateTimeOffset(2026, 9, 29, 14, 0, 0, Summer));
    }

    [Fact]
    public void Resolve_Clock1400CapturedAt1358_ReturnsToday1400()
    {
        // Arrange
        var captured = new DateTimeOffset(2026, 9, 30, 13, 58, 0, Summer);

        // Act
        var resolved = TimeExpressionResolver.Resolve(TimeExpression.Clock(14, 0), captured, Warsaw).Value;

        // Assert
        resolved.At.Should().Be(new DateTimeOffset(2026, 9, 30, 14, 0, 0, Summer));
    }

    [Fact]
    public void Resolve_Clock1406CapturedAt1400_ReturnsYesterday()
    {
        // Arrange: six minutes ahead is beyond the five-minute grace.
        var captured = new DateTimeOffset(2026, 9, 30, 14, 0, 0, Summer);

        // Act
        var resolved = TimeExpressionResolver.Resolve(TimeExpression.Clock(14, 6), captured, Warsaw).Value;

        // Assert
        resolved.At.Should().Be(new DateTimeOffset(2026, 9, 29, 14, 6, 0, Summer));
    }

    [Fact]
    public void Resolve_Clock0230OnSpringForwardDay_Returns0330Local()
    {
        // Arrange: 29 March 2026, clocks jump from 02:00 to 03:00 in Warsaw.
        var captured = new DateTimeOffset(2026, 3, 29, 9, 0, 0, Summer);

        // Act
        var resolved = TimeExpressionResolver.Resolve(TimeExpression.Clock(2, 30), captured, Warsaw).Value;

        // Assert
        resolved.At.Should().Be(new DateTimeOffset(2026, 3, 29, 3, 30, 0, Summer));
        resolved.At.Offset.Should().Be(Summer);
    }

    [Fact]
    public void Resolve_ClockInRepeatedHour_ReturnsLaterAllowedOccurrence()
    {
        // Arrange: 25 October 2026, 02:30 happens twice in Warsaw (first +02:00, then +01:00).
        var captured = new DateTimeOffset(2026, 10, 25, 9, 0, 0, Winter);

        // Act
        var resolved = TimeExpressionResolver.Resolve(TimeExpression.Clock(2, 30), captured, Warsaw).Value;

        // Assert
        resolved.At.Should().Be(new DateTimeOffset(2026, 10, 25, 2, 30, 0, Winter));
    }

    [Fact]
    public void Resolve_ClockInRepeatedHourBetweenOccurrences_ReturnsFirstOccurrence()
    {
        // Arrange: captured at 02:40 summer time, so the winter 02:30 has not happened yet.
        var captured = new DateTimeOffset(2026, 10, 25, 2, 40, 0, Summer);

        // Act
        var resolved = TimeExpressionResolver.Resolve(TimeExpression.Clock(2, 30), captured, Warsaw).Value;

        // Assert
        resolved.At.Should().Be(new DateTimeOffset(2026, 10, 25, 2, 30, 0, Summer));
    }

    [Fact]
    public void Resolve_MinutesAgo20_SubtractsFromCapturedAt()
    {
        // Arrange: "20 minutes ago", spoken at 03:10 and parsed hours later, still means 02:50.
        var captured = new DateTimeOffset(2026, 9, 30, 3, 10, 0, Summer);

        // Act
        var resolved = TimeExpressionResolver.Resolve(TimeExpression.MinutesAgo(20), captured, Warsaw).Value;

        // Assert
        resolved.At.Should().Be(new DateTimeOffset(2026, 9, 30, 2, 50, 0, Summer));
    }

    [Fact]
    public void Resolve_MinutesAgo2881_Fails()
    {
        // Arrange
        var captured = A.Now;

        // Act
        var result = TimeExpressionResolver.Resolve(TimeExpression.MinutesAgo(2881), captured, Warsaw);

        // Assert
        result.Error!.Code.Should().Be(TimeExpressionResolver.MinutesAgoOutOfRange);
    }

    [Fact]
    public void Resolve_Clock2400_FailsWithClockInvalid()
    {
        // Arrange
        var captured = A.Now;

        // Act
        var result = TimeExpressionResolver.Resolve(TimeExpression.Clock(24, 0), captured, Warsaw);

        // Assert
        result.Error!.Code.Should().Be(TimeExpressionResolver.ClockInvalid);
    }

    [Fact]
    public void Resolve_Unspecified_ReturnsCapturedAtWithWarning()
    {
        // Arrange
        var captured = A.Now;

        // Act
        var resolved = TimeExpressionResolver.Resolve(TimeExpression.Unspecified, captured, Warsaw).Value;

        // Assert
        resolved.At.Should().Be(captured);
        resolved.Warning.Should().Be(PlausibilityCodes.TimeNotStated);
    }
}
