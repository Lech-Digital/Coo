namespace Coo.Contracts.AiParsing;

// Wire DTOs for AI logging, voice or typed (ADR 0005). Versioned by adding, never by renaming (ADR 0001 §1).
// The server only proposes: the phone shows the proposals and writes the confirmed ones through its own write path.

/// <param name="Text">What the person said or typed, at most <see cref="AiParsingLimits.MaxTextLength"/> characters.</param>
/// <param name="CapturedAt">When the words were captured: times like "20 minutes ago" are resolved against it, not against when
/// the text was parsed (a pending note may be parsed hours later).</param>
/// <param name="Context">What is happening now, so short phrases resolve: "she woke up", "other side".</param>
/// <param name="Actions">The <see cref="AiActions"/> this phone can show and save. Null is a phone from before health
/// and growth (1.1): it gets <see cref="AiActions.Original"/> only, so it is never handed an action it cannot show.</param>
public sealed record AiParseRequest(
    Guid HouseholdId,
    Guid BabyId,
    string Text,
    DateTimeOffset CapturedAt,
    string TimeZoneId,
    string? Language,
    AiParseContext Context,
    IReadOnlyList<string>? Actions = null);

/// <summary>
/// A photo of a health record — a growth card, a clinic leaflet, a vaccination booklet — to read its measurements and
/// vaccinations with their dates. The server sends it to the model to read and keeps nothing of it.
/// </summary>
/// <param name="ImageBase64">A JPEG or PNG, at most <see cref="AiParsingLimits.MaxImageBytes"/> bytes before encoding.</param>
/// <param name="BirthDate">So "at birth" and "6 weeks" become dates.</param>
public sealed record AiImageRequest(
    Guid HouseholdId,
    Guid BabyId,
    string ImageBase64,
    string MimeType,
    DateTimeOffset CapturedAt,
    string TimeZoneId,
    string? Language,
    DateOnly? BirthDate);

/// <summary>
/// A spoken message to turn into text (owner, 2026-10-24: Android's recogniser understood too little): for Tell Coo,
/// whose text is then parsed as typed words, and for the Advisor, where it becomes the question. The server sends the
/// recording to the model to transcribe and keeps nothing of it.
/// </summary>
/// <param name="AudioBase64">At most <see cref="AiParsingLimits.MaxAudioBytes"/> bytes before encoding: about a minute.</param>
/// <param name="MimeType">audio/aac (Android's ADTS AAC), audio/mp4, audio/ogg, audio/wav or audio/mpeg.</param>
/// <param name="Hints">Words to expect: the baby's names, the care words. They help the model spell them right.</param>
/// <param name="Purpose">"log" (Tell Coo) or "advisor".</param>
public sealed record AiTranscribeRequest(
    Guid HouseholdId,
    string AudioBase64,
    string MimeType,
    string TimeZoneId,
    string? Language,
    IReadOnlyList<string>? Hints,
    string Purpose);

/// <param name="Text">What was said; empty when nothing was.</param>
/// <param name="ErrorCode">ai.voice-daily-limit, ai.voice-free-limit or ai.unavailable.</param>
public sealed record AiTranscribeResponse(string Text, string? ErrorCode = null, int? Limit = null);

/// <param name="Running">The sessions in progress, as <see cref="AiRunningKinds"/> codes.</param>
/// <param name="LastBreastSide">"left" or "right": the side the last breast feed ended on.</param>
/// <param name="VolumeUnit">"ml" or "oz": the unit an amount without one is in.</param>
public sealed record AiParseContext(IReadOnlyList<string> Running, string? LastBreastSide, string VolumeUnit);

