using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Today;

public sealed record RunningAction(string Label, bool IsPrimary, IAsyncRelayCommand Command);

/// <summary>
/// One running timer. The same instance lives as long as the session runs, and only <see cref="Title"/> and
/// <see cref="Digits"/> change each second, so the view updates in place and taps on its buttons are never lost.
/// <see cref="Started"/> changes when the start is moved back (Spec 007 §2).
/// </summary>
public sealed partial class RunningItem(EntryId id, string kindCode, string started, IReadOnlyList<RunningAction> actions) : ObservableObject
{
    public EntryId Id { get; } = id;

    public string KindCode { get; } = kindCode;

    [ObservableProperty]
    private string _started = started;

    public IReadOnlyList<RunningAction> Actions { get; } = actions;

    /// <summary>The language its button labels were made in; another language means a new item (Spec 009 §3.5).</summary>
    public string Language { get; } = Strings.Language;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _digits = "";
}

public sealed record TileItem(string KindCode, string Name, string LastAt, IRelayCommand Command);

public sealed record TimelineItem(EntryId Id, string Time, string KindCode, string Title, string Who);

public sealed record FactItem(string KindCode, string Label, string Value);

public sealed record QuickItem(string KindCode, string Label, IAsyncRelayCommand Command);

/// <summary>Shared helpers for the born-stage sections.</summary>
internal static class Running
{
    /// <summary>Reuses the item for a session that is still running, so its view and buttons stay put.</summary>
    public static RunningItem Item(CareEntry entry, HomeSnapshot s, HomeActions actions, RunningItem? existing)
    {
        var item = existing is not null && existing.Id == entry.Id && existing.Language == Strings.Language ? existing : Create(entry, s, actions);
        item.Started = Strings.Format("Hero.Started", s.Formats.Time(entry.Timing.OccurredAt));
        (item.Title, item.Digits) = entry.Details switch
        {
            BreastFeed f => (
                Strings.Get(f.Segments[^1].IsOpen ? (f.LastSide == BreastSide.Left ? "Hero.FeedingLeft" : "Hero.FeedingRight") : "Hero.FeedingPaused"),
                Formats.Clock(f.Segments.Aggregate(TimeSpan.Zero, (t, seg) => t + ((seg.End ?? s.Now) - seg.Start)))),
            SleepDetails => (Strings.Get("Hero.Sleeping"), Formats.Clock(s.Now - entry.Timing.OccurredAt)),
            _ => (Strings.Get("Hero.Pumping"), Formats.Clock(s.Now - entry.Timing.OccurredAt)),
        };
        return item;
    }

    private static RunningItem Create(CareEntry entry, HomeSnapshot s, HomeActions actions)
    {
        var started = Strings.Format("Hero.Started", s.Formats.Time(entry.Timing.OccurredAt));
        IReadOnlyList<RunningAction> buttons = entry.Details switch
        {
            BreastFeed f =>
            [
                new(Strings.Get("Action.SwitchSide") ?? "Switch side", false, new AsyncRelayCommand(() => actions.SwitchSideAsync(entry.Id))),
                new(Strings.Get("Action.Stop") ?? "Stop", true, new AsyncRelayCommand(() => actions.StopBreastAsync(entry.Id))),
            ],
            SleepDetails => [new(Strings.Get("Action.WokeUp") ?? "Woke up", true, new AsyncRelayCommand(() => actions.EndSessionAsync(entry.Id)))],
            _ => [new(Strings.Get("Action.Stop") ?? "Stop", true, new AsyncRelayCommand(() => { actions.RequestPumpYield(entry.Id); return Task.CompletedTask; }))],
        };
        return new RunningItem(entry.Id, entry.KindCode.Value, started, buttons);
    }

    /// <summary>Replaces a list only when what it shows changed, so unchanged views are not rebuilt every second.</summary>
    public static bool Changed<T>(IReadOnlyList<T> current, IReadOnlyList<T> next, Func<T, string> display) =>
        current.Count != next.Count || !current.Select(display).SequenceEqual(next.Select(display));

