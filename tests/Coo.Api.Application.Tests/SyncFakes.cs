using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Api.Application.Tests;

internal sealed class FakeSyncStore : ISyncStore
{
    private long _seq;

    public Dictionary<Guid, ChangeResult> Results { get; } = [];

    public Dictionary<(string Type, Guid Id), (HouseholdId Household, HlcStamp Version, bool Deleted, long Seq, SyncWrite Write)> Rows { get; } = [];

    public List<(Guid ChangeId, HlcStamp Winner, HlcStamp Loser)> Conflicts { get; } = [];

    public int ApplyCalls { get; private set; }

    public Task<ChangeResult?> FindResultAsync(Guid changeId, CancellationToken cancellationToken) =>
        Task.FromResult(Results.GetValueOrDefault(changeId));

    public Task<ChangeResult> ApplyAsync(HouseholdId household, Guid changeId, SyncWrite write, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ApplyCalls++;
        var key = (write.EntityType, write.EntityId);
        StoredVersion? stored = Rows.TryGetValue(key, out var row) ? new StoredVersion(row.Household, row.Version) : null;
        ChangeResult result;
        switch (LastWriterWinsRule.Decide(household, stored, write.Version))
        {
            case WriteDecision.HouseholdMismatch:
                result = ChangeResult.Rejected(RejectionReasons.HouseholdMismatch);
                break;
            case WriteDecision.Superseded:
                Conflicts.Add((changeId, stored!.Version, write.Version));
                result = ChangeResult.Superseded;
                break;
            default:
                if (write is not TombstoneWrite || stored is not null)
                    Rows[key] = (household, write.Version, write is TombstoneWrite, ++_seq, write);
                result = ChangeResult.Applied;
                break;
        }

        Results.TryAdd(changeId, result);
        return Task.FromResult(Results[changeId]);
    }

    public Task<ChangeResult> RecordRejectionAsync(Guid changeId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Results.TryAdd(changeId, ChangeResult.Rejected(reason));
        return Task.FromResult(Results[changeId]);
    }

    public Task<PullPage> PullAsync(HouseholdId household, long cursor, long? bootstrapHead, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Pull paging is the real store's (SyncStoreTests).");

    public Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken) => Task.FromResult(0);
}

internal sealed class FakeMemberships : IMembershipStore
{
    public Dictionary<(HouseholdId, UserId), MembershipLookup> Rows { get; } = [];

    public Task<MembershipLookup> FindAsync(HouseholdId household, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.GetValueOrDefault((household, user), MembershipLookup.None));
}

internal sealed class FakeConsents : IConsentStore
{
    public List<ConsentRecord> Rows { get; } = [];

    public Task RecordAsync(ConsentRecord record, CancellationToken cancellationToken)
    {
        if (!Rows.Any(r => r.User == record.User && r.Purpose == record.Purpose && r.Wording == record.Wording))
            Rows.Add(record);
        return Task.CompletedTask;
    }

    public Task<ConsentRecord?> FindAsync(UserId user, string purpose, string wording, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.User == user && r.Purpose == purpose && r.Wording == wording));
}
