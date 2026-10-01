using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Domain.Tests.Common;

public sealed class HybridLogicalClockTests
{
    private static readonly DeviceId Local = new(Guid.Parse("00000000-0000-7000-8000-00000000000a"));
    private static readonly DeviceId Remote = new(Guid.Parse("00000000-0000-7000-8000-00000000000b"));

    [Fact]
    public void Tick_PhysicalClockAdvances_UsesPhysicalTimeAndResetsCounter()
    {
        // Arrange
        var clock = new HybridLogicalClock(Local);
        clock.Tick(1000);

        // Act
        var stamp = clock.Tick(2000);

        // Assert
        stamp.Should().Be(new HlcStamp(2000, 0, Local));
    }

    [Fact]
    public void Tick_PhysicalClockGoesBackwards_StampStillIncreases()
    {
        // Arrange
        var clock = new HybridLogicalClock(Local);
        var first = clock.Tick(5000);

        // Act
        var second = clock.Tick(3000);

        // Assert
        second.Should().BeGreaterThan(first);
        second.Should().Be(new HlcStamp(5000, 1, Local));
    }

    [Fact]
    public void Observe_RemoteStampAhead_NextTickIsGreaterThanRemote()
    {
        // Arrange
        var clock = new HybridLogicalClock(Local);
        clock.Tick(1000);
        var remote = new HlcStamp(9000, 7, Remote);

        // Act
        clock.Observe(remote, physicalNowMs: 1500);
        var next = clock.Tick(1600);

        // Assert
        next.Should().BeGreaterThan(remote);
    }

    [Fact]
    public void Observe_SameWallClockAsLocalAndRemote_TakesMaxCounterPlusOne()
    {
        // Arrange
        var clock = new HybridLogicalClock(Local, new HlcStamp(4000, 3, Local));

        // Act
        var merged = clock.Observe(new HlcStamp(4000, 8, Remote), physicalNowMs: 100);

        // Assert
        merged.Should().Be(new HlcStamp(4000, 9, Local));
    }

    [Fact]
    public void Constructor_LastStampFromAnotherDevice_Throws()
    {
        // Arrange
        var foreign = new HlcStamp(1000, 0, Remote);

        // Act
        var act = () => new HybridLogicalClock(Local, foreign);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