    public static string LastFeed(CareEntry? feed, HomeSnapshot s) => feed switch
    {
        null => Strings.Get("Tile.Never"),
        { Timing.IsInProgress: true } => Strings.Get("Tile.Running"),
        { Details: BreastFeed f } => $"{s.Formats.Time(feed.Timing.OccurredAt)} · {Formats.Side(f.LastSide, true)}",
        { Details: BottleFeed b } => $"{s.Formats.Time(feed.Timing.OccurredAt)} · {s.Formats.Volume(b.Total)}",
        _ => s.Formats.Time(feed.Timing.OccurredAt),
    };

    public static string LastDiaper(CareEntry? diaper, HomeSnapshot s) => diaper is { Details: DiaperDetails d }
        ? $"{s.Formats.Time(diaper.Timing.OccurredAt)} · {Formats.Contents(d.Contents).ToLower(Strings.Culture)}"
        : Strings.Get("Tile.Never");

    public static string LastSleep(CareEntry? sleep, HomeSnapshot s) => sleep?.Timing switch
    {
        null => Strings.Get("Tile.Never"),
        SessionTiming { Range.End: { } woke } => Strings.Format("Tile.Woke", s.Formats.Time(woke)),
        _ => Strings.Format("Tile.Asleep", s.Formats.Time(sleep.Timing.OccurredAt)),
    };
}

/// <summary>The top bar: the wordmark and the baby chip (day palette).</summary>
public sealed partial class BabyHeaderSection : ObservableObject, IHomeSection
{
    public int Order => 0;

    [ObservableProperty]
    private string _babyName = "";

    [ObservableProperty]
    private string _initial = "";

    [ObservableProperty]
    private string _age = "";

    [ObservableProperty]
    private bool _isPremium;

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => palette == PaletteKind.Day || stage == HomeStage.Expecting;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BabyName = snapshot.Baby.Name;
        Initial = snapshot.Baby.Name[..1].ToUpper(Strings.Culture);
        Age = snapshot.Baby.IsBorn ? Formats.Age(snapshot.Baby, DateOnly.FromDateTime(snapshot.Now.LocalDateTime)) : Strings.Get("Expecting.Eyebrow");
        IsPremium = snapshot.IsPremium;
    }
}

/// <summary>The expecting stage's first section (Spec 006 §3a): the due-date countdown and "The baby has arrived".</summary>
public sealed partial class DueDateSection(HomeActions actions) : ObservableObject, IHomeSection
{
    public int Order => 10;

    [ObservableProperty]
    private string _headline = "";

    [ObservableProperty]
    private string _dateText = "";

    public IRelayCommand ArrivedCommand { get; } = new RelayCommand(() => actions.RequestRecordBirth());

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Expecting;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var due = snapshot.Baby.DueDate ?? DateOnly.FromDateTime(snapshot.Now.LocalDateTime);
        var days = due.DayNumber - DateOnly.FromDateTime(snapshot.Now.LocalDateTime).DayNumber;
        Headline = days switch
        {
            0 => Strings.Get("Due.Today"),
            < 0 => Strings.Plural("Due.PastDays", -days),
            < 14 => Strings.Plural("Due.InDays", days),
            _ => Strings.Plural("Due.InWeeks", (int)Math.Round(days / 7.0)),
        };
        DateText = due.ToString("d MMMM", Strings.Culture);
    }
}

/// <summary>"Awake for 1 h 20 m", or what is running (Spec 006 §4.2).</summary>
public sealed partial class StatusHeroSection : ObservableObject, IHomeSection
{
    public int Order => 20;

    [ObservableProperty]
    private string _eyebrow = "";

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private string _context = "";

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born && palette == PaletteKind.Day;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var today = snapshot.Today;
        if (today?.Running.FirstOrDefault(e => e.Details is SleepDetails) is { } sleeping)
        {
            (Eyebrow, Value, Context) = (Strings.Get("Hero.Sleeping"), Formats.Duration(snapshot.Now - sleeping.Timing.OccurredAt),
                Strings.Format("Hero.Since", snapshot.Formats.Time(sleeping.Timing.OccurredAt)));
        }
        else if (today?.AwakeSince is { } awake)
        {
            var nap = today.LastSleep is { Timing: SessionTiming { Range: var r } } ? Formats.Duration(r.Duration(snapshot.Now)) : null;
            (Eyebrow, Value, Context) = (Strings.Get("Hero.AwakeFor"), Formats.Duration(snapshot.Now - awake),
                nap is null ? Strings.Format("Hero.Since", snapshot.Formats.Time(awake)) : Strings.Format("Hero.SinceAfterNap", snapshot.Formats.Time(awake), nap));
        }
        else
        {
            (Eyebrow, Value, Context) = ("", Strings.Get("Hero.NoSleep"), "");
        }
    }
}

