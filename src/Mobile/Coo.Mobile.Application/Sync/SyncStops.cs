using Coo.Domain.Common;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// Families that answered <c>sync.not-a-member</c> (Spec 011b §9): each stops until the next wake (start, resume, Sync now,
/// an account change), while the others go on. One per app. A cycle stops a family under the wake it began in
/// (<see cref="Wake"/>): a stop from a cycle that began before the latest wake is dropped, so no wake is outlived.
/// </summary>
public sealed class SyncStops
{
    private readonly Lock _lock = new();
    private readonly HashSet<HouseholdId> _stopped = [];
    private long _wake;

    /// <summary>Moves with every <see cref="Clear"/>; a cycle reads it when it begins.</summary>
    public long Wake
    {
        get
        {
            lock (_lock)
                return _wake;
        }
    }

    /// <summary>Stops the family unless a wake came after <paramref name="wake"/>. True when it stopped.</summary>
    public bool Stop(HouseholdId household, long wake)
    {
        lock (_lock)
        {
            if (wake != _wake)
                return false;
            _stopped.Add(household);
            return true;
        }
    }

    /// <summary>Stops the family under the current wake.</summary>
    public void Stop(HouseholdId household)
    {
        lock (_lock)
            _stopped.Add(household);
    }

    public bool IsStopped(HouseholdId household)
    {
        lock (_lock)
            return _stopped.Contains(household);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _wake++;
            _stopped.Clear();
        }
    }
}
