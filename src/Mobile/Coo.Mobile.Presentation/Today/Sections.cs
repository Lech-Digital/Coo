using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Domain.Insights;
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

    [ObservableProperty]
    private IReadOnlyList<RunningAction> _actions = actions;

    /// <summary>The language its button labels were made in; another language means a new item (Spec 009 §3.5).</summary>
    public string Language { get; } = Strings.Language;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _digits = "";

    [ObservableProperty]
    private bool _isBreastFeed;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private BreastSide? _activeSide;

    [ObservableProperty]
    private string _leftDuration = "";

    [ObservableProperty]
    private string _rightDuration = "";
}

public sealed record TileItem(string KindCode, string Name, string LastAt, IRelayCommand Command);

public sealed record TimelineItem(EntryId? Id, string Time, string KindCode, string Title, string Who, bool IsMidnight = false);

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
        if (entry.Details is BreastFeed f)
        {
            var isOpen = f.Segments[^1].IsOpen;
            item.IsBreastFeed = true;
            item.IsPaused = !isOpen;
            item.ActiveSide = isOpen ? f.LastSide : null;

            var left = f.Segments.Where(seg => seg.Side == BreastSide.Left)
                .Aggregate(TimeSpan.Zero, (t, seg) => t + ((seg.End ?? s.Now) - seg.Start));
            var right = f.Segments.Where(seg => seg.Side == BreastSide.Right)
                .Aggregate(TimeSpan.Zero, (t, seg) => t + ((seg.End ?? s.Now) - seg.Start));

            item.LeftDuration = Formats.Clock(left);
            item.RightDuration = Formats.Clock(right);

            item.Title = Strings.Get(isOpen ? (f.LastSide == BreastSide.Left ? "Hero.FeedingLeft" : "Hero.FeedingRight") : "Hero.FeedingPaused");
            item.Digits = Formats.Clock(left + right);

            var nextButtons = MakeBreastButtons(entry.Id, f, actions);
            if (Running.Changed(item.Actions, nextButtons, a => a.Label + a.IsPrimary))
                item.Actions = nextButtons;
        }
        else
        {
            item.IsBreastFeed = false;
            (item.Title, item.Digits) = entry.Details switch
            {
                SleepDetails => (Strings.Get("Hero.Sleeping"), Formats.Clock(s.Now - entry.Timing.OccurredAt)),
                Coo.Domain.CareLog.Kinds.Development.TummyDetails => (Strings.Get("Hero.Tummy"), Formats.Clock(s.Now - entry.Timing.OccurredAt)),
                _ => (Strings.Get("Hero.Pumping"), Formats.Clock(s.Now - entry.Timing.OccurredAt)),
            };
        }

        return item;
    }

    private static IReadOnlyList<RunningAction> MakeBreastButtons(EntryId id, BreastFeed f, HomeActions actions)
    {
        var isOpen = f.Segments[^1].IsOpen;
        if (isOpen)
        {
            return
            [
                new(Strings.Get("Action.Pause") ?? "Pause", false, new AsyncRelayCommand(() => actions.PauseBreastAsync(id))),
                new(Strings.Get("Action.SwitchSide") ?? "Switch side", false, new AsyncRelayCommand(() => actions.SwitchSideAsync(id))),
                new(Strings.Get("Action.Stop") ?? "Stop", true, new AsyncRelayCommand(() => actions.StopBreastAsync(id))),
            ];
        }

        var otherSide = f.LastSide == BreastSide.Left ? BreastSide.Right : BreastSide.Left;
        return
        [
            new(Strings.Get("Action.Resume") ?? "Resume", true, new AsyncRelayCommand(() => actions.ResumeBreastAsync(id, f.LastSide))),
            new(Strings.Get("Action.ResumeOther") ?? "Switch & resume", false, new AsyncRelayCommand(() => actions.ResumeBreastAsync(id, otherSide))),
            new(Strings.Get("Action.Stop") ?? "Stop", false, new AsyncRelayCommand(() => actions.StopBreastAsync(id))),
        ];
    }

    private static RunningItem Create(CareEntry entry, HomeSnapshot s, HomeActions actions)
    {
        var started = Strings.Format("Hero.Started", s.Formats.Time(entry.Timing.OccurredAt));
        IReadOnlyList<RunningAction> buttons = entry.Details switch
        {
            BreastFeed f => MakeBreastButtons(entry.Id, f, actions),
            SleepDetails => [new(Strings.Get("Action.WokeUp") ?? "Woke up", true, new AsyncRelayCommand(() => actions.EndSessionAsync(entry.Id)))],
            Coo.Domain.CareLog.Kinds.Development.TummyDetails => [new(Strings.Get("Action.Stop") ?? "Stop", true, new AsyncRelayCommand(() => actions.EndSessionAsync(entry.Id)))],
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

    [ObservableProperty]
    private string? _avatarRef;

    /// <summary>Twins or siblings: the chip opens the picker.</summary>
    [ObservableProperty]
    private bool _hasSiblings;

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => palette == PaletteKind.Day || stage == HomeStage.Expecting;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BabyName = snapshot.Baby.Name;
        Initial = snapshot.Baby.Name.Length > 0 ? snapshot.Baby.Name[..1].ToUpper(Strings.Culture) : "";
        Age = snapshot.Baby.IsBorn ? Formats.Age(snapshot.Baby, DateOnly.FromDateTime(snapshot.Now.LocalDateTime)) : Strings.Get("Expecting.Eyebrow");
        IsPremium = snapshot.IsPremium;
        AvatarRef = snapshot.AvatarRef;
        HasSiblings = snapshot.Siblings is { Count: > 0 };
    }
}

