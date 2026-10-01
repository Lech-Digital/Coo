using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.DebugScreen;

/// <summary>
/// Increment 4's debug screen (Spec 004 §8): unstyled, and compiled only into Debug builds or an explicit
/// <c>-p:CooDebugScreen=true</c> build. Deleted when the Today screen arrives.
/// </summary>
public sealed class DebugPage : ContentPage
{
    private readonly LogEntry _log;
    private readonly ReviseEntry _revise;
    private readonly DeleteEntry _delete;
    private readonly GetEntries _entries;
    private readonly ICareLogStore _store;
    private readonly IHouseholdStore _households;
    private readonly IVersionClock _clock;
    private readonly IStorageDiagnostics _diagnostics;
    private readonly TimeProvider _time;

    private readonly Label _status = new() { FontSize = 13 };
    private readonly Label _counters = new() { FontSize = 13 };
    private readonly VerticalStackLayout _list = new() { Spacing = 6 };
    private HouseholdId _household;
    private BabyId _baby;

    public DebugPage(
        LogEntry log, ReviseEntry revise, DeleteEntry delete, GetEntries entries, ICareLogStore store,
        IHouseholdStore households, IVersionClock clock, IStorageDiagnostics diagnostics, TimeProvider time, IServiceProvider services)
    {
        (_log, _revise, _delete, _entries, _store, _households, _clock, _diagnostics, _time) =
            (log, revise, delete, entries, store, households, clock, diagnostics, time);

        Title = "Coo debug";
        BackgroundColor = Colors.White;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 48, 16, 32),
                Spacing = 8,
                Children =
                {
                    new Label { Text = "Coo · debug screen", FontSize = 22 },
                    _status,
                    _counters,
                    Row(Button("Diaper wet", () => LogPoint(new DiaperDetails(DiaperContents.Wet))),
                        Button("Diaper dirty", () => LogPoint(new DiaperDetails(DiaperContents.Dirty)))),
                    Row(Button("Bottle 120 ml", () => LogPoint(BottleFeed.Of(Milk.Formula, Volume.FromMillilitres(120).Value).Value)),
                        Button("Pump start", () => LogSession(new PumpDetails(), EntrySubject.Caregiver(PersonRef.ThisDevicesPerson)))),
                    Row(Button("Sleep start", () => LogSession(new SleepDetails(), EntrySubject.Baby(_baby))),
                        Button("Sleep end", EndLatestSession<SleepDetails>)),
                    Row(Button("Breast L start", StartBreast),
                        Button("Switch side", () => ReviseLatestBreast((d, now) => d.SwitchSide(now))),
                        Button("Complete", () => ReviseLatestBreast((d, now) => d.Complete(now)))),
                    Row(Button("Corrupt one row", async () =>
                        {
                            await _diagnostics.InsertMalformedRowAsync(_household, _baby, CancellationToken.None);
                            Report(Result.Success);
                        }),
                        Button("Foreign row", async () =>
                        {
                            await _diagnostics.InsertForeignKindRowAsync(_household, _baby, CancellationToken.None);
                            Report(Result.Success);
                        })),
                    Row(Button("Style gallery", () => Navigation.PushModalAsync(services.GetRequiredService<GalleryPage>()))),
                    new Label { Text = "Last 20 entries", FontSize = 17, Margin = new Thickness(0, 12, 0, 0) },
                    _list,
                },
            },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var household = (await _households.ListHouseholdsAsync(CancellationToken.None)).First(h => !h.IsDeleted);
        _household = household.Id;

        var babies = await _households.ListBabiesAsync(_household, CancellationToken.None);
        if (babies.Count == 0)
        {
            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            var baby = BabyProfile.Create(
                new BabyId(Guid.CreateVersion7(_time.GetUtcNow())), _household, "Test baby", [], today.AddDays(-60), null,
                await _clock.NextAsync(CancellationToken.None), today).Value;
            await _households.SaveBabyAsync(baby, CancellationToken.None);
            babies = [baby];
        }

        _baby = babies[0].Id;
        await RefreshAsync();
    }

    private async Task LogPoint(EntryDetails details) =>
        Report(await _log.ExecuteAsync(
            new LogEntryCommand(_household, EntrySubject.Baby(_baby), details, EntryTiming.Point(_time.GetLocalNow()),
                PersonRef.ThisDevicesPerson, EntrySource.Manual),
            CancellationToken.None));

    private async Task LogSession(EntryDetails details, EntrySubject subject) =>
        Report(await _log.ExecuteAsync(
            new LogEntryCommand(_household, subject, details, EntryTiming.Session(TimeRange.Open(_time.GetLocalNow())),
                PersonRef.ThisDevicesPerson, EntrySource.Timer),
            CancellationToken.None));

    private async Task StartBreast() =>
        Report(await _log.ExecuteAsync(
            new LogEntryCommand(_household, EntrySubject.Baby(_baby), BreastFeed.Start(BreastSide.Left, _time.GetLocalNow()), null,
                PersonRef.ThisDevicesPerson, EntrySource.Timer),
            CancellationToken.None));

    private async Task EndLatestSession<TDetails>()
        where TDetails : EntryDetails
    {
        if (await LatestOpenAsync<TDetails>() is not { } entry)
        {
            _status.Text = "Nothing running.";
            return;
        }

        Report(await _revise.ExecuteAsync(entry.Id, (e, version, now) =>
        {
            var start = ((SessionTiming)e.Timing).Range.Start;
            var range = TimeRange.Create(start, _time.GetLocalNow());
            return range.IsSuccess ? e.Retime(EntryTiming.Session(range.Value), version, now) : range;
        }, CancellationToken.None));
    }

    private async Task ReviseLatestBreast(Func<BreastFeed, DateTimeOffset, Result<BreastFeed>> change)
    {
        if (await LatestOpenAsync<BreastFeed>() is not { } entry)
        {
            _status.Text = "No breast feed running.";
            return;
        }

        Report(await _revise.ExecuteAsync(
            entry.Id, (e, version, now) => e.Revise<BreastFeed>(d => change(d, _time.GetLocalNow()), version, now), CancellationToken.None));
    }

    private async Task<CareEntry?> LatestOpenAsync<TDetails>()
        where TDetails : EntryDetails =>
        (await RecentAsync()).OfType<LiveEntry>().Select(l => l.Entry)
            .FirstOrDefault(e => e.Details is TDetails && e.Timing.IsInProgress);

    private Task<IReadOnlyList<StoredEntry>> RecentAsync()
    {
        var now = _time.GetUtcNow();
        return _entries.ExecuteAsync(_household, now.AddDays(-7), now.AddDays(1), CancellationToken.None);
    }

    private void Report(Result result)
    {
        _status.Text = result.IsSuccess ? "Saved." : $"Refused: {result.Error}";
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // Read the entries first: reading classifies new rows, so the counters below are current.
        var recent = await RecentAsync();
        var counts = await _store.CountsAsync(_household, CancellationToken.None);
        _counters.Text = $"Outbox {counts.PendingOutbox} · read-only {counts.ReadOnly} · quarantined {counts.Quarantined}" +
                        $"\n{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString}) · {_diagnostics.DatabasePath}";

        _list.Children.Clear();
        foreach (var stored in recent.Take(20))
        {
            var id = stored.Id;
            _list.Children.Add(Row(
                new Label { Text = Describe(stored), FontSize = 13, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Fill },
                Button("Delete", async () => Report(await _delete.ExecuteAsync(id, CancellationToken.None)))));
        }
    }

    private static string Describe(StoredEntry stored)
    {
        var time = stored.Start.ToLocalTime().ToString("ddd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        return stored switch
        {
            LiveEntry { Entry: var e } => $"{time}  {e.KindCode}  {Summary(e)}",
            ReadOnlyEntry r => $"{time}  {r.KindCode}  READ-ONLY ({r.Reason})",
            QuarantinedEntry q => $"{time}  {q.KindCode}  QUARANTINED ({q.Reason})",
            _ => time,
        };
    }

    private static string Summary(CareEntry e) => e.Details switch
    {
        DiaperDetails d => d.Contents.ToString(),
        BottleFeed b => $"bottle {b.Total}",
        BreastFeed f => $"breast L {f.LeftDuration:mm\\:ss} R {f.RightDuration:mm\\:ss}{(e.Timing.IsInProgress ? " running" : "")}",
        SleepDetails => e.Timing is SessionTiming { Range.End: { } end } s ? $"slept {end - s.Range.Start:h\\:mm}" : "sleeping",
        PumpDetails => e.Timing.IsInProgress ? "pumping" : "pumped",
        _ => "",
    };

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, FontSize = 13, Padding = new Thickness(8, 4) };
        button.Clicked += async (_, _) => await action();
        return button;
    }

    private static HorizontalStackLayout Row(params View[] views)
    {
        var row = new HorizontalStackLayout { Spacing = 6 };
        foreach (var view in views)
            row.Children.Add(view);
        return row;
    }
}
