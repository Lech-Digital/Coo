namespace Coo.Mobile.Application.CareLog;

/// <summary>
/// A first guess for a bottle when this baby has had none in the last week: what a baby of that age typically takes in
/// one feed (an average; babies vary a lot). A newborn's stomach holds a few teaspoons on day one, so starting a
/// newborn's first bottle at 120 ml (4 oz) only meant tapping "-" eleven times (owner, 2026-11-03).
/// </summary>
public static class BottleSizes
{
    /// <summary>The typical amount per feed, in millilitres, for a baby <paramref name="ageDays"/> days old.</summary>
    public static decimal TypicalMillilitres(int ageDays) => ageDays switch
    {
        < 3 => 15m,
        < 7 => 45m,
        < 14 => 60m,
        < 30 => 90m,
        < 61 => 120m,
        < 183 => 150m,
        _ => 180m,
    };

    /// <summary>The same in US fluid ounces, rounded to the half ounce the bottle panel steps in.</summary>
    public static decimal TypicalOunces(int ageDays) =>
        Math.Max(0.5m, Math.Round(TypicalMillilitres(ageDays) / Coo.Domain.CareLog.Volume.MillilitresPerUsFluidOunce * 2m, MidpointRounding.AwayFromZero) / 2m);
}
