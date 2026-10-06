using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Joining;

public enum HeldLink
{
    None,
    Stored,
    Malformed,
}

/// <summary>
/// Where an App Link waits until the database is open (spec §4.1): <c>OnCreate</c> runs before the start-up migrations, so the
/// link is held in memory, and the start-up page stores it as the pending invitation. Once Coo is running, a link from
/// <c>OnNewIntent</c> is stored and the join flow opens at once (as soon as no other sheet shows). The link holds a code, so
/// it is never logged.
/// </summary>
internal sealed class InvitationInbox(IServiceProvider services)
{
    private Uri? _held;
    private volatile bool _ready;

    /// <summary>From Android's thread; never throws.</summary>
    public void Offer(Uri link)
    {
        Interlocked.Exchange(ref _held, link);
        if (_ready)
            Drain();
    }

    /// <summary>
    /// Called by the start-up page once routing is done: later links open the flow directly. A link that arrived after the
    /// start-up page stored the held one (and before this) is drained the same way, so it is never lost.
    /// </summary>
    public void MarkReady()
    {
        _ready = true;
        Drain();
    }

    /// <summary>
    /// The start-up page is starting (a new window, perhaps in a process that lives on, M4): links are held again until it has
    /// routed, so they go held → stored → opened over the right page.
    /// </summary>
    public void Unready() => _ready = false;

    /// <summary>The held link becomes the pending invitation; malformed links are reported, never stored.</summary>
    public async Task<HeldLink> StoreHeldAsync()
    {
        if (Interlocked.Exchange(ref _held, null) is not { } link)
            return HeldLink.None;
        return await services.GetRequiredService<JoinFamily>().ReceiveAsync(link.ToString(), CancellationToken.None)
            ? HeldLink.Stored
            : HeldLink.Malformed;
    }

    private void Drain() => MainThread.BeginInvokeOnMainThread(async () =>
    {
        try
        {
            // A join that cannot start again now keeps the link held (review I2); the prompter stores it when the join ends.
            var prompter = services.GetRequiredService<Sync.SyncPrompter>();
            if (prompter.HoldsLinks)
                return;
            var held = await StoreHeldAsync();
            if (held != HeldLink.None)
                await prompter.OfferLinkAsync(malformed: held == HeldLink.Malformed);
        }
#pragma warning disable CA1031 // Fire-and-forget on the main thread: the link stays pending and the flow opens at the next start or resume.
        catch (Exception e)
#pragma warning restore CA1031
        {
            global::Android.Util.Log.Warn("Coo", $"Invitation link not handled: {e.GetType().Name}");
        }
    });
}
