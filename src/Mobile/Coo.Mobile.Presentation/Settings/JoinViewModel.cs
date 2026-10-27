using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Households;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

public enum JoinStep
{
    Loading,
    SignIn,
    Invitation,
    Message,
    Choice,
}

/// <summary>
/// The join flow (Spec 011b §5.1): sign in if needed, the invitation, consent if needed (the host's sheet), accept, then
/// §6's choice through <see cref="BringOver"/>. Not now anywhere clears the pending invitation and changes nothing on the
/// server. The page closes on <see cref="Finished"/>; its argument is the toast.
/// </summary>
public sealed partial class JoinViewModel : ObservableObject
{
    private enum Retry
    {
        None,
        Preview,
        Accept,
        Join,
        Decide,
        Start,
    }

    private readonly JoinFamily _join;
    private readonly SignIn _signIn;
    private readonly IConsentPrompt _consent;
    private readonly IClockFormat _clock;
    private InvitationPreview? _preview;
    private InvitationCode? _code;
    private Retry _retry;
    private string _inviterName = "";

    // The message is about a link that held no code: Not now (Back) leaves any pending invitation alone.
    private bool _malformedShown;

    [ObservableProperty]
    private JoinStep _step = JoinStep.Loading;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _body = "";

    [ObservableProperty]
    private string _babies = "";

    [ObservableProperty]
    private string _message = "";

    /// <summary>"Invitation code: ABCD-EFGH" on the sign-in step; empty when there is no code to show.</summary>
    [ObservableProperty]
    private string _codeLine = "";

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _canKeepSeparate;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public JoinViewModel(JoinFamily join, SignIn signIn, IConsentPrompt consent, IClockFormat clock, FamilyBringOverViewModel bringOver)
    {
        (_join, _signIn, _consent, _clock) = (join, signIn, consent, clock);
        BringOver = bringOver;
        BringOver.Finished += (_, toast) => Finished?.Invoke(this, toast);
    }

    public FamilyBringOverViewModel BringOver { get; }

    public event EventHandler<string>? Finished;

    public static string SignInTitle => Strings.Get("Join.SignInTitle");

    public static string SignInBody => Strings.Get("Join.SignInBody");

    public static string SignInText => Strings.Get("Account.SignIn");

    public static string NotNowText => Strings.Get("Consent.NotNow");

    public static string JoinText => Strings.Get("Join.Join");

    public static string OkText => Strings.Get("Join.OK");

    public static string TryAgainText => Strings.Get("Join.TryAgain");

    public static string KeepSeparateText => Strings.Get("BringOver.Keep");

    /// <summary>
    /// Opens at the invitation, at the choice (the app was closed after Join), or finishes when nothing is pending. The
    /// page calls it when it appears (and for a new link while open); the page dispatches that event to the main thread.
    /// Nothing escapes: a failure shows the message with Try again.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            await StartCoreAsync();
        }