/// <summary>Twins and siblings: the family's other babies at a glance, each a tap away (day and night).</summary>
public sealed partial class SiblingsSection : ObservableObject, IHomeSection
{
    public int Order => 5;

    [ObservableProperty]
    private IReadOnlyList<SiblingGlance> _siblings = [];

    /// <summary>"Log for both" is on: a diaper, bottle or sleep goes to every born baby.</summary>
    [ObservableProperty]
    private bool _logForAll;

    /// <summary>"Log for both", or "Log for all" with three or more; empty when no other baby is born or this one is not.</summary>
    [ObservableProperty]
    private string _logForAllLabel = "";

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => true;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var next = snapshot.Siblings ?? [];
        if (!next.SequenceEqual(Siblings))
            Siblings = next;
        LogForAll = snapshot.LogForAll;
        var born = snapshot.Baby.IsBorn && !snapshot.ViewOnly ? next.Count(g => g.IsBorn) : 0;
        LogForAllLabel = born switch
        {
            0 => "",
            1 => Strings.Get("Sibling.LogBoth"),
            _ => Strings.Get("Sibling.LogAll"),
        };
    }

    /// <summary>"asleep 40 m · fed 1 h 20 m ago", "feeding", or "no entries today" for a sibling's day.</summary>
    public static string Line(TodaySummary? summary, DateTimeOffset now)
    {
        if (summary is null)
            return Strings.Get("Expecting.Eyebrow");
        var parts = new List<string>();
        if (summary.Running.FirstOrDefault(e => e.Details is SleepDetails) is { } sleep)
            parts.Add(Strings.Format("Sibling.Asleep", Formats.Duration(now - sleep.Timing.OccurredAt)));
        else if (summary.AwakeSince is { } awake)
            parts.Add(Strings.Format("Sibling.Awake", Formats.Duration(now - awake)));
        if (summary.Running.Any(e => e.Details is FeedingDetails))
            parts.Add(Strings.Get("Sibling.Feeding"));
        else if (summary.LastFeed is { } feed && now >= feed.Timing.OccurredAt)
            parts.Add(Strings.Format("Sibling.Fed", Formats.Duration(now - feed.Timing.OccurredAt)));
        return parts.Count > 0 ? string.Join(" · ", parts) : Strings.Get("Sibling.Nothing");
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
public sealed partial class RunningSection(HomeActions actions) : ObservableObject, IHomeSection, ILoggingSection
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
public sealed partial class LogTilesSection(HomeActions actions) : ObservableObject, IHomeSection, ILoggingSection
{
    public int Order => 10;

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

/// <summary>
/// Forward-looking card: next breast side, time since the last feed, and the next sleep (ADR 0008 §7). Free: the
/// age-typical wake window. Premium: the personal window with its explanation one tap away, or, at low confidence, the
/// typical window and a word on why there is no personal one yet. Never a precise time the predictor does not believe.
/// </summary>
public sealed partial class NextUpSection : ObservableObject, IHomeSection
{
    public int Order => 35;

    [ObservableProperty]
    private string _nextSide = "";

    [ObservableProperty]
    private string _sinceFeed = "";

    [ObservableProperty]
    private string _nextNap = "";

    [ObservableProperty]
    private string _napWindow = "";

    /// <summary>The personal window is shown: <see cref="NapWhy"/> explains it.</summary>
    [ObservableProperty]
    private bool _napIsPersonal;

    /// <summary>Without Premium: <see cref="NapWindow"/> points to it.</summary>
    [ObservableProperty]
    private bool _napUpsell;

    [ObservableProperty]
    private string _napWhyTitle = "";

    [ObservableProperty]
    private IReadOnlyList<string> _napWhy = [];

    /// <summary>"Sleep plan · day 3 · nap 2 at 13:15" while a plan runs (Spec 019 §2.3); a tap opens it.</summary>
    [ObservableProperty]
    private string _planLine = "";

    [ObservableProperty]
    private bool _hasContent;

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born && palette == PaletteKind.Day;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var t = snapshot.Today;
        PlanLine = SleepPlan.SleepPlanViewModel.TodayLine(snapshot.Plan, TimeOnly.FromDateTime(snapshot.Now.DateTime), snapshot.Formats) ?? "";

        // 1. Next breast side
        if (t?.LastFeed is { Details: BreastFeed bf })
        {
            var nextBreast = bf.LastSide == BreastSide.Left ? BreastSide.Right : BreastSide.Left;
            NextSide = Strings.Format("Today.NextSide", Formats.Side(nextBreast, shortForm: false));
        }
        else
        {
            NextSide = "";
        }

        // 2. Elapsed time since last feed
        if (t?.LastFeed is { } feed)
        {
            var elapsed = snapshot.Now - feed.Timing.OccurredAt;
            // Under a minute "0 m since last feed" read like a fault: the feed was just logged.
            if (elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(1))
                SinceFeed = Strings.Get("Today.FedJustNow");
            else if (elapsed >= TimeSpan.Zero)
                SinceFeed = Strings.Format("Today.SinceFeed", Formats.Duration(elapsed));
            else
                SinceFeed = "";
        }
        else
        {
            SinceFeed = "";
        }

        // 3. The next sleep (ADR 0008 §7)
        NextNap = NapWindow = "";
        NapIsPersonal = NapUpsell = false;
        var asleep = t?.Running.Any(e => e.Details is SleepDetails) == true;
        if (snapshot.Baby.IsBorn && t?.AwakeSince is not null && !asleep)
        {
            var today = DateOnly.FromDateTime(snapshot.Now.DateTime);
            var age = Formats.Age(snapshot.Baby, today);
            var band = WakeWindowPrior.For(BabyAge.On(snapshot.Baby, today).CorrectedDays);
            var typical = Strings.Format("Today.NapTypical", age, Range(band));
            var sleep = snapshot.Sleep;
            // Free (owner, 2026-11-03): computed on the phone, it costs nothing to run.
            if (sleep is { IsPersonal: true })
            {
                var due = snapshot.Now > sleep.Latest;
                NextNap = (sleep.IsBedtime, due) switch
                {
                    (true, true) => Strings.Get("Today.BedDue"),
                    (true, false) => Strings.Format("Today.BedLikely", snapshot.Formats.Time(sleep.Earliest), snapshot.Formats.Time(sleep.Latest)),
                    (false, true) => Strings.Get("Today.NapDue"),
                    _ => Strings.Format("Today.NapLikely", snapshot.Formats.Time(sleep.Earliest), snapshot.Formats.Time(sleep.Latest)),
                };
                NapWindow = Strings.Get("Today.NapWhyLink");
                NapIsPersonal = true;
                NapWhyTitle = Strings.Get(sleep.IsBedtime ? "Why.TitleBed" : "Why.TitleNap");
                NapWhy = Explain(sleep, snapshot, typical);
            }
            else
            {
                // "Log a few more days" only while her own windows are too few; a gap in the log (the window's place in
                // the day unknown) shows the typical window alone.
                NextNap = typical;
                NapWindow = sleep is { IsPersonal: false } ? Strings.Get("Today.NapLearning") : "";
            }
        }

        HasContent = !string.IsNullOrEmpty(NextSide) || !string.IsNullOrEmpty(SinceFeed) || !string.IsNullOrEmpty(NextNap) || !string.IsNullOrEmpty(PlanLine);
    }

    /// <summary>"2–3 h" or "60–90 min": the age-typical wake window.</summary>
    public static string Range(WakeWindowBand band)
    {
        ArgumentNullException.ThrowIfNull(band);
        if (band.High < TimeSpan.FromHours(2))
            return Strings.Format("Today.RangeMinutes", (int)band.Low.TotalMinutes, (int)band.High.TotalMinutes);
        var culture = Strings.Culture;
        return Strings.Format("Today.RangeHours", band.Low.TotalHours.ToString("0.#", culture), band.High.TotalHours.ToString("0.#", culture));
    }

    /// <summary>Why this window (ADR 0008 §8): the baby's own windows, the typical ones, a short nap, and that it is not advice.</summary>
    public static IReadOnlyList<string> Explain(SleepPrediction sleep, HomeSnapshot snapshot, string typical)
    {
        ArgumentNullException.ThrowIfNull(sleep);
        ArgumentNullException.ThrowIfNull(snapshot);
        var lines = new List<string>();
        if (sleep.PersonalMedian is { } median)
        {
            lines.Add(sleep.IsBedtime
                ? Strings.Format("Why.PersonalBed", snapshot.Baby.Name, Formats.Duration(median), sleep.DaysUsed)
                : Strings.Format("Why.PersonalNap", snapshot.Baby.Name, Formats.Duration(median), sleep.DaysUsed, sleep.Position));
        }

        lines.Add(typical + ".");
        if (sleep.ShortNapAdjusted)
            lines.Add(Strings.Get("Why.ShortNap"));
        lines.Add(Strings.Format("Why.Awake", snapshot.Formats.Time(sleep.AwakeSince)));
        // Proving the predictor (owner, 2026-11-03): how the shown windows did for her lately, from her own log.
        if (snapshot.SleepRecord is { IsEnough: true } record)
            lines.Add(Strings.Format("Why.Record", snapshot.Baby.Name, record.Hits, record.Shown));
        lines.Add(Strings.Get("Why.Disclaimer"));
        return lines;
    }
}

/// <summary>Daily progress card: Feeds, Diapers, and Sleep totals vs age ranges.</summary>
public sealed partial class DailyProgressSection : ObservableObject, IHomeSection
{
    public int Order => 45;

    [ObservableProperty]
    private string _feedsProgress = "";

    [ObservableProperty]
    private string _diapersProgress = "";

    [ObservableProperty]
    private string _sleepProgress = "";

    [ObservableProperty]
    private double _feedsRatio;

    [ObservableProperty]
    private double _diapersRatio;

    [ObservableProperty]
    private double _sleepRatio;

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born && palette == PaletteKind.Day;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entries = snapshot.Today?.TodayEntries ?? [];

        // Count totals
        var feedsCount = 0;
        var diapersCount = 0;
        var sleepSpan = TimeSpan.Zero;

        foreach (var stored in entries)
        {
            if (stored.KindCode == "feeding")
            {
                feedsCount++;
            }
            else if (stored.KindCode == "diaper")
            {
                diapersCount++;
            }
            else if (stored.KindCode == "sleep")
            {
                if (stored is LiveEntry live && live.Entry.Timing is SessionTiming { Range: var r })
                    sleepSpan += r.Duration(snapshot.Now);
                else if (stored is ReadOnlyEntry ro && ro.EndedAt.HasValue)
                    sleepSpan += ro.EndedAt.Value - ro.StartedAt;
            }
        }

        // If a session is currently in progress, include it in totals
        if (snapshot.Today?.Running != null)
        {
            foreach (var running in snapshot.Today.Running)
            {
                if (running.KindCode.Value == "feeding" && !entries.Any(e => e.Id == running.Id))
                {
                    feedsCount++;
                }
                else if (running.KindCode.Value == "sleep" && running.Timing is SessionTiming { Range: var r })
                {
                    if (!entries.Any(e => e.Id == running.Id))
                    {
                        sleepSpan += r.Duration(snapshot.Now);
                    }
                }
            }
        }

        var daysOld = snapshot.Baby.IsBorn && snapshot.Baby.BirthDate is { } b
            ? Math.Max(0, DateOnly.FromDateTime(snapshot.Now.LocalDateTime).DayNumber - b.DayNumber)
            : 30;

        var (targetFeeds, targetDiapers, targetSleepHours) = GetTargets(daysOld);

        var nextFeeds = Strings.Format("Today.ProgressFeeds", feedsCount, targetFeeds);
        var nextFeedsRatio = Math.Clamp(Math.Round(feedsCount / (double)targetFeeds, 3), 0.0, 1.0);
        if (FeedsProgress != nextFeeds)
            FeedsProgress = nextFeeds;
        if (Math.Abs(FeedsRatio - nextFeedsRatio) >= 0.001)
            FeedsRatio = nextFeedsRatio;

        var nextDiapers = Strings.Format("Today.ProgressDiapers", diapersCount, targetDiapers);
        var nextDiapersRatio = Math.Clamp(Math.Round(diapersCount / (double)targetDiapers, 3), 0.0, 1.0);
        if (DiapersProgress != nextDiapers)
            DiapersProgress = nextDiapers;
        if (Math.Abs(DiapersRatio - nextDiapersRatio) >= 0.001)
            DiapersRatio = nextDiapersRatio;

        // The same midnight-cut total as the Log (owner's finding: Today said 7 h 58 m while Log said 8 h 14 m).
        if (snapshot.Today?.SleepSoFar is { } soFar)
            sleepSpan = soFar;
        var sleepMinutes = (int)Math.Floor(sleepSpan.TotalMinutes);
        var sleepHoursFormatted = Formats.Duration(TimeSpan.FromMinutes(sleepMinutes));
        var nextSleep = Strings.Format("Today.ProgressSleep", sleepHoursFormatted, Formats.Duration(TimeSpan.FromHours(targetSleepHours)));
        var nextSleepRatio = Math.Clamp(Math.Round(sleepMinutes / (targetSleepHours * 60.0), 3), 0.0, 1.0);
        if (SleepProgress != nextSleep)
            SleepProgress = nextSleep;
        if (Math.Abs(SleepRatio - nextSleepRatio) >= 0.005)
            SleepRatio = nextSleepRatio;
    }

    internal static (int Feeds, int Diapers, double SleepHours) GetTargets(int daysOld) => daysOld switch
    {
        < 14 => (8, 6, 16.0),
        < 45 => (7, 6, 15.0),
        < 90 => (6, 5, 14.5),
        < 150 => (5, 5, 14.0),
        < 240 => (5, 4, 13.5),
        < 365 => (4, 4, 13.0),
        _ => (3, 4, 12.0)
    };
}

