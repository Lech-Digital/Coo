using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Tour;

/// <summary>One tip of the tour: its kind's colour and icon (null shows Coo's mark), and its strings' key.</summary>
public sealed record TourStep(string Key, string KindCode, string? Icon);

/// <summary>
/// "How Coo works": a few screens on what Coo does and where it is, in the store listing's order and words, shown once at the first start (over the welcome
/// screen) and again from Settings. Skip at the top, previous and next tip at the bottom (owner, 2026-11-03).
/// </summary>
public sealed partial class TourViewModel(ITourState state) : ObservableObject
{
    public static IReadOnlyList<TourStep> Steps { get; } =
    [
        new("Welcome", "feeding", null),
        new("Feeding", "feeding", IconGeometry.Feed),
        new("Sleep", "sleep", IconGeometry.Sleep),
        new("Diapers", "diaper", IconGeometry.Diaper),
        new("Growth", "growth", IconGeometry.Growth),
        new("Stats", "pump", IconGeometry.Chart),
        new("LogFaster", "sleep", IconGeometry.Mic),
        new("Family", "feeding", IconGeometry.Family),
        new("Advisor", "sleep", IconGeometry.Sparkle),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Step), nameof(Title), nameof(Body), nameof(Position), nameof(CanGoBack), nameof(IsLast), nameof(NextLabel))]
    private int _index;

    /// <summary>Opened from Settings: the last button reads "Done" instead of "Start using Coo".</summary>
    public bool IsReplay { get; set; }

    /// <summary>Skipped or finished; the page closes.</summary>
    public event EventHandler? Finished;

    public TourStep Step => Steps[Index];

    public string Title => Strings.Get($"Tour.{Step.Key}.Title");

    public string Body => Strings.Get($"Tour.{Step.Key}.Body");

    public string Position => Strings.Format("Tour.Position", Index + 1, Steps.Count);

    public bool CanGoBack => Index > 0;

    public bool IsLast => Index == Steps.Count - 1;

    public string NextLabel => !IsLast ? Strings.Get("Tour.Next") : Strings.Get(IsReplay ? "Tour.Done" : "Tour.Start");

    [RelayCommand]
    private Task Next()
    {
        if (IsLast)
            return FinishAsync();
        Index++;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Previous()
    {
        if (CanGoBack)
            Index--;
    }

    [RelayCommand]
    private Task Skip() => FinishAsync();

    private async Task FinishAsync()
    {
        await state.MarkSeenAsync(CancellationToken.None);
        Finished?.Invoke(this, EventArgs.Empty);
    }
}
