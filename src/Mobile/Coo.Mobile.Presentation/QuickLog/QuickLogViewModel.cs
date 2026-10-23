using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Contracts.AiParsing;
using Coo.Mobile.Application.AiLogging;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Today;

namespace Coo.Mobile.Presentation.QuickLog;

public enum QuickLogStage
{
    /// <summary>Waiting for words: the mic, a text box, and any pending notes.</summary>
    Input,
    Listening,
    Thinking,

    /// <summary>The proposals, to keep or drop, before anything is saved (ADR 0005 §1).</summary>
    Review,
}

/// <summary>One proposal as shown: what it does, when, what to check, and whether it will be saved.</summary>
public sealed partial class QuickLogRow(QuickLogItem item, string title, string when, string? note, bool isEnabled) : ObservableObject
{
    public QuickLogItem Item { get; } = item;

    public string Title { get; } = title;

    public string When { get; } = when;

    /// <summary>A warning or "already logged", to read before saving; null when there is nothing to say.</summary>
    public string? Note { get; } = note;

    /// <summary>False when the domain refused it: shown, but it cannot be saved.</summary>
    public bool IsEnabled { get; } = isEnabled;

    [ObservableProperty]
    private bool _isSelected = isEnabled && item.IsSelectedByDefault;
}

/// <summary>
/// "Tell Coo" (ADR 0005): say or type what happened, see what Coo understood, keep what is right, save. Starts and stops
/// act on what is running, like the buttons; everything else becomes an ordinary entry marked as AI-parsed.
/// </summary>
public sealed partial class QuickLogViewModel(
    PrepareQuickLog prepare,
    ApplyQuickLog apply,
    ISpeechToText speech,
    IPendingNoteStore notes,
    IHouseholdStore households,
    IClockFormat clock,
    UndoService undo,
    TimeProvider time) : ObservableObject, IDisposable
{
    private QuickLogReview? _review;
    private Guid? _pendingNote;
    private CancellationTokenSource? _listening;

    [ObservableProperty]
    private QuickLogStage _stage = QuickLogStage.Input;

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private IReadOnlyList<QuickLogRow> _rows = [];

    [ObservableProperty]
    private string _unparsed = "";

    [ObservableProperty]
    private IReadOnlyList<PendingNote> _pendingNotes = [];

    public bool CanListen => speech.IsAvailable;

    /// <summary>Raised when the sheet should close: saved, or cancelled.</summary>
    public event EventHandler? Finished;

    public void Dispose()
    {
        _listening?.Cancel();
        _listening?.Dispose();
    }

    public async Task OpenAsync()
    {
        _listening?.Cancel();
        (_review, _pendingNote) = (null, null);
        (Stage, Text, Message, Rows, Unparsed) = (QuickLogStage.Input, "", "", [], "");
        PendingNotes = await notes.ListAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task Listen()
    {
        if (Stage is QuickLogStage.Listening or QuickLogStage.Thinking)
            return;
        Message = "";
        Stage = QuickLogStage.Listening;
        _listening?.Dispose();
        _listening = new CancellationTokenSource();
        var heard = await speech.ListenAsync(Strings.Language, _listening.Token);
        if (Stage != QuickLogStage.Listening)
            return;
        if (string.IsNullOrWhiteSpace(heard))
        {
            Stage = QuickLogStage.Input;
            Message = Strings.Get("Quick.NotHeard");
            return;
        }

        Text = heard;
        await UnderstandAsync(heard, time.GetUtcNow(), null);
    }

    [RelayCommand]
    private void StopListening()
    {
        _listening?.Cancel();
        if (Stage == QuickLogStage.Listening)
            Stage = QuickLogStage.Input;
    }

    [RelayCommand]
    private Task Send() => string.IsNullOrWhiteSpace(Text) ? Task.CompletedTask : UnderstandAsync(Text, time.GetUtcNow(), null);

    /// <summary>A note kept while offline, parsed now against the moment it was said (ADR 0005 §3).</summary>
    [RelayCommand]
    private Task ReviewNote(PendingNote note)
    {
        Text = note.Text;
        return UnderstandAsync(note.Text, note.CapturedAt, note.Id);
    }

    [RelayCommand]
    private async Task DropNote(PendingNote note)
    {
        await notes.RemoveAsync(note.Id, CancellationToken.None);
        PendingNotes = await notes.ListAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_review is null)
            return;
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
        if (selected.Count == 0)
        {
            await CancelAsync();
            return;
        }

        Stage = QuickLogStage.Thinking;
        var results = await apply.ExecuteAsync(_review, selected, _pendingNote, CancellationToken.None);
        undo.ExternalChange();
        var failed = results.Count(r => r is not null);
        if (failed == 0)
        {
            undo.Say(Strings.Plural("Quick.Saved", selected.Count));
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        Stage = QuickLogStage.Review;
        Message = results.FirstOrDefault(r => r is not null) == AiWarnings.NothingRunning
            ? Strings.Get("Quick.Warn.ai.nothing-running")
            : Strings.Get("Quick.SomeFailed");
    }

    [RelayCommand]
    private async Task Cancel() => await CancelAsync();

    private Task CancelAsync()
    {
        _listening?.Cancel();
        Finished?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private async Task UnderstandAsync(string text, DateTimeOffset capturedAt, Guid? pendingNote)
    {
        Stage = QuickLogStage.Thinking;
        Message = "";
        var prepared = await prepare.ExecuteAsync(text, capturedAt, Strings.Language, CancellationToken.None);
        if (prepared.KeptAsPendingNote)
        {
            // A note re-parsed and still not understood stays once, not twice.
            if (pendingNote is { } old)
                await notes.RemoveAsync(old, CancellationToken.None);
            PendingNotes = await notes.ListAsync(CancellationToken.None);
            Stage = QuickLogStage.Input;
            Text = "";
            Message = Strings.Get("Quick.KeptForLater");
            return;
        }

        if (prepared.Review is not { } review)
        {
            Stage = QuickLogStage.Input;
            Message = prepared.ErrorCode switch
            {
                "ai.free-limit" => Strings.Format("Quick.Error.free-limit", prepared.Limit ?? 5),
                "ai.daily-limit" => Strings.Format("Quick.Error.daily-limit", prepared.Limit ?? 100),
                "ai.signed-out" => Strings.Get("Quick.Error.signed-out"),
                _ => Strings.Get("Quick.Error.unavailable"),
            };
            return;
        }

        (_review, _pendingNote) = (review, pendingNote);
        var formats = await FormatsAsync();
        var zone = await ZoneAsync();
        Rows = [.. review.Items.Select(i => Row(i, formats, zone, review.CapturedAt))];
        Unparsed = review.Unparsed.Count == 0 ? "" : Strings.Format("Quick.Unparsed", string.Join(" · ", review.Unparsed));
        Message = Rows.Count == 0 ? Strings.Get("Quick.NothingFound") : "";
        Stage = Rows.Count == 0 ? QuickLogStage.Input : QuickLogStage.Review;
    }

    private static QuickLogRow Row(QuickLogItem item, Formats formats, TimeZoneInfo zone, DateTimeOffset capturedAt)
    {
        var p = item.Proposal;
        var title = item.Candidate is { } entry ? formats.Title(entry, capturedAt) : p.Action switch
        {
            AiActions.BreastStart => Strings.Get(p.Side == "right" ? "Hero.FeedingRight" : "Hero.FeedingLeft"),
            AiActions.BreastSwitch => Strings.Get("Action.SwitchSide"),
            AiActions.BreastStop => Strings.Get("Quick.Action.BreastStop"),
            AiActions.SleepStart => Strings.Get("Quick.Action.SleepStart"),
            AiActions.SleepEnd => Strings.Get("Action.WokeUp"),
            AiActions.PumpStart => Strings.Get("Hero.Pumping"),
            AiActions.PumpStop => Strings.Get("Quick.Action.PumpStop"),
            _ => p.SourceText,
        };
        var when = Math.Abs((p.At - capturedAt).TotalMinutes) < 1 ? Strings.Get("Quick.Now") : formats.Time(p.At, zone);
        if (p.EndAt is { } end)
            when = $"{when} – {formats.Time(end, zone)}";

        string? note = null;
        if (item.Error is { } error)
            note = Strings.Error(error);
        else if (item.Duplicate is { } duplicate)
            note = Strings.Format("Quick.AlreadyLogged", formats.Time(duplicate.Timing.OccurredAt, zone));
        else if (p.Warnings.Count > 0)
            note = string.Join(" · ", p.Warnings.Select(w => Strings.Get("Quick.Warn." + w)));
        return new QuickLogRow(item, title, when, note, item.Error is null);
    }

    private async Task<Formats> FormatsAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        return new Formats(clock.Is24Hour, household?.VolumeUnit ?? Coo.Domain.Households.VolumeUnit.Millilitres);
    }

    private async Task<TimeZoneInfo> ZoneAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        return household is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(household.TimeZoneId);
    }
}
