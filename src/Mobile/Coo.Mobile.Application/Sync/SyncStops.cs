using System.Collections.Concurrent;
using Coo.Domain.Common;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// Families that answered <c>sync.not-a-member</c> (Spec 011b §9): each stops until the next wake (start, resume, Sync now,
/// an account change), while the others go on. One per app.
/// </summary>
public sealed class SyncStops
{
    private readonly ConcurrentDictionary<HouseholdId, byte> _stopped = new();

    public void Stop(HouseholdId household) => _stopped[household] = 0;

    public bool IsStopped(HouseholdId household) => _stopped.ContainsKey(household);

    public void Clear() => _stopped.Clear();
}
