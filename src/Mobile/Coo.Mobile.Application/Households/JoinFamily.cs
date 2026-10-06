using Coo.Contracts.Households;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Households;

/// <summary>Where the join flow opens (spec §5.1): the invitation, or the choice after an accept the app was closed during.</summary>
public enum JoinStage
{
    None,
    Invitation,
    Choice,
}

public enum PreviewStatus
{
    Ready,
    AlreadyMember,
    Invalid,
    RateLimited,
    Unavailable,
    NothingPending,
}

public sealed record JoinPreview(PreviewStatus Status, InvitationPreview? Invitation = null);

public enum AcceptStatus
{
    Joined,
    AlreadyMember,

    /// <summary>The server refused the accept without the joiner's consent (§5.1): the consent sheet, then accept again.</summary>
    NeedsConsent,

    Invalid,
    RateLimited,
    Unavailable,
    NothingPending,
}

public sealed record JoinAccept(AcceptStatus Status);

public enum ChoiceStatus
{
    /// <summary>The joined family's log is not on the phone yet: "Couldn't refresh…", Try again.</summary>
    NotReady,

    NothingAsked,

    Ask,
}

/// <param name="Copies">The source has synced, so bringing it over copies (§6.2) and is followed by the old-family question.</param>
/// <param name="Source">The family shown before joining, whose entries are offered (and which the old-family question is about).</param>
public sealed record JoinChoice(ChoiceStatus Status, string InviterName, BringOverChoice? Offer = null, bool Copies = false, HouseholdId Source = default);

