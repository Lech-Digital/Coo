using CommunityToolkit.Mvvm.ComponentModel;
using Coo.Domain.Common;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Today;

/// <summary>
/// Today's banner (Spec 011b §8.2): only while the shown family is soft-locked and is the only family on the phone; logging
/// carries on (§15.2 Q6). It rechecks when the engine's status changes (a cycle soft-locks or lifts) and when a different
/// family is shown; <see cref="Update"/> runs every second, so it does not ask the store each time. Text changes are raised on
/// whichever thread the check finished on: the section's view dispatches to the main thread.
/// </summary>
public sealed partial class PausedSection : ObservableObject, IHomeSection, IDisposable
{
    private readonly Families _families;
    private readonly SemaphoreSlim _one = new(1, 1);
    private HouseholdId? _checkedFor;

    [ObservableProperty]
    private string _text = "";

    public PausedSection(Families families, ISyncStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _families = families;
        status.StatusChanged += (_, _) => _ = RefreshAsync();
    }

    public int Order => 5;

    public void Dispose() => _one.Dispose();

    public bool AppliesTo(HomeStage stage, PaletteKind palette) => true;

    public void Update(HomeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = RefreshForAsync(snapshot.Household.Id);
    }

    /// <summary>Checks once per shown family (what <see cref="Update"/> calls); a status change checks again through <see cref="RefreshAsync"/>.</summary>
    public async Task RefreshForAsync(HouseholdId shown)
    {
        if (_checkedFor == shown)
            return;
        _checkedFor = shown;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            // One check at a time, so a slow earlier answer cannot overwrite a later one.
            await _one.WaitAsync();
            try
            {
                Text = await _families.IsShownPausedAloneAsync(CancellationToken.None) ? Strings.Get("Today.Paused") : "";
            }
            finally
            {
                _one.Release();
            }
        }
#pragma warning disable CA1031 // Fire-and-forget: the banner keeps what it showed; the next status change checks again.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
