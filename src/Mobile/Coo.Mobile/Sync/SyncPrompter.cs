using Coo.Mobile.Application.Auth;
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
/// </summary>
internal sealed class SyncPrompter(SyncEngine engine, Account account, IServiceProvider services)
{
    private bool _attached;
    private bool _consentShown;
    private bool _bringOverShown;
    private Page? _pushing;
    private Page? _sheet;

    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        engine.StatusChanged += OnStatusChanged;
        account.Changed += OnAccountChanged;
    }

    /// <summary>The window came back (main thread): a sheet held back while in the background gets its turn.</summary>
    public void Resumed() => OnMainThread(PromptAsync);

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
            (_consentShown, _bringOverShown) = (false, false);
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

    private async Task PromptAsync()
    {
        var status = engine.Status;
        if (status.Last == CycleOutcome.ConsentNeeded && !_consentShown)
            _consentShown = await ShowAsync(() => services.GetRequiredService<ConsentPage>());

        // Only once consent is given and the family's log has been pulled (final review C1): there is something to choose.
        else if (status.JoinReady && account.State is JoinPending && !_bringOverShown)
            _bringOverShown = await ShowAsync(() => services.GetRequiredService<BringOverPage>());
    }

    /// <returns>Whether the sheet opened; false in the background, while another sheet is showing, or with no window yet.</returns>
    private async Task<bool> ShowAsync(Func<Page> create)
    {
        if (!App.IsForeground || _pushing is not null || Microsoft.Maui.Controls.Application.Current?.Windows is not { Count: > 0 } windows || windows[0].Page is not { } root)
            return false;
        if (_sheet is not null && root.Navigation.ModalStack.Contains(_sheet))
            return false;

        var page = create();
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
