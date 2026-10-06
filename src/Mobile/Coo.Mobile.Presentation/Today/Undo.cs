using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Today;

/// <summary>The toast to show: its text, and whether it offers Undo.</summary>
public sealed record UndoToast(string Text, bool CanUndo);

/// <summary>
/// One level of undo, for 5 seconds (Spec 007 §5):
/// <list type="bullet">
/// <item>a delete is not written until the 5 seconds pass, so Undo writes nothing (Spec 001 has no undelete);</item>
/// <item>a quick log was saved at once, so Undo writes a tombstone;</item>
/// <item>an edit was saved at once, so Undo writes the previous values back as a new revision;</item>
/// <item>a merge of feeds was saved at once, so Undo restores the earliest feed and brings the others back, in one write.</item>
/// </list>
/// A pending delete lost to an app kill leaves the entry in place: the safe direction.
/// </summary>
public sealed class UndoService(DeleteEntry delete, ReviseEntry revise, UndoFeedMerge undoMerge, ICareLogStore store, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Func<Task>? _undo;
    private ITimer? _timer;
    private EntryId? _pendingDelete;
    private Func<Task>? _commit;

    /// <summary>Raised with the toast to show, or null when it should go away.</summary>
    public event EventHandler<UndoToast?>? ToastChanged;

    /// <summary>Raised when stored data changed (a delete was written, or an undo ran): pages refresh.</summary>
    public event EventHandler? DataChanged;

    /// <summary>Data changed under the pages without an action of theirs (a sync pull, a move): they refresh.</summary>
    public void ExternalChange() => DataChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>The entry waiting to be deleted, which lists hide meanwhile.</summary>
    public EntryId? PendingDelete
    {
        get
        {
            lock (_gate)
                return _pendingDelete;
        }
    }

    public async Task DeleteWithUndoAsync(EntryId id, string what)
    {
        await CommitPendingAsync();
        lock (_gate)
        {
            _pendingDelete = id;
            _commit = async () =>
            {
                await delete.ExecuteAsync(id, CancellationToken.None);
                DataChanged?.Invoke(this, EventArgs.Empty);
            };
            _undo = () => Task.CompletedTask;
        }

        Arm(Strings.Format("Toast.Deleted", what), commitWhenDone: true);
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OfferUndoForLog(EntryId created, string message) =>
        Offer(message, () => delete.ExecuteAsync(created, CancellationToken.None));

    /// <summary>Offers to put back what an edit changed: details, timing and note, each as its own revision.</summary>
    public void OfferUndoForEdit(CareEntry before, string message)
    {
        ArgumentNullException.ThrowIfNull(before);
        var (id, details, timing, note) = (before.Id, before.Details, before.Timing, before.Note);
        var derived = before.Kind.TimingFor(details) == TimingRule.DerivedFromDetails;
        Offer(message, async () =>
        {
            // Read, never "revise with no change": ReviseEntry always writes, and a read must not.
            if (await store.FindAsync(id, CancellationToken.None) is not LiveEntry { Entry: var entry })
                return;
            if (!entry.Details.Equals(details))
                await revise.ExecuteAsync(id, (e, v, now) => e.Revise<EntryDetails>(_ => Result<EntryDetails>.Ok(details), v, now), CancellationToken.None);
            if (!derived && entry.Timing != timing)
                await revise.ExecuteAsync(id, (e, v, now) => e.Retime(timing, v, now), CancellationToken.None);
            if (entry.Note != note)
                await revise.ExecuteAsync(id, (e, v, _) => e.SetNote(note, v), CancellationToken.None);
        });
    }

    /// <summary>Offers to put back a merge that was saved at once: <c>"Merged into one feed"</c> (Spec 011d §4).</summary>
    public void OfferUndoForMerge(FeedMergeReceipt merged, string message) =>
        Offer(message, () => undoMerge.ExecuteAsync(merged, CancellationToken.None));

    /// <summary>Shows a plain toast with no undo, such as "Switch side".</summary>
    public void Say(string message)
    {
        ToastChanged?.Invoke(this, new UndoToast(message, CanUndo: false));
    }

    public async Task UndoAsync()
    {
        Func<Task>? undo;
        lock (_gate)
        {
            undo = _undo;
            _undo = null;
            _commit = null;
            _pendingDelete = null;
            _timer?.Dispose();
            _timer = null;
        }

        if (undo is null)
            return;
        await undo();
        ToastChanged?.Invoke(this, new UndoToast(Strings.Get("Toast.Undone"), CanUndo: false));
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes a pending delete now, for example before a second delete replaces it.</summary>
    public async Task CommitPendingAsync()
    {
        Func<Task>? commit;
        lock (_gate)
        {
            commit = _commit;
            _commit = null;
            _pendingDelete = null;
            _undo = null;
            _timer?.Dispose();
            _timer = null;
        }

        if (commit is not null)
            await commit();
    }

    public void Dispose()
    {
        lock (_gate)
            _timer?.Dispose();
    }

    private void Offer(string message, Func<Task> undo)
    {
        _ = CommitPendingAsync();
        lock (_gate)
            _undo = undo;
        Arm(message, commitWhenDone: false);
    }

    private void Arm(string message, bool commitWhenDone)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = time.CreateTimer(_ => _ = ExpireAsync(commitWhenDone), null, Window, Timeout.InfiniteTimeSpan);
        }

        ToastChanged?.Invoke(this, new UndoToast(message, CanUndo: true));
    }

    private async Task ExpireAsync(bool commit)
    {
        if (commit)
        {
            await CommitPendingAsync();
        }
        else
        {
            lock (_gate)
            {
                _undo = null;
                _timer = null;
            }
        }

        ToastChanged?.Invoke(this, null);
    }
}
