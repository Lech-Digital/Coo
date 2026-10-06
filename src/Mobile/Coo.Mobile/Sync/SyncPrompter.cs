using Coo.Domain.Common;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Pages;

namespace Coo.Mobile.Sync;

/// <summary>
/// Opens the consent sheet when a cycle finds no consent (Spec 011 §3: "right after a successful sign-in, in place, and
/// whenever a cycle would start for an account without consent"), at most once per app run and sign-in; and the
/// bring-over sheet once when join-pending, with consent given and the family's log pulled (§4). Settings opens either
/// when its line offers it. One sheet at a time: a sheet that could not open because another was showing opens when that
/// one closes. No sheet opens while Coo is in the background (<see cref="App.IsForeground"/>); one held back then opens on
/// resume. Everything runs on the main thread.
/// Spec 011b adds the removal sheet (§8.2: one removal at a time; Back closes it until the next resume), the join sheet (§5:
/// from a link, the code sheet, or a pending invitation or choice at start and resume), and the consent sheet on request
/// (<see cref="AskConsentAsync"/>). While the join sheet shows it owns the sheets: nothing opens but its own consent.
/// </summary>
internal sealed class SyncPrompter(SyncEngine engine, Account account, IServiceProvider services)
{
    private bool _attached;
    private bool _consentShown;
    private bool _bringOverShown;
    private bool _joinOffered;
    private HouseholdId? _removalShown;
    private Pages.JoinPage? _join;
    private Page? _pushing;
    private Page? _sheet;

