using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

public enum BringPath
{
    /// <summary>A never-synced source was re-keyed into the target (§6.1).</summary>
    Moved,

    /// <summary>A synced source's records were copied as new records (§6.2).</summary>
    Copied,

    /// <summary>This pair was copied before: nothing was written.</summary>
    AlreadyCopied,
}

/// <summary>What the phone asks about the old family after a copy (§6.2's table).</summary>
public enum OldFamilyQuestion
{
    None,
    DeleteOld,
    LeaveOld,
}

public sealed record BringOutcome(BringPath Path, int Copied, int Skipped, OldFamilyQuestion Question);

/// <summary>
/// "Bring my entries over" into a chosen family (Spec 011b §6): the phone chooses the path, never the person. A source that
/// has never synced moves (one transaction, Spec 011 §4's bring-over); one that synced is copied (ruling 8's stamps) and is
/// never re-keyed (R5). A source of another account is never offered (callers check <see cref="FamilyOnPhone.MayBeBroughtOver"/>).
/// Each change on the phone runs in the engine's exclusive section (<see cref="ISyncControl.RunExclusiveAsync"/>), so callers
/// (the join and Settings) get it without asking; none of them may already hold it while waiting for a cycle.
/// </summary>
public sealed class BringFamilyEntriesOver(
    ISessionStore sessions, Account account, IHouseholdMoves moves, IFamilyRecords records, IFamilyStore families, IHouseholdApi api,
    IVersionClock clock, Families list, ISyncControl sync)
{
    public async Task<BringOverChoice> PrepareAsync(HouseholdId source, HouseholdId target, CancellationToken cancellationToken) =>
        new(await moves.SummaryAsync(source, cancellationToken), (await moves.SummaryAsync(target, cancellationToken)).Babies);

    /// <summary>A target that is not one of the target family's babies counts as "Add as a new baby". A failing write throws (rolled back).</summary>
    public async Task<BringOutcome> ExecuteAsync(
        HouseholdId source, HouseholdId target, IReadOnlyDictionary<BabyId, BabyId?> mapping, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var generation = account.Generation;
        var session = await sessions.GetAsync(cancellationToken) ?? throw new InvalidOperationException("Signed out: nothing can be brought over.");
        var user = session.Profile.UserId;
        var targetBabies = (await moves.SummaryAsync(target, cancellationToken)).Babies.Select(b => b.Id).ToHashSet();
        var babies = (await moves.SummaryAsync(source, cancellationToken)).Babies.ToDictionary(
            b => b.Id,
            b => mapping.TryGetValue(b.Id, out var chosen) && chosen is { } t && targetBabies.Contains(t) ? t : (BabyId?)null);
        var plan = new BringOverPlan(source, target, babies);
        if (account.Generation != generation)
            throw new InvalidOperationException("The account changed: nothing was brought over.");

        // Move or copy with no cycle in flight (final review C1): a first push of a never-synced source that is still
        // unanswered would otherwise land under the source after its records moved, and never reach the target. Decided
        // after the wait, a source whose push was answered has synced and is copied.
        var (moved, copied) = (false, (CopyResult?)null);
        await sync.RunExclusiveAsync(
            async ct =>
            {
                if (account.Generation != generation)
                    throw new InvalidOperationException("The account changed: nothing was brought over.");

                // The port re-checks "never synced" inside its transaction: a source that synced meanwhile is copied instead.
                if (!await moves.HasEverSyncedAsync(source, ct) && await moves.BringOverAsync(plan, user, ct))
                {
                    moved = true;
                    return;
                }

                if (await families.WasCopiedAsync(source, target, ct))
                    return;

                try
                {
                    copied = await CopyAsync(plan, user, ct);
                }
                catch (StampBudgetExceededException)
                {
                    // Records were added between the count and the copy, which then wrote nothing: count, reserve and copy
                    // once more. A second failure surfaces.
                    copied = await CopyAsync(plan, user, ct);
                }
            },
            cancellationToken);

        if (moved)
        {
            await LeaveEmptyAdoptedAsync(source, cancellationToken);
            return new BringOutcome(BringPath.Moved, 0, 0, OldFamilyQuestion.None);
        }

        if (copied is null)
            return new BringOutcome(BringPath.AlreadyCopied, 0, 0, OldFamilyQuestion.None);

        var membership = (await families.ListAsync(user, cancellationToken) ?? []).FirstOrDefault(m => m.Household == source);
        var question = membership switch
        {
            { Role: Role.Owner, MemberCount: 1 } => OldFamilyQuestion.DeleteOld,
            { Role: Role.Parent or Role.Caregiver } => OldFamilyQuestion.LeaveOld,
            _ => OldFamilyQuestion.None,
        };
        return new BringOutcome(BringPath.Copied, copied.Copied, copied.Skipped, question);
    }

    /// <summary>
    /// §6.1: her account's family that she alone owns, once empty, is left with <c>confirm: false</c>: the server deletes it only
    /// when it holds no record. Refused or offline, it simply stays as a separate family; nothing is discarded, nothing asked.
    /// </summary>
    public async Task LeaveEmptyAdoptedAsync(HouseholdId source, CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return;
        var membership = (await families.ListAsync(session.Profile.UserId, cancellationToken) ?? []).FirstOrDefault(m => m.Household == source);
        if (membership is not { Role: Role.Owner, MemberCount: 1 })
            return;
        var left = await moves.SummaryAsync(source, cancellationToken);
        if (left.EntryCount > 0 || left.Babies.Count > 0)
            return;
        try
        {
            await api.LeaveAsync(source, confirm: false, cancellationToken);
        }
        catch (Exception e) when (e is HouseholdRefusedException or HouseholdUnavailableException)
        {
            return;
        }

        // The answer belongs to the sign-in that asked: under another one, the phone keeps the family (the next list refresh decides).
        // With no cycle in flight, as every whole-family change on the phone (final review C1).
        await sync.RunExclusiveAsync(
            async ct =>
            {
                if (account.Generation != generation)
                    return;
                await list.RemoveFromPhoneAsync(source, ct);
                await list.RefreshAsync(ct);
            },
            cancellationToken);
    }

    /// <summary>
    /// Ruling 8: one stamp is taken and the clock moves past every stamp the copy may use (<c>counter + n</c>, n = the source's
    /// records) before the transaction, so a crash after it commits never leaves the clock behind a stored stamp.
    /// </summary>
    private async Task<CopyResult?> CopyAsync(BringOverPlan plan, UserId user, CancellationToken cancellationToken)
    {
        var count = await records.CountRecordsAsync(plan.From, cancellationToken);
        var first = await clock.NextAsync(cancellationToken);
        await clock.ObserveAsync(first with { Counter = first.Counter + count }, cancellationToken);
        return await records.CopyAsync(plan, user, first, count, cancellationToken);
    }
}
