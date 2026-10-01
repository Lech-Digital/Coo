namespace Coo.Domain.CareLog.Plausibility;

/// <summary>Every warning code (Spec 001 §8). Warnings never block a save.</summary>
public static class PlausibilityCodes
{
    public static readonly Warning InFuture = new("warn.in-future");
    public static readonly Warning BeforeBirth = new("warn.before-birth");
    public static readonly Warning LongAgo = new("warn.long-ago");
    public static readonly Warning TimeNotStated = new("warn.time-not-stated");
    public static readonly Warning RunningLong = new("warn.running-long");
    public static readonly Warning BottleAmount = new("warn.bottle-amount");
    public static readonly Warning BreastLong = new("warn.breast-long");
    public static readonly Warning SleepLong = new("warn.sleep-long");
    public static readonly Warning PumpAmount = new("warn.pump-amount");
}

/// <summary>The thresholds of the checks shared by every kind (Spec 001 §8).</summary>
public static class PlausibilityThresholds
{
    public static readonly TimeSpan InFutureAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongAgoBeforeCapture = TimeSpan.FromHours(48);
}
