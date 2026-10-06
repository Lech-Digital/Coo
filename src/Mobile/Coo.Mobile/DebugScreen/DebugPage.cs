using Coo.Domain.CareLog;
using Coo.Domain.CareLog.Kinds.Diaper;
using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Domain.CareLog.Kinds.Pump;
using Coo.Domain.CareLog.Kinds.Sleep;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;

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
    private readonly IAccountApi _accountApi;
    private readonly ISessionStore _sessions;
    private readonly Account _account;
    private readonly TimeProvider _time;

    private readonly Label _status = new() { FontSize = 13 };
    private readonly Label _counters = new() { FontSize = 13 };
    private readonly Label _accountLine = new() { FontSize = 13 };
    private readonly Label _syncLine = new() { FontSize = 13 };
    private readonly SyncEngine _sync;
    private readonly IServiceProvider _services;
    private readonly Label _familiesLine = new() { FontSize = 13 };

    // For the process lifetime: this transient page is rebuilt on every visit, and the token must outlive a sign-out
    // (Spec 010 §10.6: Keep token, sign out in Settings, reopen this page, then try the token).
    private static string? _keptToken;
    private readonly VerticalStackLayout _list = new() { Spacing = 6 };
    private HouseholdId _household;
    private BabyId _baby;

    public DebugPage(
        LogEntry log, ReviseEntry revise, DeleteEntry delete, GetEntries entries, ICareLogStore store,
        IHouseholdStore households, IVersionClock clock, IStorageDiagnostics diagnostics, IAccountApi accountApi,
        ISessionStore sessions, Account account, TimeProvider time, SyncEngine sync, IServiceProvider services)
    {
        (_log, _revise, _delete, _entries, _store, _households, _clock, _diagnostics, _accountApi, _sessions, _account, _time) =
            (log, revise, delete, entries, store, households, clock, diagnostics, accountApi, sessions, account, time);
        _sync = sync;
        _services = services;

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
                    new Label { Text = "Account (Spec 010 §8)", FontSize = 17, Margin = new Thickness(0, 12, 0, 0) },
                    _accountLine,
                    Row(Button("Account (/api/me)", ShowServerAccountAsync),
                        Button("Keep token", KeepTokenAsync),
                        Button("Old token → /api/me", TryOldTokenAsync)),
                    new Label { Text = "Sync (Spec 011 §11)", FontSize = 17, Margin = new Thickness(0, 12, 0, 0) },
                    _syncLine,
                    Row(Button("Sync now", async () =>
                        {
                            await _sync.SyncNowAsync(CancellationToken.None);
                            await ShowSyncAsync();
                        }),
                        Button("Fresh bootstrap", async () =>
                        {
                            // Only with an empty outbox and no rejections (Spec 011 §11): clears this household here and pulls it from 0.
                            _status.Text = await _sync.FreshBootstrapAsync(CancellationToken.None)
                                ? "Fresh bootstrap: done"
                                : "Fresh bootstrap refused: the outbox or the rejected list is not empty (or not signed in)";
                            await ShowSyncAsync();
                            await RefreshAsync();
                        })),
                    new Label { Text = "Families (Spec 011b)", FontSize = 17, Margin = new Thickness(0, 12, 0, 0) },
                    _familiesLine,
                    Row(Button("Refresh families", async () =>
                    {
                        _status.Text = await services.GetRequiredService<Families>().RefreshAsync(CancellationToken.None)
                            ? "Families refreshed" : "Families not refreshed (offline or signed out)";
                        await ShowFamiliesAsync();
                    })),
                    new Label { Text = "Last 20 entries", FontSize = 17, Margin = new Thickness(0, 12, 0, 0) },
                    _list,
                },
            },
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _sync.StatusChanged -= OnSyncStatus;
        _sync.StatusChanged += OnSyncStatus;
        try
        {
            await LoadAsync();
        }
        catch (Exception e)
        {
            // Clicked-style catch-all: OnAppearing is async void, nothing may escape into the Android looper.
            _status.Text = $"{e.GetType().Name} {e.Message}";
        }
    }

    protected override void OnDisappearing()
    {
        _sync.StatusChanged -= OnSyncStatus;
        base.OnDisappearing();
    }

    private async Task LoadAsync()
    {
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
        await ShowSyncAsync();
        await ShowFamiliesAsync();
    }

    private async Task ShowSyncAsync()
    {
        var counts = await _sync.CountsAsync(CancellationToken.None);
        var s = _sync.Status;
        _syncLine.Text = counts is null
            ? "signed out"
            : $"cursor {counts.Cursor} · outbox {counts.Outbox} · rejected {counts.Rejections}\n"
              + $"last: {s.Last?.ToString() ?? "none"}{(s.LastError is null ? "" : $" ({s.LastError})")}"
              + $"{(s.LastSyncedAt is { } at ? $" · synced {at:HH:mm:ss}" : "")}{(s.IsRunning ? " · running" : "")}";
    }

    private async Task ShowFamiliesAsync()
    {
        var families = await _services.GetRequiredService<Families>().ListAsync(CancellationToken.None);
        var store = _services.GetRequiredService<IFamilyStore>();
        var pending = await store.GetPendingInvitationAsync(CancellationToken.None);
        var joining = await store.GetJoiningAsync(CancellationToken.None);
        var lines = families.Select(f =>
            $"{(f.IsShown ? "▶ " : "  ")}{f.Id.Value.ToString()[^6..]} · {string.Join(", ", f.BabyNames)} · "
            + $"{f.Membership?.Role.ToString() ?? "not an account family"}{(f.Membership is { } m ? $" ({m.MemberCount}, owner {m.OwnerName})" : "")}"
            + $"{(f.IsHome ? " · home" : "")}{(f.IsSoftLocked ? " · soft-locked" : "")}{(f.HasEverSynced ? " · synced" : " · never synced")}");
        _familiesLine.Text = string.Join("\n", lines)
            + $"\npending invitation: {pending?.Code.Display ?? "none"} · joining: {(joining is null ? "none" : joining.Joined.Value.ToString()[^6..])}"
            + $"{(_sync.Status.ShownStopped ? " · shown family stopped (not a member)" : "")}";
    }

    // Raised from background threads: dispatched to the main thread, with a catch-all (async void).
    private void OnSyncStatus(object? sender, EventArgs e) => Dispatcher.Dispatch(async () =>
    {
        try
        {
            await ShowSyncAsync();
            await ShowFamiliesAsync();
        }
        catch (Exception ex)
        {
            _syncLine.Text = ex.GetType().Name;
        }
    });

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
        _ = RefreshQuietlyAsync();
    }

    // Fire-and-forget: nothing may escape into an unobserved task.
    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync();
            await ShowSyncAsync();
        }
        catch (Exception e)
        {
            _status.Text = $"{e.GetType().Name} {e.Message}";
        }
    }

    private async Task RefreshAsync()
    {
        _accountLine.Text = $"{_account.State} · this phone's household {_household}";
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

    /// <summary>Deliverables 2 and 3: the server's view of this account.</summary>
    private async Task ShowServerAccountAsync()
    {
        try
        {
            var me = await _accountApi.GetMeAsync(CancellationToken.None);
            var devices = string.Join("\n", me.Devices.Select(d =>
                $"· {d.Name}, last used {d.LastUsedAt.ToLocalTime():yyyy-MM-dd HH:mm}{(d.IsThisDevice ? " (this phone)" : "")}"));
            _status.Text = $"{me.Profile.Email}\nhousehold {me.Household} as {me.Role}\nthis phone's household {_household}\n{devices}";
        }
        catch (Exception e) when (e is AuthUnavailableException or AuthRejectedException)
        {
            _status.Text = $"/api/me: {e.GetType().Name} {e.Message}";
        }
    }

    /// <summary>Deliverable 6, part 1: remember the current access token before signing out.</summary>
    private async Task KeepTokenAsync()
    {
        _keptToken = (await _sessions.GetAsync(CancellationToken.None))?.AccessToken;
        _status.Text = _keptToken is null ? "Signed out: there is no token." : "Token kept. Sign out in Settings, then try it.";
    }

    /// <summary>Deliverable 6, part 2: the kept token after sign-out must get 401.</summary>
    private async Task TryOldTokenAsync()
    {
        if (_keptToken is null)
        {
            _status.Text = "Keep a token first.";
            return;
        }

        using var http = new HttpClient { BaseAddress = BuildConstants.ApiBaseAddress, Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("api/me", UriKind.Relative));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _keptToken);
        try
        {
            using var response = await http.SendAsync(request);
            _status.Text = $"Old token → {(int)response.StatusCode} {response.StatusCode}";
        }
        catch (HttpRequestException e)
        {
            _status.Text = "Old token: " + e.Message;
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

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, FontSize = 13, Padding = new Thickness(8, 4) };
        // Clicked is async void: nothing may escape, so a failure lands in the status line instead of crashing the app.
        button.Clicked += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception e)
            {
                _status.Text = $"{text}: {e.GetType().Name} {e.Message}";
            }
        };
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