/// <summary>Tip of the day from Advisor knowledge base for the baby's exact age.</summary>
public sealed partial class AdvisorTipSection : ObservableObject, IHomeSection
{
    public int Order => 55;

    [ObservableProperty]
    private string _stageBadge = "";

    [ObservableProperty]
    private string _tipText = "";

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born && palette == PaletteKind.Day;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var daysOld = snapshot.Baby.IsBorn && snapshot.Baby.BirthDate is { } b
            ? Math.Max(0, DateOnly.FromDateTime(snapshot.Now.LocalDateTime).DayNumber - b.DayNumber)
            : 14;

        var info = Coo.Mobile.Presentation.Advisor.AdvisorKnowledgeBase.GetStage(daysOld);
        StageBadge = info.StageBadge;
        TipText = info.PediatricTip;
    }
}

/// <summary>Compact recent activity (up to 3 entries) with "See full history in Log →".</summary>
public sealed partial class RecentActivitySection : ObservableObject, IHomeSection
{
    public int Order => 65;

    [ObservableProperty]
    private IReadOnlyList<TimelineItem> _items = [];

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => stage == HomeStage.Born && palette == PaletteKind.Day;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyList<TimelineItem> next = [.. (snapshot.Today?.TodayEntries ?? []).Take(3).Select(e => TimelineSection.Row(e, snapshot))];
        if (Running.Changed(Items, next, i => i.Time + i.Title + i.Who))
            Items = next;
    }
}

