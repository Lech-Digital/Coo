namespace Coo.Mobile.Application.Sync;

/// <summary>
/// Raised after a local write committed with its outbox row (Spec 011 §6.3: a local write triggers a debounced cycle).
/// The stores raise it; pulled records never do, because they leave no outbox row. Handlers must not throw; if one
/// does, the exception is dropped and the remaining handlers still run, so a sync trigger never fails a write that
/// has already committed.
/// </summary>
public sealed class LocalWrites
{
    public event EventHandler? Written;

    public void Notify()
    {
        if (Written is not { } handlers)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // The write has committed: a failing sync trigger must not surface as a failed write.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Dropped: the next periodic cycle pushes the outbox row anyway.
            }
        }
    }
}
