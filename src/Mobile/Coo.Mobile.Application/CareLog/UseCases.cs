using Coo.Domain.CareLog;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Mobile.Application.CareLog;

/// <summary>What to log. The kind comes from <see cref="Details"/>; <see cref="Timing"/> is omitted for kinds that derive it.</summary>
public sealed record LogEntryCommand(
    HouseholdId Household,
    EntrySubject Subject,
    EntryDetails Details,
    EntryTiming? Timing,
    PersonRef LoggedBy,
    EntrySource Source,
    Note? Note = null);

/// <summary>Logs a new entry (Spec 004 §3). A Domain error comes back unchanged, and nothing is written.</summary>
public sealed class LogEntry(ICareLogStore store, IVersionClock clock, TimeProvider time)
{
    public async Task<Result<CareEntry>> ExecuteAsync(LogEntryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = time.GetUtcNow();
        var version = await clock.NextAsync(cancellationToken);
        var created = CareEntry.Create(
            new EntryId(Guid.CreateVersion7(now)), command.Household, command.Subject, command.Details, command.Timing,
            command.LoggedBy, command.Source, version, now, command.Note);

        if (created.IsSuccess)
            await store.SaveAsync(created.Value, cancellationToken);

        return created;
    }
}

/// <summary>
/// Changes a live entry: <c>(entry, version, now) =&gt; entry.Revise&lt;BreastFeed&gt;(d =&gt; d.SwitchSide(now), version, now)</c>.
/// Read-only and quarantined entries cannot be revised.
/// </summary>
public sealed class ReviseEntry(ICareLogStore store, IVersionClock clock, TimeProvider time)
{
    public async Task<Result<CareEntry>> ExecuteAsync(
        EntryId id, Func<CareEntry, HlcStamp, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        var stored = await store.FindAsync(id, cancellationToken);
        if (stored is null)
            return Result<CareEntry>.Failure(ApplicationErrors.EntryNotFound);
        if (stored is not LiveEntry { Entry: var entry })
            return Result<CareEntry>.Failure(ApplicationErrors.EntryNotEditable);

        var result = change(entry, await clock.NextAsync(cancellationToken), time.GetUtcNow());
        if (result.IsFailure)
            return Result<CareEntry>.Failure(result.Error!);

        await store.SaveAsync(entry, cancellationToken);
        return entry;
    }
}

/// <summary>Deletes any stored entry: live, read-only or quarantined. Nothing gets stuck (Spec 004 §5).</summary>
public sealed class DeleteEntry(ICareLogStore store, IVersionClock clock)
{
    public async Task<Result> ExecuteAsync(EntryId id, CancellationToken cancellationToken)
    {
        var stored = await store.FindAsync(id, cancellationToken);
        if (stored is null)
            return Result.Failure(ApplicationErrors.EntryNotFound);
        if (stored is LiveEntry { Entry.IsDeleted: true })
            return Result.Failure(RecordErrors.AlreadyDeleted);

        var deleted = await store.DeleteAsync(id, await clock.NextAsync(cancellationToken), cancellationToken);
        return deleted ? Result.Success : Result.Failure(RecordErrors.AlreadyDeleted);
    }
}

/// <summary>The stored entries in a window, newest first.</summary>
public sealed class GetEntries(ICareLogStore store)
{
    public Task<IReadOnlyList<StoredEntry>> ExecuteAsync(
        HouseholdId household, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        store.ListAsync(household, windowStart, windowEnd, cancellationToken);
}
