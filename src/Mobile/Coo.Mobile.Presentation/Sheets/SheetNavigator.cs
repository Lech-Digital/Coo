namespace Coo.Mobile.Presentation.Sheets;

/// <summary>
/// The window's own modal stack, never the Shell's (walk fix F1). The host adapts MAUI's <c>Window.Navigation</c>: through the
/// Shell a modal push is a route navigation and a page's pop is <c>GoToAsync("..")</c>, a pop of whatever the Shell last
/// recorded on top rather than of that page.
/// </summary>
public interface IModalPages
{
    /// <summary>Bottom first.</summary>
    IReadOnlyList<object> Pages { get; }

    Task PushAsync(object page);

    /// <summary>Pops the page on top.</summary>
    Task PopAsync();
}

public enum SheetClose
{
    /// <summary>It was on top and is popped.</summary>
    Closed,

    /// <summary>Another sheet is over it (its consent): nothing was popped; it closes once it is on top again.</summary>
    Covered,

    /// <summary>It is not on the stack: nothing was popped.</summary>
    Gone,
}

/// <summary>
/// The one way sheets go on and off the window (walk fix F1). Steps run one after another, so a push asked for while a pop is still
/// running (a sheet's Disappearing fires during its own pop) decides against the stack that pop leaves, and a sheet only ever
/// closes itself. Main thread.
/// </summary>
public sealed class SheetNavigator(IModalPages pages)
{
    private Task _tail = Task.CompletedTask;
    private TaskCompletionSource? _running;

    /// <summary>The window's modal pages now, bottom first.</summary>
    public IReadOnlyList<object> Pages => pages.Pages;

    public bool IsOnTop(object page) => pages.Pages.Count > 0 && ReferenceEquals(pages.Pages[^1], page);

    public bool Contains(object page) => pages.Pages.Contains(page);

    /// <summary>
    /// Pushes the page <paramref name="create"/> builds from the stack as it is once the steps before have finished; a null page
    /// pushes nothing.
    /// </summary>
    /// <returns>Whether a page was pushed.</returns>
    public Task<bool> PushAsync(Func<IReadOnlyList<object>, object?> create) => RunAsync(async () =>
    {
        if (create(pages.Pages) is not { } page)
            return false;
        await pages.PushAsync(page);
        return true;
    });

    /// <summary>Pops the page only when it is on top once the steps before have finished: never another sheet.</summary>
    public Task<SheetClose> CloseAsync(object page) => RunAsync(async () =>
    {
        if (IsOnTop(page))
        {
            await pages.PopAsync();
            return SheetClose.Closed;
        }

        return Contains(page) ? SheetClose.Covered : SheetClose.Gone;
    });

    /// <summary>
    /// Coo left the screen: the step running now, which may wait for an animation Android never finishes, no longer holds back
    /// the next one; the stack itself tells what landed. Only that step is let go: the steps queued behind it still run one after
    /// another (review I1), so two join sheets never decide at once.
    /// </summary>
    public void Reset() => _running?.TrySetResult();

    private async Task<T> RunAsync<T>(Func<Task<T>> step)
    {
        var before = _tail;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tail = done.Task;
        try
        {
            await before;
            _running = done;
            return await step();
        }
        finally
        {
            done.TrySetResult();
            if (ReferenceEquals(_running, done))
                _running = null;
        }
    }
}
