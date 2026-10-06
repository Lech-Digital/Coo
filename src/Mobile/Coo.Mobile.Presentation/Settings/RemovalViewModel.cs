using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>
/// "You're no longer in this family" (Spec 011b §8.2), one removal at a time (<see cref="SyncStatus.Removal"/>). Nothing is
/// deleted before OK. Share them as text sends the entries that were never shared, one line each, oldest first, and leaves the
/// sheet open; Back closes it without deleting (the host opens it again at the next start or resume). The acknowledgment goes
/// through <see cref="ISyncControl"/>, which the engine implements. Every command ends in a catch-all; <see cref="Closed"/>
/// is raised on the caller's thread (the page's command), so the page may close the sheet directly.
/// </summary>
public sealed partial class RemovalViewModel(
    ISyncStatus status, ISyncControl sync, Families families, IExternalLauncher launcher, IClockFormat clock, TimeProvider time) : ObservableObject
{
    [ObservableProperty]
    private string _body = "";

    [ObservableProperty]
    private string _unshared = "";

    [ObservableProperty]
    private IReadOnlyList<string> _lines = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _toast = "";

    public event EventHandler? Closed;

    public static string Title => Strings.Get("Removal.Title");

    public static string ShareText => Strings.Get("Removal.Share");

    public static string OkText => Strings.Get("Join.OK");

    /// <summary>Reads the family being removed from the status. The page calls it when the sheet opens, on the UI thread.</summary>
    public async Task LoadAsync()
    {
        if (status.Status.Removal is not { } household)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            var contents = await families.ContentsAsync(household, CancellationToken.None);
            Body = contents.BabyNames.Count == 0
                ? Strings.Get("Removal.BodyNoBaby")
                : Strings.Format("Removal.Body", string.Join(", ", contents.BabyNames));
            var entries = contents.Unshared.OfType<LiveEntry>().OrderBy(e => e.Start).ToList();
            Unshared = entries.Count == 0 ? "" : Strings.Plural("Removal.Unshared", entries.Count);
            var formats = new Formats(clock.Is24Hour, contents.Unit);
            var now = time.GetLocalNow();
            Lines = [.. entries.Select(e =>
            {
                var line = Strings.Format("Removal.Line", e.Start.ToLocalTime().ToString("ddd d MMM", Strings.Culture), formats.Time(e.Start), formats.Title(e.Entry, now));
                return e.Entry.Note is { } note ? line + " · " + note.Text : line;
            })];
        }
#pragma warning disable CA1031 // The family cannot be read: OK must still work, so the sheet shows the copy that names nobody.
        catch (Exception)
#pragma warning restore CA1031
        {
            Body = Strings.Get("Removal.BodyNoBaby");
            Unshared = "";
            Lines = [];
        }
    }

    [RelayCommand]
    private async Task ShareAsync()
    {
        if (Lines.Count == 0)
            return;
        try
        {
            await launcher.ShareTextAsync(string.Join("\n", Lines), Title);
        }
#pragma warning disable CA1031 // The share sheet refusing must not close or break the removal sheet.
        catch (Exception)
#pragma warning restore CA1031
        {
            Toast = Strings.Get("Family.ActionFailed");
        }
    }

    [RelayCommand]
    private async Task OkAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await sync.AcknowledgeRemovalAsync(CancellationToken.None);
            Closed?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // Nothing was deleted (one transaction): say so; OK can be tapped again.
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
}
