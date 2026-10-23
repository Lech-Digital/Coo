using Coo.Contracts.AiParsing;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Duplicates;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Growth;
using Coo.Domain.CareLog.Kinds.Health;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.AiLogging;

/// <summary>
/// One proposal in the review (ADR 0005 §1, §3a). <see cref="Candidate"/> is the entry a log action would save, already
/// through the domain's rules; <see cref="Duplicate"/> is an entry already on this phone it probably repeats ("Already
/// logged"), which unselects it by default. <see cref="Error"/> is a domain refusal: it cannot be saved.
/// </summary>
public sealed record QuickLogItem(AiProposal Proposal, CareEntry? Candidate, CareEntry? Duplicate, string? Error)
{
    public bool IsSelectedByDefault => Error is null && Duplicate is null;
}

public sealed record QuickLogReview(
    string Text, DateTimeOffset CapturedAt, IReadOnlyList<QuickLogItem> Items, IReadOnlyList<string> Unparsed, string PromptVersion);

/// <summary>A review to show; or the words kept as a pending note (offline, server busy); or a refusal (a used-up allowance).</summary>
public sealed record QuickLogPrepared(QuickLogReview? Review, bool KeptAsPendingNote, string? ErrorCode, int? Limit);

/// <summary>
/// Words to proposals (ADR 0005): what is happening now goes along, so "other side" and "she woke up" resolve; the
/// answer is checked against what is already logged. Nothing is saved here — that waits for the person (§1).
/// </summary>
public sealed class PrepareQuickLog(IHouseholdStore households, GetToday today, ICareLogStore store, IAiParseApi api, IPendingNoteStore notes, TimeProvider time)
{
    public async Task<QuickLogPrepared> ExecuteAsync(string text, DateTimeOffset capturedAt, string language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var trimmed = text.Trim();
        if (trimmed.Length > AiParsingLimits.MaxTextLength)
            trimmed = trimmed[..AiParsingLimits.MaxTextLength];

        var (household, baby) = await ShownAsync(households, cancellationToken);
        if (household is null || baby is null)
            return new QuickLogPrepared(null, false, "ai.no-baby", null);

        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var summary = await today.ExecuteAsync(household.Id, baby.Id, zone, time.GetUtcNow(), cancellationToken);
        var request = new AiParseRequest(household.Id.Value, baby.Id.Value, trimmed, capturedAt, household.TimeZoneId, language, Context(summary, household), AiActions.All);

        AiParseResponse? response;
        try
        {
            response = await api.ParseAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response = null;
        }

        // Never lost (ADR 0005 §3): no answer, or a server that could not parse, keeps the words for later.
        if (response is null || response.ErrorCode == "ai.unavailable")
        {
            await notes.AddAsync(new PendingNote(Guid.CreateVersion7(time.GetUtcNow()), trimmed, capturedAt), cancellationToken);
            return new QuickLogPrepared(null, true, response?.ErrorCode, null);
        }

        if (response.ErrorCode is { } refused)
            return new QuickLogPrepared(null, false, refused, response.Limit);

        var existing = (await store.ListAsync(household.Id, capturedAt.AddDays(-2), capturedAt.AddHours(1), cancellationToken))
            .OfType<LiveEntry>().Select(l => l.Entry).Where(e => !e.IsDeleted).ToList();
        var items = response.Proposals
            .Select(p => Item(p, household.Id, baby.Id, response.PromptVersion, existing, capturedAt))
            .ToList();
        return new QuickLogPrepared(new QuickLogReview(trimmed, capturedAt, items, response.Unparsed, response.PromptVersion), false, null, null);
    }