/// <summary>Today's entries, newest first (Spec 006 §4.5). Replaced on Today by dashboard cards; kept for tests and fallback.</summary>
public sealed partial class TimelineSection : ObservableObject, IHomeSection
{
    public int Order => 999;

    [ObservableProperty]
    private IReadOnlyList<TimelineItem> _items = [];

    // Removed from active Today rendering in favour of NextUp, DailyProgress, AdvisorTip, RecentActivity
    public bool AppliesTo(HomeStage stage, PaletteKind palette) => false;

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
public sealed partial class NightSection(HomeActions actions) : ObservableObject, IHomeSection, ILoggingSection
{
    public int Order => 20;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _babyName = "";

    [ObservableProperty]
    private string _initial = "";

    [ObservableProperty]
    private string _age = "";

    [ObservableProperty]
    private bool _isPremium;

    [ObservableProperty]
    private string? _avatarRef;

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
        Initial = s.Baby.Name.Length > 0 ? s.Baby.Name[..1].ToUpper(Strings.Culture) : "";
        Age = s.Baby.IsBorn ? Formats.Age(s.Baby, DateOnly.FromDateTime(s.Now.LocalDateTime)) : Strings.Get("Expecting.Eyebrow");
        IsPremium = s.IsPremium;
        AvatarRef = s.AvatarRef;
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
        var runningPump = t?.Running.FirstOrDefault(e => e.Details is PumpDetails);
        IReadOnlyList<FactItem> facts =
        [
            new("feeding", Strings.Get("Night.LastFeed"), feed),
            new("diaper", Strings.Get("Night.LastDiaper"), Running.LastDiaper(t?.LastDiaper, s)),
            sleeping is not null
                ? new("sleep", Strings.Get("Night.AsleepSince"), s.Formats.Time(sleeping.Timing.OccurredAt))
                : new("sleep", Strings.Get("Night.AwakeSince"), t?.AwakeSince is { } a ? s.Formats.Time(a) : Strings.Get("Tile.Never")),
            new("pump", Strings.Get("Kind.Pump"), t?.LastPump is { Timing.IsInProgress: true } ? Strings.Get("Tile.Running") : t?.LastPump is { } p ? s.Formats.Time(p.Timing.OccurredAt) : Strings.Get("Tile.Never")),
        ];

