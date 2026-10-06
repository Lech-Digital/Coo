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
/// <item>a delete is not written until the 5 seconds pass, so Undo writes nothing (there is no Domain undelete, Spec 001 §3.4);</item>
/// <item>a quick log was saved at once, so Undo writes a tombstone;</item>
/// <item>an edit was saved at once, so Undo writes the previous values back as a new revision;</item>
/// <item>a merge of feeds was saved at once and cannot wait, so Undo restores the earliest feed and writes the deleted
/// feeds again, live, with newer versions: replacement, as ADR 0002 §3 allows (Specs 001 §3.4, 007 §5, 011d §3).</item>
/// </list>
/// An undo that finds nothing left to put back says so rather than "Undone".
/// A pending delete lost to an app kill leaves the entry in place: the safe direction.
/// </summary>
public sealed class UndoService(DeleteEntry delete, ReviseEntry revise, UndoFeedMerge undoMerge, ICareLogStore store, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Func<Task<bool>>? _undo;
    private ITimer? _timer;
    private HashSet<EntryId> _pendingDeletes = [];
    private Func<Task>? _commit;

    /// <summary>Raised with the toast to show, or null when it should go away.</summary>
    public event EventHandler<UndoToast?>? ToastChanged;

    /// <summary>Raised when stored data changed (a delete was written, or an undo ran): pages refresh.</summary>
    public event EventHandler? DataChanged;

    /// <summary>Data changed under the pages without an action of theirs (a sync pull, a move): they refresh.</summary>
    public void ExternalChange() => DataChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Whether the entry waits to be deleted: lists hide it meanwhile.</summary>
    public bool IsPendingDelete(EntryId id)
    {
        lock (_gate)
            return _pendingDeletes.Contains(id);
    }

    public Task DeleteWithUndoAsync(EntryId id, string what) => DeleteWithUndoAsync([id], what);

    /// <summary>
    /// Deletes several entries under one undo, as "Keep only this one" does in a group of three or more (Spec 011d §4).
    /// The toast names one entry by <paramref name="what"/>, and counts several.
    /// </summary>
    public async Task DeleteWithUndoAsync(IReadOnlyCollection<EntryId> ids, string what)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await CommitPendingAsync();
        var pending = ids.ToHashSet();
        lock (_gate)
        {
            _pendingDeletes = pending;
            _commit = async () =>
            {
                foreach (var id in pending)
                    await delete.ExecuteAsync(id, CancellationToken.None);
                DataChanged?.Invoke(this, EventArgs.Empty);
            };
            _undo = () => Task.FromResult(true);
        }

        Arm(pending.Count == 1 ? Strings.Format("Toast.Deleted", what) : Strings.Plural("Toast.DeletedMany", pending.Count), commitWhenDone: true);
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OfferUndoForLog(EntryId created, string message) =>
        Offer(message, async () =>
        {
            await delete.ExecuteAsync(created, CancellationToken.None);
            return true;
        });

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
                return false;
            if (!entry.Details.Equals(details))
                await revise.ExecuteAsync(id, (e, v, now) => e.Revise<EntryDetails>(_ => Result<EntryDetails>.Ok(details), v, now), CancellationToken.None);
            if (!derived && entry.Timing != timing)
                await revise.ExecuteAsync(id, (e, v, now) => e.Retime(timing, v, now), CancellationToken.None);
            if (entry.Note != note)
                await revise.ExecuteAsync(id, (e, v, _) => e.SetNote(note, v), CancellationToken.None);
            return true;
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
        Func<Task<bool>>? undo;
        lock (_gate)
        {
            undo = _undo;
            _undo = null;
            _commit = null;
            _pendingDeletes = [];
            _timer?.Dispose();
            _timer = null;
        }

        if (undo is null)
            return;
        var wrote = await undo();
        ToastChanged?.Invoke(this, new UndoToast(Strings.Get(wrote ? "Toast.Undone" : "Toast.NothingUndone"), CanUndo: false));
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
            _pendingDeletes = [];
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

    private void Offer(string message, Func<Task<bool>> undo)
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
