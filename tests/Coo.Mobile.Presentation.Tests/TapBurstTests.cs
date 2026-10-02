using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.Time.Testing;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>The debug entry (Spec 006 Q5): three taps within two seconds, counted from single taps.</summary>
public sealed class TapBurstTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Tap_ThreeWithinTwoSeconds_FiresOnTheThird()
    {
        // Arrange
        var burst = new TapBurst(_time);

        // Act
        var first = burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(600));
        var second = burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(600));
        var third = burst.Tap();

        // Assert
        new[] { first, second, third }.Should().Equal(false, false, true);
    }

    [Fact]
    public void Tap_ThreeSpreadOverMoreThanTwoSeconds_DoesNotFire()
    {
        // Arrange: an ordinary tap now and then on the wordmark must never open it.
        var burst = new TapBurst(_time);

        // Act
        burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(1500));
        var third = burst.Tap();

        // Assert
        third.Should().BeFalse();
    }

    [Fact]
    public void Tap_AfterFiring_CountsAgainFromZero()
    {
        // Arrange
        var burst = new TapBurst(_time);
        burst.Tap();
        burst.Tap();
        burst.Tap();

        // Act
        var fourth = burst.Tap();
        var fifth = burst.Tap();

        // Assert
        fourth.Should().BeFalse();
        fifth.Should().BeFalse();
    }

    [Fact]
    public void Tap_ThirdExactlyAtTheWindowEdge_Fires()
    {
        // Arrange
        var burst = new TapBurst(_time);

        // Act
        burst.Tap();
        _time.Advance(TimeSpan.FromSeconds(1));
        burst.Tap();
        _time.Advance(TimeSpan.FromSeconds(1));
        var third = burst.Tap();

        // Assert
        third.Should().BeTrue();
    }

    [Fact]
    public void Tap_StaleTapDropsOut_NextTwoThenFire()
    {
        // Arrange
        var burst = new TapBurst(_time);
        burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(2500));

        // Act
        var second = burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var third = burst.Tap();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var fourth = burst.Tap();

        // Assert
        new[] { second, third, fourth }.Should().Equal(false, false, true);
    }
}
