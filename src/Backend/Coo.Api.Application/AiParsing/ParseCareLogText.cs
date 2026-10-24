using System.Globalization;
using System.Text;
using Coo.Api.Application.Billing;
using Coo.Api.Application.Sync;
using Coo.Contracts.AiParsing;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Time;
using Coo.Domain.Common;

namespace Coo.Api.Application.AiParsing;

/// <summary>
/// The daily allowances (ADR 0005 §7): Premium families get many, others a few to try it. Voice has its own: a minute
/// of speech costs about what a parse does, and a family that speaks every log uses one of each (owner, 2026-10-24:
/// limits that fit the price of the subscription).
/// </summary>
public sealed record AiParsingLimitsOptions(int PremiumPerDay = 100, int FreePerDay = 5, int VoicePremiumPerDay = 60, int VoiceFreePerDay = 5);

public enum ParseRefusal
{
    None,
    NotAMember,
    TooLong,

    /// <summary>Not a JPEG or PNG, or not one at all.</summary>
    NotAnImage,

    /// <summary>Not one of the audio types, too long, or empty.</summary>
    NotAudio,
}

public sealed record ParseOutcome(ParseRefusal Refusal, AiParseResponse? Response);

/// <summary>
/// Text to proposals (ADR 0005): pseudonymise, ask the model, then deterministic code decides — times resolved against
/// when the words were captured, amounts into millilitres, implausible values flagged and never silently fixed. Nothing
/// is written to the care log and nothing of the text is stored (§1, §8).
/// </summary>
public sealed class ParseCareLogText(
    SyncAccess access, IBillingStore billing, IAiParsingStore store, ICareLogTextParser parser, AiParsingLimitsOptions limits, TimeProvider time)
{
    public static readonly TimeSpan FutureGrace = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongAgoLimit = TimeSpan.FromHours(48);
    public static readonly TimeSpan LongSleepLimit = TimeSpan.FromHours(16);
    public const decimal MinBottleMl = 5;
    public const decimal MaxBottleMl = 400;

    public async Task<ParseOutcome> ExecuteAsync(UserId user, AiParseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var household = new HouseholdId(request.HouseholdId);
        if ((await access.CheckAsync(user, household, cancellationToken)).Denial != AccessDenial.None)
            return new ParseOutcome(ParseRefusal.NotAMember, null);
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > AiParsingLimits.MaxTextLength)
            return new ParseOutcome(ParseRefusal.TooLong, null);

        var zone = Zone(request.TimeZoneId);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
        if (await RefusedAsync(household, day, cancellationToken) is { } refused)
            return refused;

        var (babies, caregivers) = await store.NamesAsync(household, cancellationToken);
        var names = babies.Select(b => new KnownName(b, "Baby A"))
            .Concat(caregivers.Select((c, i) => new KnownName(c, $"Caregiver {i + 1}")))
            .ToList();
        // First names too: "Andrzej Lech" is called Andrzej.
        names.AddRange(caregivers
            .Select((c, i) => (First: c.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? c, Index: i))
            .Where(c => !string.IsNullOrWhiteSpace(c.First))
            .Select(c => new KnownName(c.First, $"Caregiver {c.Index + 1}")));

        var text = TextPseudonymiser.Pseudonymise(request.Text, names);
        var parsed = await parser.ParseAsync(new ParsePrompt(text, Context(request, zone)), cancellationToken);
        if (parsed is null)
            return Answer([], [], "", "ai.unavailable", null);

        // Counted only once answered: a failure does not use up an allowance.
        await store.CountAsync(household, day, cancellationToken);

        var proposals = new List<AiProposal>();
        var unparsed = parsed.Unparsed.Select(u => TextPseudonymiser.Restore(u, names)).ToList();
        var understood = request.Actions ?? AiActions.Original;
        foreach (var entry in parsed.Entries)
        {
            var proposal = Propose(entry, request.CapturedAt, request.Context, zone, names, null);
            // An action this phone cannot show goes back as words, never as something it would drop or misread.
            if (proposal is null || !understood.Contains(proposal.Action))
                unparsed.Add(TextPseudonymiser.Restore(entry.SourceText, names));
            else
                proposals.Add(proposal);
        }

        return Answer(proposals, unparsed, parsed.PromptVersion, null, null);
    }

    /// <summary>
    /// A recording to text, for Tell Coo or the Advisor. The model hears the names: unlike typed words they cannot be
    /// replaced first, which the app says before the first recording. Nothing of the recording is kept.
    /// </summary>
    public async Task<(ParseRefusal Refusal, AiTranscribeResponse? Response)> TranscribeAsync(
        UserId user, AiTranscribeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var household = new HouseholdId(request.HouseholdId);
        if ((await access.CheckAsync(user, household, cancellationToken)).Denial != AccessDenial.None)
            return (ParseRefusal.NotAMember, null);
        if (DecodeAudio(request) is not { } audio)
            return (ParseRefusal.NotAudio, null);

        var zone = Zone(request.TimeZoneId);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
        var isPremium = await billing.IsHouseholdPremiumAsync(household, cancellationToken);
        var limit = isPremium ? limits.VoicePremiumPerDay : limits.VoiceFreePerDay;
        if (await store.UsedVoiceAsync(household, day, cancellationToken) >= limit)
            return (ParseRefusal.None, new AiTranscribeResponse("", isPremium ? "ai.voice-daily-limit" : "ai.voice-free-limit", limit));

        var hints = (request.Hints ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).Distinct().Take(60).ToList();
        var text = await parser.TranscribeAsync(new SpeechPrompt(audio, request.MimeType, hints, request.Language, request.Purpose == "advisor"), cancellationToken);
        if (text is null)
            return (ParseRefusal.None, new AiTranscribeResponse("", "ai.unavailable"));
        await store.CountVoiceAsync(household, day, cancellationToken);
        return (ParseRefusal.None, new AiTranscribeResponse(text.Trim()));
    }

    private static byte[]? DecodeAudio(AiTranscribeRequest request)
    {
        if (!AiParsingLimits.AudioTypes.Contains(request.MimeType) || string.IsNullOrEmpty(request.AudioBase64)
            || request.AudioBase64.Length > (AiParsingLimits.MaxAudioBytes / 3 * 4) + 4)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(request.AudioBase64);
            return bytes.Length < 64 ? null : bytes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The actions a photo can give: what health records carry. A photo of anything else gives nothing, never a feed.
    /// </summary>
    public static IReadOnlyList<string> ImageActions { get; } = [AiActions.GrowthLog, AiActions.VaccineLog];

    /// <summary>
    /// A photo of a health record to proposals: the measurements and vaccinations on it, each dated from the date or age
    /// written next to it. Counted like a parse; the photo is sent to the model to read and kept nowhere. Names on it
    /// cannot be replaced first, which the phone says before the photo is taken.
    /// </summary>
    public async Task<ParseOutcome> ExecuteImageAsync(UserId user, AiImageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var household = new HouseholdId(request.HouseholdId);
        if ((await access.CheckAsync(user, household, cancellationToken)).Denial != AccessDenial.None)
            return new ParseOutcome(ParseRefusal.NotAMember, null);
        if (Decode(request) is not { } image)
            return new ParseOutcome(ParseRefusal.NotAnImage, null);

        var zone = Zone(request.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
        if (await RefusedAsync(household, today, cancellationToken) is { } refused)
            return refused;

        var context = new StringBuilder();
        context.AppendLine(CultureInfo.InvariantCulture, $"Today is {today:yyyy-MM-dd}.");
        if (request.BirthDate is { } born)
            context.AppendLine(CultureInfo.InvariantCulture, $"The baby was born on {born:yyyy-MM-dd}.");
        if (!string.IsNullOrWhiteSpace(request.Language))
            context.AppendLine(CultureInfo.InvariantCulture, $"The app's language is \"{request.Language}\"; the record may be in any language.");
        var parsed = await parser.ReadImageAsync(new ImagePrompt(image, request.MimeType, context.ToString()), cancellationToken);
        if (parsed is null)
            return Answer([], [], "", "ai.unavailable", null);
        await store.CountAsync(household, today, cancellationToken);

        var noContext = new AiParseContext([], null, "ml");
        var proposals = new List<AiProposal>();
        var unparsed = parsed.Unparsed.ToList();
        foreach (var entry in parsed.Entries)
        {
            var proposal = Propose(entry, request.CapturedAt, noContext, zone, [], request.BirthDate ?? DateOnly.MinValue);
            if (proposal is null || !ImageActions.Contains(proposal.Action))
                unparsed.Add(entry.SourceText);
            else
                proposals.Add(proposal);
        }

        return Answer([.. proposals.OrderBy(p => p.At)], unparsed, parsed.PromptVersion, null, null);
    }

    // A JPEG or PNG by its first bytes, within the size limit; anything else is refused before the model sees it.
    private static byte[]? Decode(AiImageRequest request)
    {
        if (request.MimeType is not ("image/jpeg" or "image/png") || string.IsNullOrEmpty(request.ImageBase64)
            || request.ImageBase64.Length > (AiParsingLimits.MaxImageBytes / 3 * 4) + 4)
            return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.ImageBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        var jpeg = bytes is [0xFF, 0xD8, 0xFF, ..];
        var png = bytes is [0x89, 0x50, 0x4E, 0x47, ..];
        return (jpeg && request.MimeType == "image/jpeg") || (png && request.MimeType == "image/png") ? bytes : null;
    }

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found) ? found : TimeZoneInfo.Utc;

    // The day's allowance (ADR 0005 §7), shared by words and photos: null while some is left.
    private async Task<ParseOutcome?> RefusedAsync(HouseholdId household, DateOnly day, CancellationToken cancellationToken)
    {
        var isPremium = await billing.IsHouseholdPremiumAsync(household, cancellationToken);
        var limit = isPremium ? limits.PremiumPerDay : limits.FreePerDay;
        return await store.UsedAsync(household, day, cancellationToken) >= limit
            ? Answer([], [], "", isPremium ? "ai.daily-limit" : "ai.free-limit", limit)
            : null;
    }

    private static ParseOutcome Answer(IReadOnlyList<AiProposal> proposals, IReadOnlyList<string> unparsed, string version, string? error, int? limit) =>
        new(ParseRefusal.None, new AiParseResponse(proposals, unparsed, version, error, limit));

    /// <summary>The facts the model needs, in plain English: it never sees IDs, emails or past entries (ADR 0005 §4).</summary>
    public static string Context(AiParseRequest request, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(request);
        var local = TimeZoneInfo.ConvertTime(request.CapturedAt, zone);
        var context = new StringBuilder();
        context.AppendLine(CultureInfo.InvariantCulture, $"Said at {local:HH:mm} local time.");
        context.AppendLine("The baby is called Baby A.");
        var running = request.Context.Running;
        context.AppendLine(running.Count == 0
            ? "Nothing is running now."
            : "Running now: " + string.Join(", ", running.Select(r => r switch
            {
                AiRunningKinds.BreastLeft => "a breast feed on the left side",
                AiRunningKinds.BreastRight => "a breast feed on the right side",
                AiRunningKinds.BreastPaused => "a paused breast feed",
                AiRunningKinds.Sleep => "a sleep (the baby is asleep)",
                AiRunningKinds.Pump => "a pumping session",
                _ => r,
            })) + ".");
        if (request.Context.LastBreastSide is { } side)
            context.AppendLine(CultureInfo.InvariantCulture, $"The last breast feed ended on the {side} side.");
        context.AppendLine(CultureInfo.InvariantCulture, $"Amounts without a unit are in {(request.Context.VolumeUnit == "oz" ? "ounces (oz)" : "millilitres (ml)")}.");
        if (!string.IsNullOrWhiteSpace(request.Language))
            context.AppendLine(CultureInfo.InvariantCulture, $"The app's language is \"{request.Language}\"; the text may be in any language.");
        return context.ToString();
    }

    /// <param name="born">From a photo: the birth date (MinValue when unknown); entries are dated, not timed. Null for words.</param>
    private static AiProposal? Propose(
        ParsedEntry entry, DateTimeOffset capturedAt, AiParseContext context, TimeZoneInfo zone, IReadOnlyList<KnownName> names, DateOnly? born)
    {
        var action = entry.Type switch
        {
            "BreastStart" => AiActions.BreastStart,
            "BreastSwitch" => AiActions.BreastSwitch,
            "BreastStop" => AiActions.BreastStop,
            "BreastFeeding" => AiActions.BreastLog,
            "BottleFeeding" => AiActions.BottleLog,
            "Diaper" => AiActions.DiaperLog,
            "SleepStart" => AiActions.SleepStart,
            "SleepEnd" => AiActions.SleepEnd,
            "Sleep" => AiActions.SleepLog,
            "PumpStart" => AiActions.PumpStart,
            "PumpStop" => AiActions.PumpStop,
            "Pumping" => AiActions.PumpLog,
            "Medicine" => AiActions.MedicineLog,
            "Temperature" => AiActions.TemperatureLog,
            "Growth" => AiActions.GrowthLog,
            "Vaccine" => AiActions.VaccineLog,
            "Visit" => AiActions.VisitLog,
            "Bath" => AiActions.BathLog,
            _ => null,
        };
        if (action is null)
            return null;

        var warnings = new List<string>();
        Result<ResolvedTime> at;
        if (born is { } birth)
        {
            if (Dated(entry, birth, zone) is not { } dated)
                return null;
            at = Result<ResolvedTime>.Ok(new ResolvedTime(dated, null));
        }
        else
        {
            at = TimeExpressionResolver.Resolve(entry.Time, capturedAt, zone);
        }

        if (at.IsFailure)
            return null;
        DateTimeOffset? endAt = null;
        if (born is null && entry.EndTime is { } endExpression and not UnspecifiedExpression)
        {
            var end = TimeExpressionResolver.Resolve(endExpression, capturedAt, zone);
            if (end.IsFailure)
                return null;
            endAt = end.Value.At;
        }

        // "Now" is what a start or a stop means when no time is said; elsewhere an unstated time is worth a look.
        var isLiveAction = action is AiActions.BreastStart or AiActions.BreastSwitch or AiActions.BreastStop
            or AiActions.SleepStart or AiActions.SleepEnd or AiActions.PumpStart or AiActions.PumpStop;
        if (at.Value.Warning is not null && !isLiveAction)
            warnings.Add(AiWarnings.TimeNotStated);
        if (at.Value.At > capturedAt + FutureGrace || endAt > capturedAt + FutureGrace)
            warnings.Add(AiWarnings.InTheFuture);
        // A record on paper is about the past: only words are expected to be recent.
        if (born is null && capturedAt - at.Value.At > LongAgoLimit)
            warnings.Add(AiWarnings.LongAgo);
        if (endAt is { } finish && finish - at.Value.At > LongSleepLimit)
            warnings.Add(AiWarnings.LongSleep);
        if (endAt is { } before && before < at.Value.At)
            return null;

        var running = context.Running;
        var nothingRunning = action switch
        {
            AiActions.BreastSwitch or AiActions.BreastStop => !running.Any(r => r.StartsWith("breast", StringComparison.Ordinal)),
            AiActions.SleepEnd => !running.Contains(AiRunningKinds.Sleep),
            AiActions.PumpStop => !running.Contains(AiRunningKinds.Pump),
            _ => false,
        };
        if (nothingRunning)
            warnings.Add(AiWarnings.NothingRunning);

        decimal? millilitres = entry.Amount is { } amount && amount > 0
            ? Math.Round((entry.Unit ?? context.VolumeUnit) == "oz" ? amount * Volume.MillilitresPerUsFluidOunce : amount, 0)
            : null;
        if (action == AiActions.BottleLog)
        {
            if (millilitres is null)
                return null;
            if (millilitres < MinBottleMl || millilitres > MaxBottleMl)
                warnings.Add(AiWarnings.UnusualAmount);
        }

        if (action == AiActions.BreastStart && entry.Side is not ("left" or "right"))
            return null;
        if (action == AiActions.BreastLog && (entry.LeftMinutes ?? 0) + (entry.RightMinutes ?? 0) <= 0)
            return null;
        if (action == AiActions.DiaperLog && entry.Contents is not ("wet" or "dirty" or "mixed"))
            return null;
        if (action == AiActions.SleepLog && endAt is null)
            return null;

        // Health and growth go through the Domain's own factories: what they refuse is never proposed.
        string? name = entry.Name is { } said ? TextPseudonymiser.Restore(said.Trim(), names) : null;
        string? dose = entry.Dose?.Trim();
        decimal? celsius = null;
        int? grams = null, lengthMm = null, headMm = null;
        switch (action)
        {
            case AiActions.MedicineLog when MedicineDetails.Create(name, dose).IsFailure:
            case AiActions.VaccineLog when VaccineDetails.Create(name).IsFailure:
            case AiActions.VisitLog when VisitDetails.Create(name).IsFailure:
                return null;
            case AiActions.TemperatureLog:
                if (entry.Temperature is not { } degrees)
                    return null;
                // A number over 50 can only be °F, whatever unit came back.
                var isFahrenheit = entry.TemperatureUnit == "f" || degrees > 50;
                var reading = TemperatureDetails.Create(isFahrenheit ? (degrees - 32m) * 5m / 9m : degrees);
                if (reading.IsFailure)
                    return null;
                celsius = reading.Value.Celsius;
                break;
            case AiActions.GrowthLog:
                grams = entry.Weight is { } w and > 0 ? (int)Math.Round(entry.WeightUnit switch { "g" => w, "lb" => w * 453.59237m, _ => w * 1000m }) : null;
                var perUnit = entry.LengthUnit == "in" ? 25.4m : 10m;
                lengthMm = entry.Length is { } l and > 0 ? (int)Math.Round(l * perUnit) : null;
                headMm = entry.Head is { } h and > 0 ? (int)Math.Round(h * perUnit) : null;
                if (GrowthDetails.Create(grams, lengthMm, headMm).IsFailure)
                    return null;
                break;
        }

        return new AiProposal(
            action,
            at.Value.At,
            endAt,
            entry.Side is "left" or "right" ? entry.Side : null,
            entry.LeftMinutes is > 0 ? entry.LeftMinutes : null,
            entry.RightMinutes is > 0 ? entry.RightMinutes : null,
            millilitres,
            entry.Milk is "formula" or "breast" ? entry.Milk : null,
            entry.Contents,
            TextPseudonymiser.Restore(entry.SourceText, names),
            warnings,
            action is AiActions.MedicineLog or AiActions.VaccineLog or AiActions.VisitLog ? name : null,
            action == AiActions.MedicineLog ? dose : null,
            celsius,
            grams,
            lengthMm,
            headMm);
    }

    /// <summary>
    /// A photo's entry as an instant: its written date, or the birth date plus its age, at noon local time (a record gives
    /// the day, rarely the hour). Before the birth or with nothing to date it by: null, never a guess.
    /// </summary>
    private static DateTimeOffset? Dated(ParsedEntry entry, DateOnly born, TimeZoneInfo zone)
    {
        var known = born != DateOnly.MinValue;
        DateOnly? date = entry.Date
            ?? (known && entry.AgeDays is >= 0 and <= 1100 ? born.AddDays(entry.AgeDays.Value) : (DateOnly?)null)
            ?? (known && entry.AgeMonths is >= 0 and <= 36 ? born.AddMonths(entry.AgeMonths.Value) : (DateOnly?)null);
        if (date is not { } day || (known && day < born))
            return null;
        var local = day.ToDateTime(new TimeOnly(12, 0));
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