/// <summary>One thing to do, already resolved to instants and millilitres. <see cref="Action"/> is one of <see cref="AiActions"/>.</summary>
/// <param name="At">When it happened, or when a session started.</param>
/// <param name="EndAt">When a logged session ended (sleep, breast feed, pump), if said.</param>
/// <param name="Side">"left" or "right" for breast actions.</param>
/// <param name="Milk">"formula" or "breast" for a bottle.</param>
/// <param name="Contents">"wet", "dirty" or "mixed" for a diaper.</param>
/// <param name="SourceText">The part of the input this came from, shown in the review.</param>
/// <param name="Warnings">Codes from <see cref="AiWarnings"/>: shown, never silently fixed.</param>
/// <param name="Name">A medicine's or a vaccine's name, or who a visit was with.</param>
/// <param name="Dose">A medicine's dose as said ("400 IU").</param>
/// <param name="Celsius">A temperature, converted to °C.</param>
/// <param name="WeightGrams">A measurement, in grams and millimetres whatever unit was said.</param>
public sealed record AiProposal(
    string Action,
    DateTimeOffset At,
    DateTimeOffset? EndAt,
    string? Side,
    int? LeftMinutes,
    int? RightMinutes,
    decimal? Millilitres,
    string? Milk,
    string? Contents,
    string SourceText,
    IReadOnlyList<string> Warnings,
    string? Name = null,
    string? Dose = null,
    decimal? Celsius = null,
    int? WeightGrams = null,
    int? LengthMm = null,
    int? HeadMm = null);

/// <param name="Unparsed">Fragments the model could not turn into an action.</param>
/// <param name="ErrorCode">ai.daily-limit, ai.free-limit or ai.unavailable; the phone keeps the text as a pending note.</param>
public sealed record AiParseResponse(
    IReadOnlyList<AiProposal> Proposals,
    IReadOnlyList<string> Unparsed,
    string PromptVersion,
    string? ErrorCode = null,
    int? Limit = null);

public static class AiParsingLimits
{
    public const int MaxTextLength = 1000;

    /// <summary>A phone photo scaled to about 1600 pixels is a few hundred kilobytes; this leaves room.</summary>
    public const int MaxImageBytes = 4_000_000;

    /// <summary>A minute of speech as 32 kbit/s AAC is about 240 kB; this leaves room for other codecs.</summary>
    public const int MaxAudioBytes = 2_000_000;

    public static readonly IReadOnlyList<string> AudioTypes = ["audio/aac", "audio/mp4", "audio/ogg", "audio/wav", "audio/mpeg"];
}

public static class AiRunningKinds
{
    public const string BreastLeft = "breast:left";
    public const string BreastRight = "breast:right";
    public const string BreastPaused = "breast:paused";
    public const string Sleep = "sleep";
    public const string Pump = "pump";
}

public static class AiActions
{
    /// <summary>Starts a breast-feed timer on <see cref="AiProposal.Side"/>.</summary>
    public const string BreastStart = "breast.start";

    /// <summary>The running breast feed goes on, on the other side.</summary>
    public const string BreastSwitch = "breast.switch";

    /// <summary>The running breast feed ends.</summary>
    public const string BreastStop = "breast.stop";

    /// <summary>A finished breast feed with minutes per side.</summary>
    public const string BreastLog = "breast.log";

    public const string BottleLog = "bottle.log";
    public const string DiaperLog = "diaper.log";
    public const string SleepStart = "sleep.start";

    /// <summary>The running sleep ends ("she woke up").</summary>
    public const string SleepEnd = "sleep.end";

    /// <summary>A finished sleep from <see cref="AiProposal.At"/> to <see cref="AiProposal.EndAt"/>.</summary>
    public const string SleepLog = "sleep.log";

    public const string PumpStart = "pump.start";
    public const string PumpStop = "pump.stop";
    public const string PumpLog = "pump.log";

    public const string MedicineLog = "medicine.log";
    public const string TemperatureLog = "temperature.log";
    public const string GrowthLog = "growth.log";
    public const string VaccineLog = "vaccine.log";
    public const string VisitLog = "visit.log";

    /// <summary>The actions of 1.1, the first Tell Coo: what a phone that sends no list understands.</summary>
    public static IReadOnlyList<string> Original { get; } =
    [
        BreastStart, BreastSwitch, BreastStop, BreastLog, BottleLog, DiaperLog, SleepStart, SleepEnd, SleepLog, PumpStart, PumpStop, PumpLog,
    ];

    /// <summary>Everything this version knows: <see cref="Original"/> and health and growth.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Original, MedicineLog, TemperatureLog, GrowthLog, VaccineLog, VisitLog];
}

public static class AiWarnings
{
    public const string TimeNotStated = "ai.time-not-stated";
    public const string InTheFuture = "ai.in-the-future";
    public const string LongAgo = "ai.long-ago";
    public const string UnusualAmount = "ai.unusual-amount";
    public const string LongSleep = "ai.long-sleep";
    public const string NothingRunning = "ai.nothing-running";
}