/// <summary>
/// Joining a family (Spec 011b §4–§6). The code becomes the pending invitation; preview, consent, accept; the joined family is
/// pulled; then §6 decides whether to ask anything. "Not now" anywhere clears the pending invitation and changes nothing on
/// the server. The joining marker (§5.3) is kept until the choice is made, so a closed app reopens at the choice; from the
/// pending invitation to the choice, one of the two is always stored, which is what the cycle's mid-join guard reads.
/// Every write after a server answer belongs to the sign-in that asked (<see cref="Account.Generation"/>).
/// </summary>
public sealed class JoinFamily(
    IFamilyStore families, IHouseholdApi api, ISessionStore sessions, Account account, ConsentGate consent, IHouseholdMoves moves,
    IHouseholdStore households, ISyncStore store, ISyncControl sync, Families list, BringFamilyEntriesOver bring, TimeProvider time)
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(72);

    /// <summary>A typed code, a pasted link, or an App Link's URL. False, with nothing stored and no request, when it holds no code.</summary>
    public async Task<bool> ReceiveAsync(string? text, CancellationToken cancellationToken)
    {
        var code = InvitationCode.Parse(CodeIn(text));
        if (code.IsFailure)
            return false;
        await families.SetPendingInvitationAsync(new PendingInvitation(code.Value, time.GetUtcNow()), cancellationToken);
        return true;
    }

    /// <summary>The flow opens with its sign-in step when signed out (spec §5.1 step 1).</summary>
    public async Task<bool> NeedsSignInAsync(CancellationToken cancellationToken) => await sessions.GetAsync(cancellationToken) is null;

    public async Task<JoinStage> StageAsync(CancellationToken cancellationToken)
    {
        if (await families.GetJoiningAsync(cancellationToken) is not null)
            return JoinStage.Choice;
        if (await families.GetPendingInvitationAsync(cancellationToken) is not { } pending)
            return JoinStage.None;
        if (time.GetUtcNow() - pending.ArrivedAt < PendingLifetime)
            return JoinStage.Invitation;
        await families.SetPendingInvitationAsync(null, cancellationToken);
        return JoinStage.None;
    }

    public async Task<JoinPreview> PreviewAsync(CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        if (await families.GetPendingInvitationAsync(cancellationToken) is not { } pending)
            return new JoinPreview(PreviewStatus.NothingPending);
        InvitationPreview preview;
        try
        {
            preview = await api.PreviewAsync(pending.Code, cancellationToken);
        }
        catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.InvitationInvalid)
        {
            if (account.Generation != generation)
                return new JoinPreview(PreviewStatus.Unavailable);
            await families.SetPendingInvitationAsync(null, cancellationToken);
            return new JoinPreview(PreviewStatus.Invalid);
        }
        catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.RateLimited)
        {
            return new JoinPreview(PreviewStatus.RateLimited);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return new JoinPreview(PreviewStatus.Unavailable);
        }

        if (!preview.AlreadyMember)
            return new JoinPreview(PreviewStatus.Ready, preview);
        if (account.Generation != generation)
            return new JoinPreview(PreviewStatus.Unavailable);
        await families.SetPendingInvitationAsync(null, cancellationToken);
        await ShowAsync(preview.Household, cancellationToken);
        return new JoinPreview(PreviewStatus.AlreadyMember, preview);
    }

    /// <summary>The account's consent to the current wording (asking <c>/api/me</c> when the phone does not know).</summary>
    public async Task<bool> HasConsentAsync(CancellationToken cancellationToken) =>
        await sessions.GetAsync(cancellationToken) is { } session && await consent.CheckAsync(session.Profile.UserId, cancellationToken) == ConsentState.Given;

    /// <summary>
    /// The accept. Only an answer of this sign-in is acted on; any other outcome leaves the invitation pending except
    /// <c>invitation.invalid</c> (ruling 25). <c>sync.consent-required</c> (§5.1: no membership without consent) forgets the
    /// cached consent and joins nothing: the flow shows the consent sheet and accepts again.
    /// </summary>
    public async Task<JoinAccept> AcceptAsync(string inviterName, CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        if (await families.GetPendingInvitationAsync(cancellationToken) is not { } pending)
            return new JoinAccept(AcceptStatus.NothingPending);
        if (await sessions.GetAsync(cancellationToken) is not { } session)
            return new JoinAccept(AcceptStatus.Unavailable);

        // The source is the family shown before joining (§6), read before the answer.
        var source = await moves.ActiveHouseholdAsync(cancellationToken);
        AcceptedInvitation accepted;
        try
        {
            accepted = await api.AcceptAsync(pending.Code, cancellationToken);
        }
        catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.InvitationInvalid)
        {
            if (account.Generation != generation)
                return new JoinAccept(AcceptStatus.Unavailable);
            await families.SetPendingInvitationAsync(null, cancellationToken);
            return new JoinAccept(AcceptStatus.Invalid);
        }
        catch (HouseholdRefusedException e) when (e.Code == HouseholdErrors.RateLimited)
        {
            return new JoinAccept(AcceptStatus.RateLimited);
        }
        catch (HouseholdRefusedException e) when (e.Code == SyncErrors.ConsentRequired)
        {
            if (account.Generation != generation)
                return new JoinAccept(AcceptStatus.Unavailable);
            await consent.ForgetAsync(session.Profile.UserId, cancellationToken);
            return new JoinAccept(AcceptStatus.NeedsConsent);
        }
        catch (Exception e) when (e is HouseholdUnavailableException or HouseholdRefusedException)
        {
            return new JoinAccept(AcceptStatus.Unavailable);
        }

        // Signed out or in again meanwhile: nothing is written; the invitation stays and a retry is idempotent (§5.2).
        if (account.Generation != generation)
            return new JoinAccept(AcceptStatus.Unavailable);
        if (accepted.Outcome == AcceptOutcome.AlreadyMember)
        {
            await families.SetPendingInvitationAsync(null, cancellationToken);
            await ShowAsync(accepted.Household, cancellationToken);
            return new JoinAccept(AcceptStatus.AlreadyMember);
        }

        // She is a member now: the join is stored whatever the token says, so nobody is a member without the choice (§5.3).
        // The marker goes in before the pending invitation goes, so the cycle's mid-join guard never lapses in between.
        await families.SetJoiningAsync(new JoiningMarker(accepted.Household, source, inviterName), CancellationToken.None);
        await families.SetPendingInvitationAsync(null, CancellationToken.None);
        await list.RefreshAsync(cancellationToken);
        await sync.SyncNowAsync(cancellationToken);
        return new JoinAccept(AcceptStatus.Joined);
    }

    /// <summary>§6: whether to ask, once the joined family's log is on the phone.</summary>
    public async Task<JoinChoice> DecideAsync(CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        if (await families.GetJoiningAsync(cancellationToken) is not { } marker)
            return new JoinChoice(ChoiceStatus.NothingAsked, "");
        if (!await store.HasPulledAsync(marker.Joined, cancellationToken))
        {
            await sync.SyncNowAsync(cancellationToken);
            if (account.Generation != generation || !await store.HasPulledAsync(marker.Joined, cancellationToken))
                return new JoinChoice(ChoiceStatus.NotReady, marker.InviterName);
        }

        var source = marker.Source;
        var synced = await moves.HasEverSyncedAsync(source, cancellationToken);
        var session = await sessions.GetAsync(cancellationToken);
        var accountFamily = session is not null
            && (await families.ListAsync(session.Profile.UserId, cancellationToken) ?? []).Any(m => m.Household == source);

        // Rulings 2 and 3: the joined family itself, or a family of another account, is never offered.
        if (source == marker.Joined || (synced && !accountFamily))
        {
            await FinishAsync(marker.Joined, generation, cancellationToken);
            return new JoinChoice(ChoiceStatus.NothingAsked, marker.InviterName);
        }

        var choice = await bring.PrepareAsync(source, marker.Joined, cancellationToken);
        if (choice.Local.EntryCount == 0 && (choice.Local.Babies.Count == 0 || choice.Family.Count > 0))
        {
            // Nothing to bring; an empty, never-synced account family is still left (§6, §6.1).
            if (!synced)
                await bring.LeaveEmptyAdoptedAsync(source, cancellationToken);
            await FinishAsync(marker.Joined, generation, cancellationToken);
            return new JoinChoice(ChoiceStatus.NothingAsked, marker.InviterName);
        }

        return new JoinChoice(ChoiceStatus.Ask, marker.InviterName, choice, Copies: synced, Source: source);
    }

    /// <summary>
    /// Bring my entries over into the joined family. The join ends only when it committed: a failure throws with nothing
    /// changed and the marker kept, so the choice is offered again.
    /// </summary>
    public async Task<BringOutcome> BringAsync(IReadOnlyDictionary<BabyId, BabyId?> mapping, CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        if (await families.GetJoiningAsync(cancellationToken) is not { } marker)
            return new BringOutcome(BringPath.AlreadyCopied, 0, 0, OldFamilyQuestion.None);
        var outcome = await bring.ExecuteAsync(marker.Source, marker.Joined, mapping, cancellationToken);
        await FinishAsync(marker.Joined, generation, cancellationToken);
        return outcome;
    }

    /// <summary>§6.3: both families stay; the joined one is shown.</summary>
    public async Task KeepSeparateAsync(CancellationToken cancellationToken)
    {
        var generation = account.Generation;
        if (await families.GetJoiningAsync(cancellationToken) is { } marker)
            await FinishAsync(marker.Joined, generation, cancellationToken);
    }

    /// <summary>"Not now" (§5.1): the pending invitation goes; nothing changes on the server.</summary>
    public Task DeclineAsync(CancellationToken cancellationToken) => families.SetPendingInvitationAsync(null, cancellationToken);

    /// <summary>
    /// The choice is made: the joining marker goes first, whatever the token says, so the mid-join guard never outlives the
    /// choice; then the joined family shows; the invitation's choice replaces a <c>created</c> move (ruling 4) and Spec 011's
    /// join-pending choice (§5.3); a cycle runs. Under another sign-in nothing is done (sign-out already cleared the marker).
    /// </summary>
    private async Task FinishAsync(HouseholdId joined, long generation, CancellationToken cancellationToken)
    {
        if (account.Generation != generation)
            return;
        await families.SetJoiningAsync(null, CancellationToken.None);
        await ShowAsync(joined, cancellationToken);
        await account.TryClearPendingMoveAsync(generation, cancellationToken);
        if (account.State is JoinPending)
            await account.TryCompleteJoinAsync(generation, cancellationToken);
        sync.RequestSync();
    }

    /// <summary>The family is shown, if its log is on the phone (it has settings), and the pages are told.</summary>
    private async Task ShowAsync(HouseholdId household, CancellationToken cancellationToken)
    {
        if ((await households.ListHouseholdsAsync(cancellationToken)).Any(h => h.Id == household && !h.IsDeleted))
            await moves.ActivateAsync(household, cancellationToken);
        sync.NotifyDataChanged();
    }

    /// <summary>The code in a typed text or in an invitation link on our host; anything else is returned as is, or null for a foreign link.</summary>
    private static string? CodeIn(string? text)
    {
        if (text is null)
            return null;
        var trimmed = text.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;
        return uri.Host.Equals(InvitationLinks.Host, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(InvitationLinks.PathPrefix, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath[InvitationLinks.PathPrefix.Length..]
            : null;
    }
}
