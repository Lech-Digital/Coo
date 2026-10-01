namespace Coo.Domain.Common.Versioning;

/// <summary>
/// Issues <see cref="HlcStamp"/>s for one device (Spec 001 §3.3). It never reads a clock: the caller passes the
/// physical time and persists <see cref="Last"/>.
/// </summary>
public sealed class HybridLogicalClock
{
    public HybridLogicalClock(DeviceId deviceId, HlcStamp? last = null)
    {
        if (last is { } stamp && stamp.DeviceId != deviceId)
            throw new ArgumentException("The last stamp must belong to this device.", nameof(last));

        DeviceId = deviceId;
        Last = last;
    }

    public DeviceId DeviceId { get; }

    public HlcStamp? Last { get; private set; }

    /// <summary>Issues the stamp for a local write. It is greater than every stamp issued or observed so far.</summary>
    public HlcStamp Tick(long physicalNowMs)
    {
        var previous = Last;
        var wall = Math.Max(previous?.WallClockMs ?? long.MinValue, physicalNowMs);
        var counter = previous is { } p && p.WallClockMs == wall ? checked(p.Counter + 1) : 0;
        return Remember(new HlcStamp(wall, counter, DeviceId));
    }

    /// <summary>Advances past a stamp received from elsewhere, so the next <see cref="Tick"/> exceeds it.</summary>
    public HlcStamp Observe(HlcStamp remote, long physicalNowMs)
    {
        var localWall = Last?.WallClockMs ?? long.MinValue;
        var localCounter = Last?.Counter ?? -1;
        var wall = Math.Max(Math.Max(localWall, remote.WallClockMs), physicalNowMs);

        int counter;
        if (wall == localWall && wall == remote.WallClockMs)
            counter = checked(Math.Max(localCounter, remote.Counter) + 1);
        else if (wall == localWall)
            counter = checked(localCounter + 1);
        else if (wall == remote.WallClockMs)
            counter = checked(remote.Counter + 1);
        else
            counter = 0;

        return Remember(new HlcStamp(wall, counter, DeviceId));
    }

    private HlcStamp Remember(HlcStamp stamp)
    {
        Last = stamp;
        return stamp;
    }
}
