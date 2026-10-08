using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Domain.Common;
using Coo.Domain.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

public sealed record BabyOption(BabyId? Target, string Label);

/// <summary>One local baby and where it goes: a family baby, or "Add as a new baby" (the last option).</summary>
public sealed partial class BabyMapping(LocalBaby local, IReadOnlyList<BabyOption> options, int selected) : ObservableObject
{
    [ObservableProperty]
    private int _selected = selected;

    public LocalBaby Local => local;

    public IReadOnlyList<BabyOption> Options => options;

    public string Prompt => Strings.Format("BringOver.Which", local.Name);
}

/// <summary>
/// "This phone has 42 entries for Maja. Your family's log has: Maja." (Spec 011 §4). Bring my entries over asks where
/// each local baby goes, then one transaction moves everything; Keep them separate closes and changes nothing (Q4).
/// </summary>
public sealed partial class BringOverViewModel(BringEntriesOver bringOver) : ObservableObject
{
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _family = "";

    [ObservableProperty]
    private IReadOnlyList<BabyMapping> _mappings = [];

    [ObservableProperty]
    private bool _isMapping;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public event EventHandler? Closed;

    public static string BringText => Strings.Get("BringOver.Bring");

    public static string KeepText => Strings.Get("BringOver.Keep");

    public static string ConfirmText => Strings.Get("BringOver.Confirm");

    public async Task LoadAsync()
    {
        BringOverChoice? choice = null;
        try
        {
            choice = await bringOver.PrepareAsync(CancellationToken.None);
        }
        catch (Exception)
        {
        }

        // Null: nothing to offer, or the household had synced; the use case completed the join itself, so no toast.
        if (choice is null)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Summary = BabyMappings.Summary(choice);
        Family = choice.Family.Count == 0
            ? Strings.Get("BringOver.FamilyEmpty")
            : Strings.Format("BringOver.Family", string.Join(", ", choice.Family.Select(b => b.Name)));
        Mappings = BabyMappings.For(choice);
    }

    [RelayCommand]
    private async Task BringAsync()
    {
        if (Mappings.Count == 0)
            await ConfirmAsync();
        else
            IsMapping = true;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Toast = "";
        try
        {
            var mapping = Mappings.ToDictionary(m => m.Local.Id, m => m.Options[Math.Clamp(m.Selected, 0, m.Options.Count - 1)].Target);
            switch (await bringOver.ExecuteAsync(mapping, CancellationToken.None))
            {
                case BringOverResult.Done:
                case BringOverResult.MovedButNotCompleted: // The move stands; the next sign-in finds the phone already moved. Claim nothing.
                    Closed?.Invoke(this, EventArgs.Empty);
                    break;
                default:
                    Toast = Strings.Get("BringOver.Failed");
                    // Signed out meanwhile: there is nothing left to ask. Prepare is null exactly when nothing is pending.
                    if (await bringOver.PrepareAsync(CancellationToken.None) is null)
                        Closed?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
#pragma warning disable CA1031 // The transaction rolled back (Spec 011 §4): say so; never crash the sheet.
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
    private void Keep() => Closed?.Invoke(this, EventArgs.Empty);
}
