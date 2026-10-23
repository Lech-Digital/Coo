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
    TimeProvider time,
    ISpeechLanguagePreference speechLanguage) : ObservableObject, IDisposable
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

    private IReadOnlyList<string> _babyNames = [];

    public bool CanListen => speech.IsAvailable;

    /// <summary>The language Tell Coo listens in: the app's, unless this parent speaks another to it.</summary>
    [ObservableProperty]
    private string _speechLanguage = Strings.Language;

    public static IReadOnlyList<string> SpeechLanguages => Strings.Supported;

    [RelayCommand]
    private async Task SetSpeechLanguage(string language)
    {
        if (!Strings.Supported.Contains(language))
            return;
        SpeechLanguage = language;
        await speechLanguage.SetAsync(language == Strings.Language ? null : language, CancellationToken.None);
    }

    /// <summary>The words to expect, in the language spoken: the baby's names, then the care words.</summary>
    public IReadOnlyList<string> Hints()
    {
        using var scope = Strings.InLanguage(SpeechLanguage);
        return [.. _babyNames, .. Strings.Get("Quick.SpeechHints").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>The example under the title, with the family's own baby in it rather than a made-up name.</summary>
    [ObservableProperty]
    private string _hint = Strings.Format("Quick.Hint", Strings.Get("Quick.Baby"));

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
        Hint = Strings.Format("Quick.Hint", await BabyNameAsync() ?? Strings.Get("Quick.Baby"));
        SpeechLanguage = await speechLanguage.GetAsync(CancellationToken.None) is { } chosen && Strings.Supported.Contains(chosen) ? chosen : Strings.Language;
    }

    private async Task<string?> BabyNameAsync()
    {
        var household = (await households.ListHouseholdsAsync(CancellationToken.None)).FirstOrDefault(h => !h.IsDeleted);
        if (household is null)
            return null;
        var baby = (await households.ListBabiesAsync(household.Id, CancellationToken.None)).FirstOrDefault(b => !b.IsDeleted);
        _babyNames = baby is null ? [] : [baby.Name, .. baby.Nicknames];
        return string.IsNullOrWhiteSpace(baby?.Name) ? null : baby.Name;
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
        Text = "";
        var heard = await speech.ListenAsync(SpeechLanguage, words => Text = words, _listening.Token, Hints());
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

    /// <summary>Let go, or a tap while listening: what was heard so far goes to Coo.</summary>
    [RelayCommand]
    private void FinishListening()
    {
        if (Stage == QuickLogStage.Listening)
            speech.Finish();
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

    /// <summary>
    /// A photo of a health record (a growth card, a vaccination booklet): what Coo read on it, to review like words.
    /// </summary>
    /// <param name="image">A JPEG, scaled down by the page.</param>
    public async Task ReadPhotoAsync(byte[] image)
    {
        _listening?.Cancel();
        (_review, _pendingNote) = (null, null);
        (Stage, Text, Message, Rows, Unparsed) = (QuickLogStage.Thinking, "", "", [], "");
        var prepared = await prepare.ExecuteImageAsync(image, time.GetUtcNow(), Strings.Language, CancellationToken.None);
        await ShowAsync(prepared, null, nothingFound: "Quick.PhotoNothing", unavailable: "Quick.PhotoUnavailable");
    }

    private async Task UnderstandAsync(string text, DateTimeOffset capturedAt, Guid? pendingNote)
    {
        Stage = QuickLogStage.Thinking;
        Message = "";
        var prepared = await prepare.ExecuteAsync(text, capturedAt, Strings.Language, CancellationToken.None);
        await ShowAsync(prepared, pendingNote, nothingFound: "Quick.NothingFound", unavailable: "Quick.Error.unavailable");
    }

    private async Task ShowAsync(QuickLogPrepared prepared, Guid? pendingNote, string nothingFound, string unavailable)
    {
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
                _ => Strings.Get(unavailable),
            };
            return;
        }

        (_review, _pendingNote) = (review, pendingNote);
        var formats = await FormatsAsync();
        var zone = await ZoneAsync();
        Rows = [.. review.Items.Select(i => Row(i, formats, zone, review.CapturedAt, isPhoto: review.Text.Length == 0))];
        Unparsed = review.Unparsed.Count == 0 ? "" : Strings.Format("Quick.Unparsed", string.Join(" · ", review.Unparsed));
        Message = Rows.Count == 0 ? Strings.Get(nothingFound) : "";
        Stage = Rows.Count == 0 ? QuickLogStage.Input : QuickLogStage.Review;
    }

    /// <param name="isPhoto">From a record on paper: each row is dated (the day is what was written, not the hour).</param>
    private static QuickLogRow Row(QuickLogItem item, Formats formats, TimeZoneInfo zone, DateTimeOffset capturedAt, bool isPhoto)
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
        var when = isPhoto ? TimeZoneInfo.ConvertTime(p.At, zone).ToString("d MMM yyyy", Strings.Culture)
            : Math.Abs((p.At - capturedAt).TotalMinutes) < 1 ? Strings.Get("Quick.Now")
            : formats.Time(p.At, zone);
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
