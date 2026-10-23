namespace Coo.Contracts.AiParsing;

// Wire DTOs for AI logging, voice or typed (ADR 0005). Versioned by adding, never by renaming (ADR 0001 §1).
// The server only proposes: the phone shows the proposals and writes the confirmed ones through its own write path.

/// <param name="Text">What the person said or typed, at most <see cref="AiParsingLimits.MaxTextLength"/> characters.</param>
/// <param name="CapturedAt">When the words were captured: times like "20 minutes ago" are resolved against it, not against when
/// the text was parsed (a pending note may be parsed hours later).</param>
/// <param name="Context">What is happening now, so short phrases resolve: "she woke up", "other side".</param>
public sealed record AiParseRequest(
    Guid HouseholdId,
    Guid BabyId,
    string Text,
    DateTimeOffset CapturedAt,
    string TimeZoneId,
    string? Language,
    AiParseContext Context);

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
    IReadOnlyList<string> Warnings);

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