/// <summary>One card per running timer (Spec 006 §4.3).</summary>
public sealed partial class RunningSection(HomeActions actions) : ObservableObject, IHomeSection
{
    public int Order => 30;

    [ObservableProperty]
    private IReadOnlyList<RunningItem> _items = [];

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born && palette == PaletteKind.Day;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var next = (snapshot.Today?.Running ?? []).Select(e => Running.Item(e, snapshot, actions, Items.FirstOrDefault(i => i.Id == e.Id))).ToList();
        if (Running.Changed(Items, next, i => i.Id + i.Language))
            Items = next;
    }
}

/// <summary>Feed, Diaper, Sleep and Pump, with their "last at" lines (Spec 006 §4.4).</summary>
public sealed partial class LogTilesSection(HomeActions actions) : ObservableObject, IHomeSection
{
    public int Order => 40;

    [ObservableProperty]
    private IReadOnlyList<TileItem> _tiles = [];

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born && palette == PaletteKind.Day;

    private Task Pump(EntryId id)
    {
        actions.RequestPumpYield(id);
        return Task.CompletedTask;
    }

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var t = snapshot.Today;
        IReadOnlyList<TileItem> all =
        [
            new("feeding", Strings.Get("Kind.Feed"), Running.LastFeed(t?.LastFeed, snapshot), new RelayCommand(() => actions.RequestSheet(HomeSheet.Feed))),
            new("diaper", Strings.Get("Kind.Diaper"), Running.LastDiaper(t?.LastDiaper, snapshot), new RelayCommand(() => actions.RequestSheet(HomeSheet.Diaper))),
            new("sleep", Strings.Get("Kind.Sleep"), Running.LastSleep(t?.LastSleep, snapshot), new AsyncRelayCommand(() => t?.Running.FirstOrDefault(e => e.Details is SleepDetails) is { } s
                ? actions.EndSessionAsync(s.Id) : actions.StartSleepAsync())),
            new("pump", Strings.Get("Kind.Pump"),
                t?.LastPump is { Timing.IsInProgress: true } ? Strings.Get("Tile.Running") : t?.LastPump is { } p ? snapshot.Formats.Time(p.Timing.OccurredAt) : Strings.Get("Tile.Never"),
                new AsyncRelayCommand(() => t?.Running.FirstOrDefault(e => e.Details is PumpDetails) is { } run
                    ? Pump(run.Id) : actions.StartPumpAsync())),
        ];
        IReadOnlyList<TileItem> next = [.. all.Where(tile => KindVisibility.IsShown(snapshot.Household, tile.KindCode))];
        if (Running.Changed(Tiles, next, i => i.Name + i.LastAt))
            Tiles = next;
    }
}

/// <summary>Today's entries, newest first (Spec 006 §4.5).</summary>
public sealed partial class TimelineSection : ObservableObject, IHomeSection
{
    public int Order => 50;

    [ObservableProperty]
    private IReadOnlyList<TimelineItem> _items = [];

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born && palette == PaletteKind.Day;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyList<TimelineItem> next = [.. (snapshot.Today?.TodayEntries ?? []).Select(e => Row(e, snapshot))];

        // Who too (walk fix F4): the family's names may arrive after the first draw.
        if (Running.Changed(Items, next, i => i.Time + i.Title + i.Who))
            Items = next;
    }

    internal static TimelineItem Row(StoredEntry e, HomeSnapshot s) =>
        new(e.Id, s.Formats.Time(e.Start), e.KindCode, s.Formats.Title(e, s.Now), (s.Who ?? WhoLabels.SignedOut).For(e));
}

/// <summary>"2 need attention" (Spec 007 §6): a quiet line on Today, shown only when there is something.</summary>
public sealed partial class AttentionSection : ObservableObject, IHomeSection
{
    public int Order => 25;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string _text = "";

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Count = snapshot.AttentionCount;
        Text = Count == 0 ? "" : Strings.Plural("Attention.Badge", Count);
    }
}