        facts = [.. facts.Where(f => KindVisibility.IsShown(s.Household, f.KindCode))];
        if (Running.Changed(Facts, facts, f => f.Label + f.Value))
            Facts = facts;

        IReadOnlyList<QuickItem> quick =
        [
            new("diaper", Strings.Get("Diaper.Wet"), new AsyncRelayCommand(() => actions.LogDiaperAsync(DiaperContents.Wet))),
            new("diaper", Strings.Get("Diaper.Dirty"), new AsyncRelayCommand(() => actions.LogDiaperAsync(DiaperContents.Dirty))),
            new("feeding", Strings.Get("Night.FeedL"), new AsyncRelayCommand(() => actions.StartBreastAsync(BreastSide.Left))),
            new("feeding", Strings.Get("Night.FeedR"), new AsyncRelayCommand(() => actions.StartBreastAsync(BreastSide.Right))),
            new("feeding", Strings.Get("Feed.Bottle"), new AsyncRelayCommand(() =>
            {
                actions.RequestSheet(HomeSheet.Bottle);
                return Task.CompletedTask;
            })),
            runningPump is not null
                ? new("pump", Strings.Get("Action.Stop") ?? "Stop", new AsyncRelayCommand(() => { actions.RequestPumpYield(runningPump.Id); return Task.CompletedTask; }))
                : new("pump", Strings.Get("Kind.Pump"), new AsyncRelayCommand(actions.StartPumpAsync)),
            sleeping is not null
                ? new("sleep", Strings.Get("Action.WokeUp"), new AsyncRelayCommand(() => actions.EndSessionAsync(sleeping.Id)))
                : new("sleep", Strings.Get("Kind.Sleep"), new AsyncRelayCommand(actions.StartSleepAsync)),
        ];
        quick = [.. quick.Where(q => KindVisibility.IsShown(s.Household, q.KindCode))];
        if (Running.Changed(Quick, quick, q => q.Label))
            Quick = quick;
    }
}

/// <summary>For a Viewer (a grandparent following along): whose day this is, and that parents and caregivers add to it.</summary>
public sealed partial class ViewOnlySection : ObservableObject, IHomeSection
{
    public int Order => 8;

    [ObservableProperty]
    private string _text = "";

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => true;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Text = snapshot.ViewOnly ? Strings.Format("Today.ViewOnly", snapshot.Baby.Name) : "";
    }
}
