using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <param name="Execute">Brings the entries over (the join's <c>JoinFamily.BringAsync</c>, or <c>BringFamilyEntriesOver.ExecuteAsync</c> from Settings).</param>
/// <param name="Keep">"Keep them separate" in the join flow; null in Settings, where Back cancels.</param>
/// <param name="DoneToast">What a move says when it is done.</param>
public sealed record BringOverRequest(
    HouseholdId Source, BringOverChoice Choice, string FamilyLine, string TargetName,
    Func<IReadOnlyDictionary<BabyId, BabyId?>, CancellationToken, Task<BringOutcome>> Execute, Func<CancellationToken, Task>? Keep, string DoneToast);

public enum BringOverStep
{
    Choice,
    Mapping,
    OldFamily,
}

/// <summary>
/// "This phone has 42 entries for Maja." → Bring my entries over (each baby mapped) or Keep them separate → after a copy,
/// the question about the old family (Spec 011b §6). Confirm is busy-guarded; the copy's marker makes a second one a no-op.
/// </summary>
public sealed partial class FamilyBringOverViewModel(LeaveFamily leave) : ObservableObject
{
    private BringOverRequest? _request;

    [ObservableProperty]
    private BringOverStep _step;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _familyLine = "";

    [ObservableProperty]
    private IReadOnlyList<BabyMapping> _mappings = [];

    [ObservableProperty]
    private bool _canKeep;

    [ObservableProperty]
    private string _question = "";

    [ObservableProperty]
    private string _questionNote = "";

    [ObservableProperty]
    private string _answerText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    /// <summary>The flow is over; the argument is the toast to say ("" for none).</summary>
    public event EventHandler<string>? Finished;

    public static string BringText => Strings.Get("BringOver.Bring");

    public static string KeepText => Strings.Get("BringOver.Keep");

    public static string ConfirmText => Strings.Get("BringOver.Confirm");

    public static string KeepOldText => Strings.Get("Join.KeepIt");

    /// <summary>Opens the flow at its choice. The host calls it from its own (main-thread) code; it raises no events.</summary>
    public void Start(BringOverRequest request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        var choice = request.Choice;
        Summary = BabyMappings.Summary(choice);
        FamilyLine = request.FamilyLine;
        Mappings = BabyMappings.For(choice);
        CanKeep = request.Keep is not null;
        (Question, QuestionNote, AnswerText, Toast) = ("", "", "", "");
        Step = BringOverStep.Choice;
    }

    [RelayCommand]
    private async Task BringAsync()
    {
        if (Mappings.Count == 0)
            await ConfirmAsync();
        else
            Step = BringOverStep.Mapping;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy || _request is not { } request)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var mapping = Mappings.ToDictionary(m => m.Local.Id, m => m.Options[Math.Clamp(m.Selected, 0, m.Options.Count - 1)].Target);
            var outcome = await request.Execute(mapping, CancellationToken.None);
            switch (outcome)
            {
                case { Path: BringPath.Copied, Question: OldFamilyQuestion.DeleteOld }:
                    Question = Strings.Plural("Join.DeleteOld", request.Choice.Local.EntryCount, request.TargetName,
                        string.Join(", ", request.Choice.Local.Babies.Select(b => b.Name)));
                    QuestionNote = outcome.Skipped > 0 ? Strings.Plural("Join.Skipped", outcome.Skipped) : "";
                    AnswerText = Strings.Get("Join.DeleteOldButton");
                    Step = BringOverStep.OldFamily;
                    break;
                case { Path: BringPath.Copied, Question: OldFamilyQuestion.LeaveOld }:
                    Question = Strings.Format("Join.LeaveOld", request.TargetName);
                    QuestionNote = "";
                    AnswerText = Strings.Get("Family.LeaveShort");
                    Step = BringOverStep.OldFamily;
                    break;
                case { Path: BringPath.Copied }:
                    Finished?.Invoke(this, Strings.Format("Join.Copied", request.TargetName));
                    break;
                default:
                    Finished?.Invoke(this, request.DoneToast);
                    break;
            }
        }
#pragma warning disable CA1031 // The transaction rolled back: say so; never crash the sheet.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("BringOver.Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task KeepAsync()
    {
        if (IsBusy || _request?.Keep is not { } keep)
            return;
        IsBusy = true;
        try
        {
            await keep(CancellationToken.None);
            Finished?.Invoke(this, Strings.Get("Join.BothFamilies"));
        }
#pragma warning disable CA1031 // Keeping only ends the flow; a failure leaves the joining marker, and the flow opens again.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("BringOver.Failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>"Delete old family" or "Leave": §8.3's leave with <c>confirm: true</c>.</summary>
    [RelayCommand]
    private async Task AnswerAsync()
    {
        if (IsBusy || _request is not { } request)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var action = await leave.ExecuteAsync(request.Source, confirm: true, CancellationToken.None);
            if (action.Succeeded)
                Finished?.Invoke(this, "");
            else
                Toast = action.Code is { } code ? Strings.Error(code) : Strings.Get("Family.ActionFailed");
        }
#pragma warning disable CA1031 // Say it did not work; the person may try again or keep the old family.
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
    private void KeepOld() => Finished?.Invoke(this, "");
}
