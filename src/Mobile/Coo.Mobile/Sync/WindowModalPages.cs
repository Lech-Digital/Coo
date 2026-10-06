using Coo.Mobile.Presentation.Sheets;

namespace Coo.Mobile.Sync;

/// <summary>
/// The window's modal stack, reached through <c>Window.Navigation</c> and never through the Shell (walk fix F1). Pushed through
/// the Shell, a sheet's push is a route navigation and its own <c>PopModalAsync</c> becomes <c>Shell.GoToAsync("..")</c>, which
/// pops whatever the Shell last recorded on top; with two navigations under way at once (a sheet's Disappearing starts the next
/// sheet during its own pop) that record and the window's stack part, and a sheet's OK or Back popped nothing. Main thread.
/// </summary>
internal sealed class WindowModalPages : IModalPages
{
    public IReadOnlyList<object> Pages => Window()?.Navigation.ModalStack is { } stack ? [.. stack] : [];

    public Task PushAsync(object page) =>
        (Window() ?? throw new InvalidOperationException("No window to show a sheet in.")).Navigation.PushModalAsync((Page)page);

    public Task PopAsync() => Window()?.Navigation.PopModalAsync() ?? Task.CompletedTask;

    private static Window? Window() =>
        Microsoft.Maui.Controls.Application.Current?.Windows is { Count: > 0 } windows ? windows[0] : null;
}
