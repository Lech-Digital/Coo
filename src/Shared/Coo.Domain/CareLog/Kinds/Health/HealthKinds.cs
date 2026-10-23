using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.Common;

namespace Coo.Domain.CareLog.Kinds.Health;

public static class HealthErrors
{
    public const string NameRequired = "health.name-required";
    public const string TextTooLong = "health.text-too-long";
    public const string TemperatureOutOfRange = "temperature.out-of-range";

    public const int MaxNameLength = 60;

    /// <summary>A trimmed name of 1–60 characters, or the error.</summary>
    internal static Result<string> Name(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
            return Result<string>.Failure(NameRequired);
        return trimmed.Length > MaxNameLength ? Result<string>.Failure(TextTooLong) : trimmed;
    }

    /// <summary>A trimmed optional text of at most 60 characters: null when empty.</summary>
    internal static Result<string?> Optional(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return Result<string?>.Ok(null);
        return trimmed.Length > MaxNameLength ? Result<string?>.Failure(TextTooLong) : trimmed;
    }
}

/// <summary>A medicine or supplement given to the baby: vitamin D, paracetamol, antibiotics. The dose is free text ("400 IU", "2.5 ml").</summary>
public sealed record MedicineDetails : EntryDetails
{
    private MedicineDetails(string name, string? dose) => (Name, Dose) = (name, dose);

    public string Name { get; }

    public string? Dose { get; }

    public static Result<MedicineDetails> Create(string? name, string? dose) =>
        HealthErrors.Name(name).Then(n => HealthErrors.Optional(dose).Then(d => Result<MedicineDetails>.Ok(new MedicineDetails(n, d))));
}

/// <summary>A temperature reading, in °C to one decimal. The range rejects typos (a reading in °F typed as °C).</summary>
public sealed record TemperatureDetails : EntryDetails
{
    public const decimal MinCelsius = 34.0m;
    public const decimal MaxCelsius = 43.0m;

    /// <summary>At or above this, the timeline and the reports mark it as a fever (rectal, the paediatric reference).</summary>
    public const decimal FeverCelsius = 38.0m;

    private TemperatureDetails(decimal celsius) => Celsius = celsius;

    public decimal Celsius { get; }

    public bool IsFever => Celsius >= FeverCelsius;

    public static Result<TemperatureDetails> Create(decimal celsius)
    {
        var rounded = Math.Round(celsius, 1, MidpointRounding.AwayFromZero);
        return rounded is >= MinCelsius and <= MaxCelsius
            ? new TemperatureDetails(rounded)
            : Result<TemperatureDetails>.Failure(HealthErrors.TemperatureOutOfRange);
    }
}

/// <summary>A vaccination: its name as on the vaccination card.</summary>
public sealed record VaccineDetails : EntryDetails
{
    private VaccineDetails(string name) => Name = name;

    public string Name { get; }

    public static Result<VaccineDetails> Create(string? name) => HealthErrors.Name(name).Then(n => Result<VaccineDetails>.Ok(new VaccineDetails(n)));
}

/// <summary>A visit to a doctor, midwife or clinic. Who, optionally; what was said goes in the entry's note.</summary>
public sealed record VisitDetails : EntryDetails
{
    private VisitDetails(string? with) => With = with;

    public string? With { get; }

    public static Result<VisitDetails> Create(string? with) => HealthErrors.Optional(with).Then(w => Result<VisitDetails>.Ok(new VisitDetails(w)));
}

/// <summary>The <c>medicine</c> kind. The same medicine within 10 minutes is a duplicate; a different one is not.</summary>
public sealed class MedicineKind : EntryKind<MedicineDetails>
{
    public static readonly MedicineKind Instance = new();

    private MedicineKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("medicine");

    public override SubjectType Subject => SubjectType.Baby;

    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(TimeSpan.FromMinutes(10),
        (a, b) => string.Equals(((MedicineDetails)a).Name, ((MedicineDetails)b).Name, StringComparison.OrdinalIgnoreCase));

    protected override TimingRule TimingFor(MedicineDetails details) => TimingRule.PointOnly;

    protected override Result Validate(MedicineDetails details, EntryTiming timing) => Result.Success;
}

/// <summary>The <c>temperature</c> kind.</summary>
public sealed class TemperatureKind : EntryKind<TemperatureDetails>
{
    public static readonly TemperatureKind Instance = new();

    private TemperatureKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("temperature");

    public override SubjectType Subject => SubjectType.Baby;

    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(TimeSpan.FromMinutes(5));

    protected override TimingRule TimingFor(TemperatureDetails details) => TimingRule.PointOnly;

    protected override Result Validate(TemperatureDetails details, EntryTiming timing) => Result.Success;
}

/// <summary>The <c>vaccine</c> kind.</summary>
public sealed class VaccineKind : EntryKind<VaccineDetails>
{
    public static readonly VaccineKind Instance = new();

    private VaccineKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("vaccine");

    public override SubjectType Subject => SubjectType.Baby;

    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(TimeSpan.FromHours(1),
        (a, b) => string.Equals(((VaccineDetails)a).Name, ((VaccineDetails)b).Name, StringComparison.OrdinalIgnoreCase));

    protected override TimingRule TimingFor(VaccineDetails details) => TimingRule.PointOnly;

    protected override Result Validate(VaccineDetails details, EntryTiming timing) => Result.Success;
}

/// <summary>The <c>visit</c> kind: a doctor, midwife or clinic appointment.</summary>
public sealed class VisitKind : EntryKind<VisitDetails>
{
    public static readonly VisitKind Instance = new();

    private VisitKind()
    {
    }

    public override EntryKindCode Code { get; } = EntryKindCode.Known("visit");

    public override SubjectType Subject => SubjectType.Baby;

    public override DuplicateRule Duplicates { get; } = DuplicateRule.Within(TimeSpan.FromHours(1));

    protected override TimingRule TimingFor(VisitDetails details) => TimingRule.PointOnly;

    protected override Result Validate(VisitDetails details, EntryTiming timing) => Result.Success;
}
