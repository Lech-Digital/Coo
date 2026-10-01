using System.Text.Json;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Domain.Tests.Common;

public sealed class HlcStampTests
{
    private static readonly DeviceId DeviceA = new(Guid.Parse("00000000-0000-7000-8000-00000000000a"));
    private static readonly DeviceId DeviceB = new(Guid.Parse("00000000-0000-7000-8000-00000000000b"));

    [Fact]
    public void Compare_SameWallClockDifferentCounter_OrdersByCounter()
    {
        // Arrange
        var lower = new HlcStamp(1000, 1, DeviceB);
        var higher = new HlcStamp(1000, 2, DeviceA);

        // Act
        var comparison = lower.CompareTo(higher);

        // Assert
        comparison.Should().BeNegative("the counter decides before the device");
    }

    [Fact]
    public void Compare_SameWallClockAndCounter_OrdersByDeviceId()
    {
        // Arrange
        var a = new HlcStamp(1000, 0, DeviceA);
        var b = new HlcStamp(1000, 0, DeviceB);

        // Act
        var comparison = a.CompareTo(b);

        // Assert
        comparison.Should().BeNegative();
        a.Should().NotBe(b, "stamps from different devices never tie");
    }

    [Fact]
    public void Compare_GoldenFixture_SortsShuffledStampsIntoFixtureOrder()
    {
        // Arrange
        var expected = LoadGoldenStamps();
        var shuffled = expected.AsEnumerable().Reverse().Concat(expected.Where((_, i) => i % 2 == 0)).Distinct().ToList();

        // Act
        var sorted = shuffled.Order().ToList();

        // Assert
        sorted.Should().Equal(expected);
    }

    [Fact]
    public void IsTooFarAhead_Exactly24HoursAhead_ReturnsFalse()
    {
        // Arrange
        const long serverNow = 1_790_000_000_000;
        var stamp = new HlcStamp(serverNow + (long)TimeSpan.FromHours(24).TotalMilliseconds, 0, DeviceA);

        // Act
        var tooFar = stamp.IsTooFarAhead(serverNow);

        // Assert
        tooFar.Should().BeFalse();
    }

    [Fact]
    public void IsTooFarAhead_24HoursAndOneMillisecondAhead_ReturnsTrue()
    {
        // Arrange
        const long serverNow = 1_790_000_000_000;
        var stamp = new HlcStamp(serverNow + (long)TimeSpan.FromHours(24).TotalMilliseconds + 1, 0, DeviceA);

        // Act
        var tooFar = stamp.IsTooFarAhead(serverNow);

        // Assert
        tooFar.Should().BeTrue();
    }

    private static List<HlcStamp> LoadGoldenStamps()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hlc-order.json")));
        return json.RootElement.GetProperty("stamps").EnumerateArray()
            .Select(s => new HlcStamp(
                s.GetProperty("wallClockMs").GetInt64(),
                s.GetProperty("counter").GetInt32(),
                new DeviceId(Guid.Parse(s.GetProperty("deviceId").GetString()!))))
            .ToList();
    }
}