    /// <summary>
    /// A photo of a health record to a review: its measurements and vaccinations, dated as written, checked against what
    /// is already logged on those days. Unlike words, a photo that cannot be read now is not kept: it can be taken again.
    /// </summary>
    /// <param name="image">A JPEG, already scaled down on the phone.</param>
    public async Task<QuickLogPrepared> ExecuteImageAsync(byte[] image, DateTimeOffset capturedAt, string language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (household, baby) = await ShownAsync(households, cancellationToken);
        if (household is null || baby is null)
            return new QuickLogPrepared(null, false, "ai.no-baby", null);

        var request = new AiImageRequest(
            household.Id.Value, baby.Id.Value, Convert.ToBase64String(image), "image/jpeg", capturedAt, household.TimeZoneId, language, baby.BirthDate);
        AiParseResponse? response;
        try
        {
            response = await api.ParseImageAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response = null;
        }

        if (response is null)
            return new QuickLogPrepared(null, false, "ai.unavailable", null);
        if (response.ErrorCode is { } refused)
            return new QuickLogPrepared(null, false, refused, response.Limit);

        var from = response.Proposals.Count == 0 ? capturedAt : response.Proposals.Min(p => p.At);
        var existing = (await store.ListAsync(household.Id, from.AddDays(-1), capturedAt.AddHours(1), cancellationToken))
            .OfType<LiveEntry>().Select(l => l.Entry).Where(e => !e.IsDeleted).ToList();
        var items = response.Proposals.Select(p => Item(p, household.Id, baby.Id, response.PromptVersion, existing, capturedAt)).ToList();
        return new QuickLogPrepared(new QuickLogReview("", capturedAt, items, response.Unparsed, response.PromptVersion), false, null, null);
    }

    internal static async Task<(HouseholdSettings? Household, BabyProfile? Baby)> ShownAsync(IHouseholdStore households, CancellationToken cancellationToken)
    {
        var household = (await households.ListHouseholdsAsync(cancellationToken)).FirstOrDefault(h => !h.IsDeleted);
        var baby = household is null ? null : (await households.ListBabiesAsync(household.Id, cancellationToken)).FirstOrDefault(b => !b.IsDeleted);
        return (household, baby);
    }

    /// <summary>What is running now and the last side, in the contract's words (ADR 0005 §4).</summary>
    public static AiParseContext Context(TodaySummary summary, HouseholdSettings household)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(household);
        var running = summary.Running.Select(e => e.Details switch
        {
            BreastFeed f when f.Segments[^1].End is null => f.LastSide == BreastSide.Left ? AiRunningKinds.BreastLeft : AiRunningKinds.BreastRight,
            BreastFeed => AiRunningKinds.BreastPaused,
            SleepDetails => AiRunningKinds.Sleep,
            PumpDetails => AiRunningKinds.Pump,
            _ => null,
        }).OfType<string>().ToList();
        var lastSide = summary.LastFeed?.Details is BreastFeed last ? (last.LastSide == BreastSide.Left ? "left" : "right") : null;
        return new AiParseContext(running, lastSide, household.VolumeUnit == VolumeUnit.Ounces ? "oz" : "ml");
    }

    private static QuickLogItem Item(
        AiProposal proposal, HouseholdId household, BabyId baby, string promptVersion, IReadOnlyCollection<CareEntry> existing, DateTimeOffset now)
    {
        if (QuickLogEntries.NewEntry(proposal) is not { } shape)
            return new QuickLogItem(proposal, null, null, null);

        var subject = shape.Details is PumpDetails ? EntrySubject.Caregiver(PersonRef.ThisDevicesPerson) : EntrySubject.Baby(baby);
        var candidate = CareEntry.Create(
            new EntryId(Guid.CreateVersion7(now)), household, subject, shape.Details, shape.Timing,
            PersonRef.ThisDevicesPerson, EntrySource.AiParsed(promptVersion), new Coo.Domain.Common.Versioning.HlcStamp(0, 0, default), now);
        if (candidate.IsFailure)
            return new QuickLogItem(proposal, null, null, candidate.Error!.Code);

        var duplicate = DuplicateDetector.Find([candidate.Value], existing, new HashSet<EntryPair>(), now)
            .Select(p => p.First.Id == candidate.Value.Id ? p.Second : p.First)
            .FirstOrDefault();
        return new QuickLogItem(proposal, candidate.Value, duplicate, null);
    }
}

