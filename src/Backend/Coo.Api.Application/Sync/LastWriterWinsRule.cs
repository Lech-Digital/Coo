using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;

namespace Coo.Api.Application.Sync;

/// <summary>
/// The server's half of ADR 0002 §3: the higher stamp wins, compared with the Domain's <see cref="HlcStamp"/>, the same
/// compiled rule every phone uses. An equal stamp is the same write again (a retry after <c>applied_changes</c> was
/// purged), so it is superseded. An ID in another household is never moved.
/// </summary>
public static class LastWriterWinsRule
{
    public static WriteDecision Decide(HouseholdId household, StoredVersion? stored, HlcStamp incoming) =>
        stored is null ? WriteDecision.Apply
        : stored.Household != household ? WriteDecision.HouseholdMismatch
        : stored.Version >= incoming ? WriteDecision.Superseded
        : WriteDecision.Apply;
}
