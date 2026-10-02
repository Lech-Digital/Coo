using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

public enum SyncLineAction
{
    None,
    TurnOnSharing,
    ChooseWhatToDo,
}

public sealed record SyncLineView(string Text, SyncLineAction Action);

/// <summary>
/// The line under the account (Spec 011 §2), the only sync indicator. Pure: the state, the status and the clock format
/// in, the text out. It never says "up to date" while the outbox holds anything; "Sharing n changes…" only when n &gt; 0
/// (ruling 11), so the 30-second pull does not flicker it. A cycle that stopped for good (a household mismatch, no progress)
/// is said as such, never as waiting for a connection: the engine tries again only when the app reopens.
/// </summary>
public static class SyncLine
{
    public static SyncLineView For(AccountState account, SyncStatus status, bool is24Hour)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(status);
        switch (account)
        {
            case SignedOut { SessionExpired: true }:
                return new SyncLineView(Strings.Get("Account.SessionExpired"), SyncLineAction.None);
            case SignedOut:
                return new SyncLineView(Strings.Get("Account.SignInHint"), SyncLineAction.None);
            case JoinPending:
                return new SyncLineView(Strings.Get("Account.JoinPending"), SyncLineAction.ChooseWhatToDo);
        }

        return status.Last switch
        {
            CycleOutcome.ConsentDeclined or CycleOutcome.ConsentNeeded => new SyncLineView(Strings.Get("Sync.Off"), SyncLineAction.TurnOnSharing),
            CycleOutcome.ClientTooOld => new SyncLineView(Strings.Get("Sync.UpdateApp"), SyncLineAction.None),
            _ when status.IsRunning && status.Pending > 0 => Sharing(status.Pending),
            CycleOutcome.Failed when status.LastError == SyncCycle.HouseholdMismatchError => new SyncLineView(Strings.Get("Sync.StoppedMismatch"), SyncLineAction.None),
            CycleOutcome.Failed when status.LastError == SyncCycle.NoProgressError => new SyncLineView(Strings.Get("Sync.StoppedNoProgress"), SyncLineAction.None),
            CycleOutcome.Failed or CycleOutcome.SoftLocked or CycleOutcome.Removed => new SyncLineView(
                status.Pending > 0 ? Strings.Plural("Sync.Waiting", status.Pending) : Strings.Get("Sync.WaitingNoChanges"), SyncLineAction.None),
            _ when status.Pending > 0 => Sharing(status.Pending),
            _ when status.LastSyncedAt is { } at => new SyncLineView(
                Strings.Format("Sync.UpToDate", new Formats(is24Hour, VolumeUnit.Millilitres).Time(at)), SyncLineAction.None),
            _ => new SyncLineView("", SyncLineAction.None),
        };
    }

    private static SyncLineView Sharing(int pending) => new(Strings.Plural("Sync.Sharing", pending), SyncLineAction.None);
}
