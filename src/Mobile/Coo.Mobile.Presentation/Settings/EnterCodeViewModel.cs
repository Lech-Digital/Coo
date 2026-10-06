using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Settings;

/// <summary>"Join a family" · "Enter the code from your invitation." (spec §4.4). A malformed code is refused without a request.</summary>
public sealed partial class EnterCodeViewModel(JoinFamily join) : ObservableObject
{
    [ObservableProperty]
    private string _code = "";

    [ObservableProperty]
    private string _error = "";

    /// <summary>The code is the pending invitation now: the host opens the join flow.</summary>
    public event EventHandler? Accepted;

    public static string Title => Strings.Get("Family.Join");

    public static string Hint => Strings.Get("Join.EnterHint");

    public static string ContinueText => Strings.Get("Setup.Continue");

    [RelayCommand]
    private async Task ContinueAsync()
    {
        try
        {
            if (await join.ReceiveAsync(Code, CancellationToken.None))
            {
                Error = "";
                Accepted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Error = Strings.Get("Join.Malformed");
            }
        }
#pragma warning disable CA1031 // Storing the code failed (disk): say it did not work, never crash the page.
        catch (Exception)
#pragma warning restore CA1031
        {
            Error = Strings.Get("Family.ActionFailed");
        }
    }
}
