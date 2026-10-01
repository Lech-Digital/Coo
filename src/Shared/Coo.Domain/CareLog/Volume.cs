using Coo.Domain.Common;

namespace Coo.Domain.CareLog;

/// <summary>A volume of milk, stored canonically in millilitres rounded to 0.1 ml (Spec 001 §4.1, ADR 0006 §5).</summary>
public readonly record struct Volume : IComparable<Volume>
{
    public const decimal MillilitresPerUsFluidOunce = 29.5735m;
    public const decimal MaxMillilitres = 1000m;
    public const string OutOfRange = "volume.out-of-range";

    private Volume(decimal millilitres) => Millilitres = millilitres;

    public decimal Millilitres { get; }

    public decimal Ounces => Millilitres / MillilitresPerUsFluidOunce;

    public static Result<Volume> FromMillilitres(decimal millilitres)
    {
        var rounded = Math.Round(millilitres, 1, MidpointRounding.AwayFromZero);
        return rounded is > 0m and <= MaxMillilitres
            ? new Volume(rounded)
            : DomainError.Of(OutOfRange, "max", MaxMillilitres.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static Result<Volume> FromOunces(decimal ounces) => FromMillilitres(ounces * MillilitresPerUsFluidOunce);

    public static Result<Volume> Sum(IEnumerable<Volume> volumes) =>
        FromMillilitres(volumes.Sum(v => v.Millilitres));

    public int CompareTo(Volume other) => Millilitres.CompareTo(other.Millilitres);

    public static bool operator <(Volume left, Volume right) => left.CompareTo(right) < 0;

    public static bool operator >(Volume left, Volume right) => left.CompareTo(right) > 0;

    public static bool operator <=(Volume left, Volume right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Volume left, Volume right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Millilitres} ml";
}