/// <summary>
/// Saves what the person kept in the review through the ordinary write path (ADR 0005 §1): new entries with
/// <c>source = AiParsed</c>, starts and stops as revisions of what is running — exactly what the buttons do.
/// </summary>
public sealed class ApplyQuickLog(IHouseholdStore households, GetToday today, LogEntry log, ReviseEntry revise, IPendingNoteStore notes, TimeProvider time)
{
    /// <returns>One result per item, in order: null when it was done, or an error code.</returns>
    public async Task<IReadOnlyList<string?>> ExecuteAsync(QuickLogReview review, IReadOnlyList<QuickLogItem> selected, Guid? pendingNote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(selected);
        var (household, baby) = await PrepareQuickLog.ShownAsync(households, cancellationToken);
        if (household is null || baby is null)
            return [.. selected.Select(_ => (string?)"ai.no-baby")];

        var zone = TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
        var source = EntrySource.AiParsed(review.PromptVersion);
        var results = new List<string?>();
        foreach (var item in selected)
        {
            // Again for every item: "switched sides, then done" must see the switch before it stops.
            var summary = await today.ExecuteAsync(household.Id, baby.Id, zone, time.GetUtcNow(), cancellationToken);
            results.Add(await ApplyAsync(item, household.Id, baby.Id, summary, source, cancellationToken));
        }

        if (pendingNote is { } note)
            await notes.RemoveAsync(note, cancellationToken);
        return results;
    }

    private async Task<string?> ApplyAsync(QuickLogItem item, HouseholdId household, BabyId baby, TodaySummary summary, EntrySource source, CancellationToken cancellationToken)
    {
        var p = item.Proposal;
        if (item.Candidate is { } candidate)
        {
            var logged = await log.ExecuteAsync(
                new LogEntryCommand(household, candidate.Subject, candidate.Details, candidate.Timing, PersonRef.ThisDevicesPerson, source), cancellationToken);
            return logged.IsFailure ? logged.Error!.Code : null;
        }

        CareEntry? Running<T>() where T : EntryDetails => summary.Running.FirstOrDefault(e => e.Details is T);

        switch (p.Action)
        {
            case AiActions.BreastStart:
                var side = p.Side == "right" ? BreastSide.Right : BreastSide.Left;
                if (Running<BreastFeed>() is { } paused)
                    return await ReviseAsync(paused.Id, (e, v, n) => e.Revise<BreastFeed>(d => d.Resume(side, p.At), v, n), cancellationToken);
                var started = await log.ExecuteAsync(
                    new LogEntryCommand(household, EntrySubject.Baby(baby), BreastFeed.Start(side, p.At), null, PersonRef.ThisDevicesPerson, source), cancellationToken);
                return started.IsFailure ? started.Error!.Code : null;
            case AiActions.BreastSwitch:
                return Running<BreastFeed>() is { } feed
                    ? await ReviseAsync(feed.Id, (e, v, n) => e.Revise<BreastFeed>(d => d.SwitchSide(p.At), v, n), cancellationToken)
                    : AiWarnings.NothingRunning;
            case AiActions.BreastStop:
                return Running<BreastFeed>() is { } feeding
                    ? await ReviseAsync(feeding.Id, (e, v, n) => e.Revise<BreastFeed>(d => d.Complete(p.At), v, n), cancellationToken)
                    : AiWarnings.NothingRunning;
            case AiActions.SleepStart:
                var slept = await log.ExecuteAsync(
                    new LogEntryCommand(household, EntrySubject.Baby(baby), new SleepDetails(), EntryTiming.Session(TimeRange.Open(p.At)), PersonRef.ThisDevicesPerson, source),
                    cancellationToken);
                return slept.IsFailure ? slept.Error!.Code : null;
            case AiActions.SleepEnd:
                return Running<SleepDetails>() is { } sleep ? await EndAsync(sleep, p.At, null, cancellationToken) : AiWarnings.NothingRunning;
            case AiActions.PumpStart:
                var pumped = await log.ExecuteAsync(
                    new LogEntryCommand(household, EntrySubject.Caregiver(PersonRef.ThisDevicesPerson), new PumpDetails(), EntryTiming.Session(TimeRange.Open(p.At)), PersonRef.ThisDevicesPerson, source),
                    cancellationToken);
                return pumped.IsFailure ? pumped.Error!.Code : null;
            case AiActions.PumpStop:
                return Running<PumpDetails>() is { } pump ? await EndAsync(pump, p.At, p.Millilitres, cancellationToken) : AiWarnings.NothingRunning;
            default:
                return "ai.unknown-action";
        }
    }

