using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Presentation.Tour;

/// <summary>One tip of the tour: its kind's colour ("brand" for the matcha accent) and icon (null shows Mochi), and its strings' key.</summary>
public sealed record TourStep(string Key, string KindCode, string? Icon);

/// <summary>
/// "How Mochi works": a few screens on what Mochi does and where it is, shown once at the first start (over the welcome
/// screen) and again from Settings. Skip at the top, previous and next tip at the bottom (owner, 2026-11-03). Redone on
/// 2026-11-06 to explain it better (owner): in the order a parent meets things, each tip saying where it is ("Today ·
/// Next up") and what it does in two or three short points, including how Mochi learns the baby's rhythm.
/// </summary>
public sealed partial class TourViewModel(ITourState state) : ObservableObject
{
    public static IReadOnlyList<TourStep> Steps { get; } =
    [
        new("Welcome", "feeding", null),
        new("Today", "feeding", IconGeometry.Feed),
        new("Learns", "brand", IconGeometry.Sparkle),
        new("Night", "sleep", IconGeometry.Sleep),
        new("Log", "diaper", IconGeometry.Log),
        new("Stats", "pump", IconGeometry.Chart),
        new("Faster", "feeding", IconGeometry.Mic),
        new("Family", "brand", IconGeometry.Family),
        new("Advisor", "sleep", IconGeometry.Sparkle),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Step), nameof(Title), nameof(Body), nameof(Where), nameof(Points), nameof(Position), nameof(CanGoBack), nameof(IsLast), nameof(NextLabel))]
    private int _index;

    /// <summary>Opened from Settings: the last button reads "Done" instead of "Start using Mochi".</summary>
    public bool IsReplay { get; set; }

    /// <summary>Skipped or finished; the page closes.</summary>
    public event EventHandler? Finished;

    public TourStep Step => Steps[Index];

    public string Title => Strings.Get($"Tour.{Step.Key}.Title");

    public string Body => Strings.Get($"Tour.{Step.Key}.Body");

    /// <summary>Where it is in the app ("Today · Next up"); empty for a tip without a place (the welcome).</summary>
    public string Where => Strings.Get($"Tour.{Step.Key}.Where") is var where && !where.StartsWith("Tour.", StringComparison.Ordinal) ? where : "";

    /// <summary>The body's points, one per line.</summary>
    public IReadOnlyList<string> Points => [.. Body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

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