#pragma warning disable CA1031 // The page calls this from an event; a failure is a message with Try again, never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            ShowMessage(Strings.Get("Family.ActionFailed"), Retry.Start);
        }
    }

    private async Task StartCoreAsync()
    {
        switch (await _join.StageAsync(CancellationToken.None))
        {
            case JoinStage.Choice:
                await DecideAsync();
                break;
            case JoinStage.Invitation:
                if (await _join.NeedsSignInAsync(CancellationToken.None))
                {
                    // The code from the link shows already, before sign-in: the person sees which invitation this is.
                    var code = await _join.SuggestedCodeAsync(CancellationToken.None);
                    CodeLine = code is null ? "" : Strings.Format("Join.CodeLine", code.Display);
                    Step = JoinStep.SignIn;
                }
                else
                    await PreviewAsync();
                break;
            default:
                Finished?.Invoke(this, "");
                break;
        }
    }

    /// <summary>A link whose path holds no code (spec §4.1). The page calls it from its link event and dispatches to the main thread.</summary>
    public void ShowMalformed()
    {
        ShowMessage(Strings.Error("invitation.invalid"), Retry.None);
        _malformedShown = true;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        var failure = "Account.SignInFailed";
        try
        {
            var result = await _signIn.ExecuteAsync(CancellationToken.None);
            if (result == SignInResult.SignedIn)
            {
                // Signed in: anything that goes wrong from here is the preview's, not the sign-in's.
                failure = "Family.ActionFailed";
                await PreviewAsync();
            }
            else if (result == SignInResult.Failed)
            {
                Toast = Strings.Get(failure);
            }
        }
#pragma warning disable CA1031 // A sign-in or preview that throws is "didn't work", never a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get(failure);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NotNowAsync()
    {
        // Not now while a join is under way would forget the code the accept is using.
        if (IsBusy)
            return;
        try
        {
            // A link without a code is not the invitation that may be pending: that one stays (final review minor).
            if (!_malformedShown)
                await _join.DeclineAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Not now only forgets a code; the sheet closes regardless.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        Finished?.Invoke(this, "");
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        if (IsBusy || _preview is not { } preview)
            return;
        IsBusy = true;
        try
        {
            await JoinCoreAsync(preview);
        }
#pragma warning disable CA1031 // Nothing was joined that the stored marker does not keep; say it did not work and let the person try again.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            switch (_retry)
            {
                case Retry.Preview:
                    await PreviewAsync();
                    break;
                case Retry.Accept when _preview is { } preview:
                    await AcceptAsync(preview, consentAsked: false);
                    break;
                case Retry.Join when _preview is { } preview:
                    await JoinCoreAsync(preview);
                    break;
                case Retry.Decide:
                    await DecideAsync();
                    break;
                case Retry.Start:
                    await StartCoreAsync();
                    break;
            }
        }
#pragma warning disable CA1031 // Try again that fails again keeps its message and says so.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// "Keep them separate" from the not-ready state (the family's log cannot be pulled yet): the join ends, the joining marker
    /// is cleared, and the person is never stuck. Nothing is copied or deleted.
    /// </summary>
    [RelayCommand]
    private async Task KeepSeparateAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            await _join.KeepSeparateAsync(CancellationToken.None);
            Finished?.Invoke(this, Strings.Format("Join.Joined", _inviterName));
        }
#pragma warning disable CA1031 // The marker stays on a failure; the flow opens again at the choice.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Ok() => Finished?.Invoke(this, "");

    private async Task PreviewAsync()
    {
        var result = await _join.PreviewAsync(CancellationToken.None);
        switch (result.Status)
        {
            case PreviewStatus.Ready:
                (_preview, _code) = (result.Invitation!, result.Code);
                Title = Strings.Format("Join.Title", _preview.InviterName);
                Body = Strings.Format("Join.Body", _preview.InviterName);
                Babies = _preview.BabyNames.Count == 0 ? Strings.Get("Join.NoBaby") : Strings.Format("Join.Babies", string.Join(", ", _preview.BabyNames));
                Step = JoinStep.Invitation;
                break;
            case PreviewStatus.Joined:
                // An accept whose answer was lost (final review C2): the join is stored; on to the choice.
                await DecideAfterJoinAsync(result.Invitation?.InviterName ?? "");
                break;
            case PreviewStatus.AlreadyMember:
                ShowMessage(Strings.Get("Join.AlreadyMember"), Retry.None);
                break;
            case PreviewStatus.Invalid:
                ShowMessage(Strings.Error("invitation.invalid"), Retry.None);
                break;
            case PreviewStatus.RateLimited:
                ShowMessage(Strings.Error("auth.rate-limited"), Retry.None);
                break;
            case PreviewStatus.Unavailable:
                ShowMessage(Strings.Get("Family.ActionFailed"), Retry.Preview);
                break;
            default:
                Finished?.Invoke(this, "");
                break;
        }
    }

    /// <summary>Consent if the account has none (the host's sheet), then the accept.</summary>
    private async Task JoinCoreAsync(InvitationPreview preview)
    {
        var asked = false;
        if (!await _join.HasConsentAsync(CancellationToken.None))
        {
            switch (await _consent.AskAsync())
            {
                case ConsentAnswer.Declined:
                    await DeclineForConsentAsync(preview);
                    return;
                case ConsentAnswer.NotAsked:
                    ShowMessage(Strings.Get("Family.ActionFailed"), Retry.Join);
                    return;
            }

            asked = true;
        }

        await AcceptAsync(preview, consentAsked: asked);
    }

    private async Task AcceptAsync(InvitationPreview preview, bool consentAsked)
    {
        var accepted = await _join.AcceptAsync(_code!, preview.InviterName, CancellationToken.None);
        switch (accepted.Status)
        {
            case AcceptStatus.Joined:
                await DecideAfterJoinAsync(preview.InviterName);
                break;
            case AcceptStatus.AlreadyMember:
                ShowMessage(Strings.Get("Join.AlreadyMember"), Retry.None);
                break;
            case AcceptStatus.NeedsConsent when consentAsked:
                await DeclineForConsentAsync(preview);
                break;
            case AcceptStatus.NeedsConsent:
                switch (await _consent.AskAsync())
                {
                    case ConsentAnswer.Given:
                        await AcceptAsync(preview, consentAsked: true);
                        break;
                    case ConsentAnswer.Declined:
                        await DeclineForConsentAsync(preview);
                        break;
                    default:
                        // The sheet could not be shown: the invitation stays; Try again asks again (review I1).
                        ShowMessage(Strings.Get("Family.ActionFailed"), Retry.Join);
                        break;
                }

                break;
            case AcceptStatus.Changed:
                // A newer link replaced the code: show that invitation instead (review I2).
                (_preview, _code) = (null, null);
                try
                {
                    await StartCoreAsync();
                }
#pragma warning disable CA1031 // The old invitation is gone from the sheet: a failure is a message with Try again, which starts over.
                catch (Exception)
#pragma warning restore CA1031
                {
                    ShowMessage(Strings.Get("Family.ActionFailed"), Retry.Start);
                }

                break;
            case AcceptStatus.Invalid:
                ShowMessage(Strings.Error("invitation.invalid"), Retry.None);
                break;
            case AcceptStatus.RateLimited:
                ShowMessage(Strings.Error("auth.rate-limited"), Retry.None);
                break;
            case AcceptStatus.Unavailable:
                ShowMessage(Strings.Get("Family.ActionFailed"), Retry.Accept);
                break;
            default:
                Finished?.Invoke(this, "");
                break;
        }
    }

    /// <summary>After a join is stored: the choice. A failed choice is not a failed join: Try again reopens it, Keep separate never strands the person.</summary>
    private async Task DecideAfterJoinAsync(string inviterName)
    {
        try
        {
            await DecideAsync();
        }
#pragma warning disable CA1031 // The join is stored; the marker reopens the choice.
        catch (Exception)
#pragma warning restore CA1031
        {
            _inviterName = inviterName;
            ShowMessage(Strings.Get("Family.ActionFailed"), Retry.Decide);
            // The choice failed, so whether this phone has a baby is unknown: Try again only, never a second baby
            // (final review; Keep separate stays reachable from NotReady, where the answer is known).
            CanKeepSeparate = false;
        }
    }

    private async Task DecideAsync()
    {
        var choice = await _join.DecideAsync(CancellationToken.None);
        switch (choice.Status)
        {
            case ChoiceStatus.NothingAsked:
                Finished?.Invoke(this, choice.InviterName.Length == 0 ? "" : Strings.Format("Join.Joined", choice.InviterName));
                break;
            case ChoiceStatus.NotReady:
                _inviterName = choice.InviterName;
                ShowMessage(Strings.Get("Family.RefreshFailed"), Retry.Decide);

                // With no baby yet the baby step shows: keeping them separate there would add a second baby (final review minor).
                CanKeepSeparate = choice.ShownHasBaby;
                break;
            default:
                BringOver.Start(new BringOverRequest(
                    choice.Source,
                    choice.Offer!,
                    BringOverRequest.FamilyLineFor(choice.Offer!),
                    FamilyLabels.In(choice.InviterName),
                    _join.BringAsync,
                    _join.KeepSeparateAsync,
                    Strings.Format("Join.Joined", choice.InviterName)));
                (CanRetry, CanKeepSeparate) = (false, false);
                Step = JoinStep.Choice;
                break;
        }
    }

    private async Task DeclineForConsentAsync(InvitationPreview preview)
    {
        await _join.DeclineAsync(CancellationToken.None);
        ShowMessage(Strings.Format("Join.Later", FamilyLabels.Until(preview.ExpiresAt, _clock.Is24Hour)), Retry.None);
    }

    partial void OnStepChanged(JoinStep value)
    {
        if (value != JoinStep.Message)
            _malformedShown = false;
    }

    private void ShowMessage(string message, Retry retry)
    {
        (Message, _retry, CanRetry, CanKeepSeparate, _malformedShown) = (message, retry, retry != Retry.None, false, false);
        Step = JoinStep.Message;
    }
}