    /// <summary>The join sheet left the modal stack, whatever ended it (main thread).</summary>
    public event EventHandler? JoinEnded;

    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        engine.StatusChanged += OnStatusChanged;
        account.Changed += OnAccountChanged;
    }

    /// <summary>The window came back (main thread): a held-back sheet, a removal closed with Back, a join left open get their turn.</summary>
    public void Resumed() => OnMainThread(() =>
    {
        (_joinOffered, _removalShown) = (false, null);
        return PromptAsync();
    });

    /// <summary>
    /// The window stopped (main thread). A push still in flight must never block the sheets for the rest of the run: it is
    /// forgotten here, and the modal stack tells whether it landed.
    /// </summary>
    public void Backgrounded() => _pushing = null;

    public Task ShowConsentAsync() => ShowAsync(() => services.GetRequiredService<ConsentPage>());

    public Task ShowBringOverAsync() => ShowAsync(() => services.GetRequiredService<BringOverPage>());

    // Both events come from background threads.
    private void OnStatusChanged(object? sender, EventArgs e) => OnMainThread(PromptAsync);

    private void OnAccountChanged(object? sender, EventArgs e) => OnMainThread(() =>
    {
        if (account.State is SignedOut)
            (_consentShown, _bringOverShown, _joinOffered, _removalShown) = (false, false, false, null);
        return Task.CompletedTask;
    });

    private static void OnMainThread(Func<Task> action) => MainThread.BeginInvokeOnMainThread(async () =>
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // A sheet that cannot open now is offered again from Settings; never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    });

    /// <summary>
    /// The join sheet (spec §5): from a link, the code sheet, or at start/resume with an invitation or a choice pending. Once
    /// it opened, it is not offered again by itself until the next resume (Back at the choice keeps the marker).
    /// </summary>
    /// <returns>Whether it opened; false in the background, while another sheet (this one included) is showing, or with no window.</returns>
    public async Task<bool> ShowJoinAsync(bool malformed = false)
    {
        var opened = await ShowAsync(() =>
        {
            var page = services.GetRequiredService<JoinPage>();
            page.Malformed = malformed;
            page.Ended += (_, _) =>
            {
                if (ReferenceEquals(_join, page))
                    _join = null;
                JoinEnded?.Invoke(this, EventArgs.Empty);
            };
            _join = page;
            return page;
        });
        if (opened)
            _joinOffered = true;
        return opened;
    }

    /// <summary>
    /// A link arrived while Coo runs (spec §4.1), already stored when it held a code. A join sheet showing starts again from
    /// it; otherwise the join sheet opens now, or as soon as the sheet in front closes.
    /// </summary>
    public async Task OfferLinkAsync(bool malformed)
    {
        _joinOffered = false;
        if (JoinShowing())
            _join!.NewLink(malformed);
        else
            await ShowJoinAsync(malformed);
    }

    /// <summary>Settings → Family → Join a family, and the setup step's link (spec §4.4).</summary>
    public Task<bool> ShowEnterCodeAsync() => ShowAsync(() =>
    {
        var page = services.GetRequiredService<EnterCodePage>();
        page.CodeAccepted += (_, _) => OnMainThread(async () =>
        {
            _joinOffered = false;
            await ShowJoinAsync();
        });
        return page;
    });

    /// <summary>
    /// The consent sheet asked for by a flow (inviting, joining): it may open over the join sheet, and the flow waits for it to
    /// close. True when consent is given then. Main thread.
    /// </summary>
    public async Task<bool> AskConsentAsync()
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = await ShowAsync(
            () =>
            {
                var page = services.GetRequiredService<ConsentPage>();

                // Disappearing is also raised by a page opening over it: closed only once it left the modal stack.
                page.Disappearing += (_, _) => OnMainThread(() =>
                {
                    if (!OnModalStack(page))
                        closed.TrySetResult();
                    return Task.CompletedTask;
                });
                return page;
            },
            overSheet: true);
        if (!opened)
            return false;
        _consentShown = true;
        await closed.Task;
        return await services.GetRequiredService<ISessionStore>().GetAsync(CancellationToken.None) is { } session
            && await services.GetRequiredService<ConsentGate>().CachedAsync(session.Profile.UserId, CancellationToken.None) == ConsentState.Given;
    }

    private bool JoinShowing() => _join is not null && OnModalStack(_join);

    private static bool OnModalStack(Page page) =>
        Microsoft.Maui.Controls.Application.Current?.Windows is { Count: > 0 } windows
        && windows[0].Page?.Navigation.ModalStack.Contains(page) == true;

    private async Task PromptAsync()
    {
        // The join flow owns the sheets while it shows (spec §5.1): nothing opens over it but its own consent.
        if (JoinShowing())
            return;

        // A link that arrived since it was last looked at (it may not have been stored yet).
        var held = await services.GetRequiredService<Joining.InvitationInbox>().StoreHeldAsync();
        if (held != Joining.HeldLink.None)
            _joinOffered = false;

        var status = engine.Status;
        if (status.Removal is { } removed && removed != _removalShown)
        {
            if (await ShowAsync(() => services.GetRequiredService<RemovalPage>()))
                _removalShown = removed;
            return;
        }

        if (held == Joining.HeldLink.Malformed)
        {
            await ShowJoinAsync(malformed: true);
            return;
        }

        if (!_joinOffered && await services.GetRequiredService<JoinFamily>().StageAsync(CancellationToken.None) != JoinStage.None)
        {
            await ShowJoinAsync();
            return;
        }

        if (status.Last == CycleOutcome.ConsentNeeded && !_consentShown)
            _consentShown = await ShowAsync(() => services.GetRequiredService<ConsentPage>());

        // Only once consent is given and the family's log has been pulled (final review C1): there is something to choose.
        else if (status.JoinReady && account.State is JoinPending && !_bringOverShown)
            _bringOverShown = await ShowAsync(() => services.GetRequiredService<BringOverPage>());
    }

    /// <returns>Whether the sheet opened; false in the background, while another sheet is showing, or with no window yet.</returns>
    /// <param name="create">Builds the sheet; called only when it will be pushed.</param>
    /// <param name="overSheet">
    /// A sheet a flow asked for (the consent while joining): it opens over the sheet showing, which stays the tracked one.
    /// </param>
    private async Task<bool> ShowAsync(Func<Page> create, bool overSheet = false)
    {
        if (!App.IsForeground || _pushing is not null || Microsoft.Maui.Controls.Application.Current?.Windows is not { Count: > 0 } windows || windows[0].Page is not { } root)
            return false;
        if (!overSheet && _sheet is not null && root.Navigation.ModalStack.Contains(_sheet))
            return false;

        var page = create();
        if (!overSheet || _sheet is null || !root.Navigation.ModalStack.Contains(_sheet))
            _sheet = page;
        _pushing = page;

        // When it closes, whatever was held back while it showed (bring-over after consent) gets its turn.
        page.Disappearing += (_, _) => OnMainThread(PromptAsync);
        try
        {
            await root.Navigation.PushModalAsync(page);
            return true;
        }
        catch
        {
            // Only its own sheet: a newer one that opened meanwhile stays tracked.
            if (ReferenceEquals(_sheet, page))
                _sheet = null;
            throw;
        }
        finally
        {
            // Only its own push: one forgotten in the background must not clear a newer one.
            if (ReferenceEquals(_pushing, page))
                _pushing = null;
        }
    }
}