/// <summary>
/// Today at night: the 03:12 layout (Spec 006 §6). Big timer, three facts, the quick row: diapers, then feeds (left,
/// right, bottle: Spec 009 §3.8), then sleep.
/// </summary>
public sealed partial class NightSection(HomeActions actions) : ObservableObject, IHomeSection
{
    public int Order => 20;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _babyName = "";

    /// <summary>
    /// Every running timer, active care first: during a dream feed both the feed and the sleep run, and the feed's
    /// Switch side and Stop must be reachable at night (Spec 006 §5a, found on the phone).
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<RunningItem> _timers = [];

    [ObservableProperty]
    private IReadOnlyList<FactItem> _facts = [];

    [ObservableProperty]
    private IReadOnlyList<QuickItem> _quick = [];

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born && palette == PaletteKind.Night;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var s = snapshot;
        var t = s.Today;
        Title = Strings.Format("Night.Title", s.Formats.Time(s.Now));
        BabyName = s.Baby.Name;
        var running = (t?.Running ?? []).OrderBy(e => e.Details is SleepDetails ? 1 : 0).ThenBy(e => e.Timing.OccurredAt);
        var timers = running.Select(e => Running.Item(e, s, actions, Timers.FirstOrDefault(i => i.Id == e.Id))).ToList();
        if (Running.Changed(Timers, timers, i => i.Id + i.Language))
            Timers = timers;

        var feed = t?.LastFeed is { } f ? f.Details switch
        {
            BreastFeed b when !f.Timing.IsInProgress => $"{s.Formats.Time(f.Timing.OccurredAt)} · {Formats.BreastSummary(b)}",
            _ => Running.LastFeed(f, s),
        } : Strings.Get("Tile.Never");
        var sleeping = t?.Running.FirstOrDefault(e => e.Details is SleepDetails);
        IReadOnlyList<FactItem> facts =
        [
            new("feeding", Strings.Get("Night.LastFeed"), feed),
            new("diaper", Strings.Get("Night.LastDiaper"), Running.LastDiaper(t?.LastDiaper, s)),
            sleeping is not null
                ? new("sleep", Strings.Get("Night.AsleepSince"), s.Formats.Time(sleeping.Timing.OccurredAt))
                : new("sleep", Strings.Get("Night.AwakeSince"), t?.AwakeSince is { } a ? s.Formats.Time(a) : Strings.Get("Tile.Never")),
        ];

        facts = [.. facts.Where(f => KindVisibility.IsShown(s.Household, f.KindCode))];
        if (Running.Changed(Facts, facts, f => f.Label + f.Value))
            Facts = facts;

        IReadOnlyList<QuickItem> quick =
        [
            new("diaper", Strings.Get("Diaper.Wet"), new AsyncRelayCommand(() => actions.LogDiaperAsync(DiaperContents.Wet))),
            new("diaper", Strings.Get("Diaper.Dirty"), new AsyncRelayCommand(() => actions.LogDiaperAsync(DiaperContents.Dirty))),
            new("diaper", Strings.Get("Diaper.Mixed"), new AsyncRelayCommand(() => actions.LogDiaperAsync(DiaperContents.Mixed))),
            new("feeding", Strings.Get("Night.FeedL"), new AsyncRelayCommand(() => actions.StartBreastAsync(BreastSide.Left))),
            new("feeding", Strings.Get("Night.FeedR"), new AsyncRelayCommand(() => actions.StartBreastAsync(BreastSide.Right))),
            new("feeding", Strings.Get("Feed.Bottle"), new AsyncRelayCommand(() =>
            {
                actions.RequestSheet(HomeSheet.Bottle);
                return Task.CompletedTask;
            })),
            sleeping is not null
                ? new("sleep", Strings.Get("Action.WokeUp"), new AsyncRelayCommand(() => actions.EndSessionAsync(sleeping.Id)))
                : new("sleep", Strings.Get("Kind.Sleep"), new AsyncRelayCommand(actions.StartSleepAsync)),
        ];
        quick = [.. quick.Where(q => KindVisibility.IsShown(s.Household, q.KindCode))];
        if (Running.Changed(Quick, quick, q => q.Label))
            Quick = quick;
    }
}
