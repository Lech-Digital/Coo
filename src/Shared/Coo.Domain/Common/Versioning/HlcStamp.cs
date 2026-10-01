namespace Coo.Domain.Common.Versioning;

/// <summary>
/// A hybrid logical clock stamp (ADR 0002 §4, Spec 001 §3.3). Ordered by wall clock, then counter, then device,
/// so every pair of stamps compares, and never ties.
/// </summary>
public readonly record struct HlcStamp(long WallClockMs, int Counter, DeviceId DeviceId) : IComparable<HlcStamp>
{
    /// <summary>The server refuses stamps further ahead of its own clock than this (ADR 0002 §4).</summary>
    public static readonly TimeSpan MaxAheadOfServer = TimeSpan.FromHours(24);

    public int CompareTo(HlcStamp other)
    {
        var byWall = WallClockMs.CompareTo(other.WallClockMs);
        if (byWall != 0)
            return byWall;

        var byCounter = Counter.CompareTo(other.Counter);
        return byCounter != 0 ? byCounter : GuidOrder.Compare(DeviceId.Value, other.DeviceId.Value);
    }

    public bool IsTooFarAhead(long serverNowMs) =>
        WallClockMs - serverNowMs > (long)MaxAheadOfServer.TotalMilliseconds;

    public static bool operator <(HlcStamp left, HlcStamp right) => left.CompareTo(right) < 0;

    public static bool operator >(HlcStamp left, HlcStamp right) => left.CompareTo(right) > 0;

    public static bool operator <=(HlcStamp left, HlcStamp right) => left.CompareTo(right) <= 0;

    public static bool operator >=(HlcStamp left, HlcStamp right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{WallClockMs}:{Counter}:{DeviceId}";
}
