using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Application.Sync;

public enum AccessDenial
{
    None,
    NotAMember,
    MembershipRevoked,
    ConsentRequired,
}

public sealed record SyncAccessResult(Membership? Membership, AccessDenial Denial);

/// <summary>
/// The per-request check of every sync call (Spec 011 §7.1): an active membership of the household, then a consent to
/// the current wording (Q1, Q8). Revoked and never-a-member differ: only the first is ADR 0002 §10's signal.
/// </summary>
public sealed class SyncAccess(IMembershipStore memberships, IConsentStore consents)
{
    public async Task<SyncAccessResult> CheckAsync(UserId user, HouseholdId household, CancellationToken cancellationToken)
    {
        var found = await memberships.FindAsync(household, user, cancellationToken);
        if (found.Membership is null)
            return new SyncAccessResult(null, AccessDenial.NotAMember);
        if (found.IsRevoked)
            return new SyncAccessResult(null, AccessDenial.MembershipRevoked);
        if (await consents.FindAsync(user, CareDataConsent.Purpose, CareDataConsent.CurrentWording, cancellationToken) is null)
            return new SyncAccessResult(found.Membership, AccessDenial.ConsentRequired);
        return new SyncAccessResult(found.Membership, AccessDenial.None);
    }
}

/// <summary>The N−1 seam of ADR 0002 §11. There is no older version yet, so a change passes unchanged.</summary>
public static class SchemaUpgrade
{
    /// <remarks>A version above the current one passes too: the validator rejects it as <c>sync.schema-unsupported</c>.
    /// Below <see cref="SchemaVersions.Oldest"/> the endpoint has already answered 426.</remarks>
    public static IncomingChange ToCurrent(int schemaVersion, IncomingChange change) =>
        schemaVersion >= SchemaVersions.Current
            ? change
            : throw new NotSupportedException($"No upgrader from schema version {schemaVersion}.");
}

/// <summary>
/// <c>POST /api/sync/push</c> (Spec 011 §6.1). Each change gets its own result; a change ID decided before returns its
/// stored result and is not applied twice (ADR 0002 §5). One bad change never stops the others.
/// </summary>
public sealed class PushChanges(ISyncStore store, TimeProvider time)
{
    public async Task<IReadOnlyList<ChangeVerdict>> ExecuteAsync(
        HouseholdId household, Membership member, int schemaVersion, IReadOnlyList<IncomingChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(changes);
        var verdicts = new List<ChangeVerdict>(changes.Count);
        foreach (var change in changes)
        {
            if (await store.FindResultAsync(change.ChangeId, cancellationToken) is { } stored)
            {
                verdicts.Add(new ChangeVerdict(change.ChangeId, stored));
                continue;
            }

            var now = time.GetUtcNow();
            var current = SchemaUpgrade.ToCurrent(schemaVersion, change);
            var validated = ChangeValidator.Validate(current, household, member, schemaVersion, now);
            var result = validated.IsSuccess
                ? await store.ApplyAsync(household, change.ChangeId, validated.Value, now, cancellationToken)
                : await store.RecordRejectionAsync(change.ChangeId, validated.Error!.Code, now, cancellationToken);
            verdicts.Add(new ChangeVerdict(change.ChangeId, result));
        }

        return verdicts;
    }
}

/// <summary><c>GET /api/sync/pull</c> (Spec 011 §6.2): one page of at most 500 records.</summary>
public sealed class PullChanges(ISyncStore store)
{
    public Task<PullPage> ExecuteAsync(HouseholdId household, long cursor, long? bootstrapHead, CancellationToken cancellationToken) =>
        store.PullAsync(household, cursor, bootstrapHead, SyncLimits.MaxPullRecords, cancellationToken);
}

/// <summary><c>POST /api/consents</c> (Spec 011 §3): only the current wording of the care-data purpose.</summary>
public sealed class RecordConsent(IConsentStore consents, TimeProvider time)
{
    public const int AppVersionMaxLength = 40;
    public const int LanguageMaxLength = 16;

    public async Task<Result> ExecuteAsync(
        UserId user, string? purpose, string? wording, string? appVersion, string? language, CancellationToken cancellationToken)
    {
        if (purpose != CareDataConsent.Purpose || wording != CareDataConsent.CurrentWording)
            return Result.Failure(SyncErrors.ConsentUnknownWording);

        await consents.RecordAsync(
            new ConsentRecord(user, purpose, wording, time.GetUtcNow(), Cut(appVersion, AppVersionMaxLength), Cut(language, LanguageMaxLength)),
            cancellationToken);
        return Result.Success;
    }

    private static string? Cut(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary><c>GET /api/households/{id}/membership</c> (ADR 0002 §10, step 3).</summary>
public sealed class GetMembershipStatus(IMembershipStore memberships)
{
    public Task<MembershipLookup> ExecuteAsync(UserId user, HouseholdId household, CancellationToken cancellationToken) =>
        memberships.FindAsync(household, user, cancellationToken);
}

/// <summary>The daily clean-up (Spec 011 §7.2): change results and conflict records older than 30 days go.</summary>
public sealed class PurgeSyncHistory(ISyncStore store, TimeProvider time)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    public Task<int> ExecuteAsync(CancellationToken cancellationToken) => store.PurgeAsync(time.GetUtcNow() - KeepFor, cancellationToken);
}