    private async Task<string?> EndAsync(CareEntry running, DateTimeOffset at, decimal? millilitres, CancellationToken cancellationToken)
    {
        if (millilitres is { } ml && Volume.FromMillilitres(ml) is { IsSuccess: true } volume)
        {
            var yield = await ReviseAsync(running.Id, (e, v, n) => e.Revise<PumpDetails>(d => d.WithYield(PumpYield.OfTotal(volume.Value)), v, n), cancellationToken);
            if (yield is not null)
                return yield;
        }

        return await ReviseAsync(running.Id, (e, v, n) =>
        {
            var range = TimeRange.Create(((SessionTiming)e.Timing).Range.Start, at);
            return range.IsFailure ? range : e.Retime(EntryTiming.Session(range.Value), v, n);
        }, cancellationToken);
    }

    private async Task<string?> ReviseAsync(EntryId id, Func<CareEntry, Coo.Domain.Common.Versioning.HlcStamp, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        var revised = await revise.ExecuteAsync(id, change, cancellationToken);
        return revised.IsFailure ? revised.Error!.Code : null;
    }
}

/// <summary>The entry a log-type proposal would save, or null for starts and stops (they change what is running).</summary>
public static class QuickLogEntries
{
    /// <summary>A pump logged without an end lasted this long: the session needs one, and the review says so.</summary>
    public static readonly TimeSpan AssumedPumpLength = TimeSpan.FromMinutes(15);

    public static (EntryDetails Details, EntryTiming? Timing)? NewEntry(AiProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var p = proposal;
        switch (p.Action)
        {
            case AiActions.BottleLog when p.Millilitres is { } ml && Volume.FromMillilitres(ml) is { IsSuccess: true } volume
                                       && BottleFeed.Of(p.Milk == "formula" ? Milk.Formula : Milk.ExpressedBreastMilk, volume.Value) is { IsSuccess: true } bottle:
                return (bottle.Value, EntryTiming.Point(p.At));
            case AiActions.DiaperLog:
                var contents = p.Contents switch { "dirty" => DiaperContents.Dirty, "mixed" => DiaperContents.Mixed, _ => DiaperContents.Wet };
                return (new DiaperDetails(contents), EntryTiming.Point(p.At));
            case AiActions.BreastLog when BreastFeed.FromSideDurations(p.At, Sides(p)) is { IsSuccess: true } breast:
                return (breast.Value, null);
            case AiActions.SleepLog when p.EndAt is { } end && TimeRange.Create(p.At, end) is { IsSuccess: true } slept:
                return (new SleepDetails(), EntryTiming.Session(slept.Value));
            case AiActions.PumpLog when TimeRange.Create(p.At, p.EndAt ?? p.At + AssumedPumpLength) is { IsSuccess: true } range:
                var yield = p.Millilitres is { } amount && Volume.FromMillilitres(amount) is { IsSuccess: true } pumped ? PumpYield.OfTotal(pumped.Value) : null;
                return (new PumpDetails(yield), EntryTiming.Session(range.Value));
            case AiActions.MedicineLog when MedicineDetails.Create(p.Name, p.Dose) is { IsSuccess: true } medicine:
                return (medicine.Value, EntryTiming.Point(p.At));
            case AiActions.TemperatureLog when p.Celsius is { } celsius && TemperatureDetails.Create(celsius) is { IsSuccess: true } reading:
                return (reading.Value, EntryTiming.Point(p.At));
            case AiActions.GrowthLog when GrowthDetails.Create(p.WeightGrams, p.LengthMm, p.HeadMm) is { IsSuccess: true } growth:
                return (growth.Value, EntryTiming.Point(p.At));
            case AiActions.VaccineLog when VaccineDetails.Create(p.Name) is { IsSuccess: true } vaccine:
                return (vaccine.Value, EntryTiming.Point(p.At));
            case AiActions.VisitLog when VisitDetails.Create(p.Name) is { IsSuccess: true } visit:
                return (visit.Value, EntryTiming.Point(p.At));
            default:
                return null;
        }
    }

    private static IEnumerable<(BreastSide Side, int Minutes)> Sides(AiProposal p)
    {
        if (p.LeftMinutes is > 0 and var left)
            yield return (BreastSide.Left, left);
        if (p.RightMinutes is > 0 and var right)
            yield return (BreastSide.Right, right);
    }
}
