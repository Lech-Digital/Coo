using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Pages;

namespace Coo.Mobile.Sync;

/// <summary>
/// Opens the consent sheet when a cycle finds no consent (Spec 011 §3: "right after a successful sign-in, in place, and
/// whenever a cycle would start for an account without consent"), at most once per app run and sign-in; and the
/// bring-over sheet once when join-pending (§4). Settings opens either at any time. One sheet at a time: a sheet that
/// could not open because another was showing opens when that one closes. Everything runs on the main thread.
/// </summary>
internal sealed class SyncPrompter(SyncEngine engine, Account account, IServiceProvider services)
{
    private bool _attached;
    private bool _consentShown;
    private bool _bringOverShown;
    private bool _pushing;
    private Page? _sheet;

    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        engine.StatusChanged += OnStatusChanged;
        account.Changed += OnAccountChanged;
    }

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
        switch (engine.Status.Last)
        {
            case CycleOutcome.ConsentNeeded when !_consentShown:
                _consentShown = await ShowAsync(() => services.GetRequiredService<ConsentPage>());
                break;
            case CycleOutcome.JoinPending when !_bringOverShown:
                _bringOverShown = await ShowAsync(() => services.GetRequiredService<BringOverPage>());
                break;
        }
    }

    /// <returns>Whether the sheet opened; false while another sheet is showing or no window is up yet.</returns>
    private async Task<bool> ShowAsync(Func<Page> create)
    {
        if (_pushing || Microsoft.Maui.Controls.Application.Current?.Windows is not { Count: > 0 } windows || windows[0].Page is not { } root)
            return false;
        if (_sheet is not null && root.Navigation.ModalStack.Contains(_sheet))
            return false;

        var page = create();
        _sheet = page;
        _pushing = true;

        // When it closes, whatever was held back while it showed (bring-over after consent) gets its turn.
        page.Disappearing += (_, _) => OnMainThread(PromptAsync);
        try
        {
            await root.Navigation.PushModalAsync(page);
            return true;
        }
        catch
        {
            _sheet = null;
            throw;
        }
        finally
        {
            _pushing = false;
        }
    }
}
