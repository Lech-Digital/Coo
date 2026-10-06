using Coo.Mobile.Presentation.Sheets;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>
/// The sheets' one way on and off the window's modal stack (walk fix F1): a sheet closes only itself, a push asked for while a pop
/// is still running decides against the stack that pop leaves, and a step Android never finished does not block the sheets.
/// </summary>
public sealed class SheetNavigatorTests
{
    private readonly FakeModalPages _stack = new();
    private readonly SheetNavigator _navigator;

    public SheetNavigatorTests() => _navigator = new SheetNavigator(_stack);

    [Fact]
    public async Task CloseAsync_ThePageIsOnTop_PopsItAndSaysClosed()
    {
        // Arrange
        var sheet = new object();
        _stack.Pages.Add(sheet);

        // Act
        var result = await _navigator.CloseAsync(sheet);

        // Assert
        result.Should().Be(SheetClose.Closed);
        _stack.Pages.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseAsync_AnotherSheetIsOverIt_PopsNothingAndSaysCovered()
    {
        // Arrange
        var join = new object();
        var consent = new object();
        _stack.Pages.AddRange([join, consent]);

        // Act
        var result = await _navigator.CloseAsync(join);

        // Assert
        result.Should().Be(SheetClose.Covered);
        _stack.Pages.Should().Equal(join, consent);
    }

    [Fact]
    public async Task CloseAsync_ThePageIsNotOnTheStack_PopsNothingAndSaysGone()
    {
        // Arrange
        var other = new object();
        _stack.Pages.Add(other);

        // Act
        var result = await _navigator.CloseAsync(new object());

        // Assert
        result.Should().Be(SheetClose.Gone);
        _stack.Pages.Should().Equal(other);
    }

    [Fact]
    public async Task PushAsync_AskedWhileAPopIsStillRunning_DecidesOnceThePopHasFinished()
    {
        // Arrange: the code sheet's pop is in its animation when its Disappearing asks for the join sheet (the walk's step 1).
        var code = new object();
        _stack.Pages.Add(code);
        _stack.HoldNextStep();
        var closing = _navigator.CloseAsync(code);
        IReadOnlyList<object>? seen = null;

        // Act
        var pushing = _navigator.PushAsync(stack =>
        {
            seen = [.. stack];
            return new object();
        });
        var decidedEarly = seen is not null;
        _stack.ReleaseStep();
        await closing;
        var pushed = await pushing;

        // Assert
        decidedEarly.Should().BeFalse();
        seen.Should().BeEmpty();
        pushed.Should().BeTrue();
        _stack.Pages.Should().ContainSingle().Which.Should().NotBeSameAs(code);
    }

    [Fact]
    public async Task PushAsync_TheGuardRefuses_PushesNothing()
    {
        // Arrange
        var join = new object();
        _stack.Pages.Add(join);

        // Act
        var pushed = await _navigator.PushAsync(stack => stack.Contains(join) ? null : new object());

        // Assert
        pushed.Should().BeFalse();
        _stack.Pages.Should().Equal(join);
        _stack.Pushes.Should().Be(0);
    }

    [Fact]
    public async Task PushAsync_TwoJoinSheetsAskedTogether_OnlyOneIsPushed()
    {
        // Arrange: a link and the code sheet's handoff both ask for the join sheet while the first push is still animating.
        object? join = null;
        object? Create(IReadOnlyList<object> stack) => join is not null && stack.Contains(join) ? null : join = new object();
        _stack.HoldNextStep();

        // Act
        var first = _navigator.PushAsync(Create);
        var second = _navigator.PushAsync(Create);
        _stack.ReleaseStep();
        var results = await Task.WhenAll(first, second);

        // Assert
        results.Should().Equal(true, false);
        _stack.Pages.Should().ContainSingle();
    }

    [Fact]
    public async Task CloseAsync_AskedWhileANewerSheetIsBeingPushed_DoesNotPopTheNewerSheet()
    {
        // Arrange: OK is tapped on the message while the next sheet is on its way over it.
        var message = new object();
        var newer = new object();
        _stack.Pages.Add(message);
        _stack.HoldNextStep();
        var pushing = _navigator.PushAsync(_ => newer);

        // Act
        var closing = _navigator.CloseAsync(message);
        _stack.ReleaseStep();
        await pushing;
        var result = await closing;

        // Assert
        result.Should().Be(SheetClose.Covered);
        _stack.Pages.Should().Equal(message, newer);
    }

    [Fact]
    public async Task Reset_AStepNeverFinishes_TheNextStepRuns()
    {
        // Arrange: an animation Android cut short never completes its push.
        _stack.HoldNextStep();
        _ = _navigator.PushAsync(_ => new object());
        var sheet = new object();

        // Act
        _navigator.Reset();
        var pushed = await _navigator.PushAsync(_ => sheet);

        // Assert
        pushed.Should().BeTrue();
        _stack.Pages.Should().Contain(sheet);
    }

    [Fact]
    public async Task Reset_TwoStepsQueuedBehindAStuckOne_TheSecondStillWaitsForTheFirst()
    {
        // Arrange: a push never finishes; a link and the code sheet's handoff each queue a join sheet behind it (review I1).
        _stack.HoldNextStep();
        _ = _navigator.PushAsync(_ => new object());
        var secondStep = _stack.HoldNextStep();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstJoin = _navigator.PushAsync(_ =>
        {
            firstStarted.TrySetResult();
            return new object();
        });
        var secondDecided = false;
        var secondJoin = _navigator.PushAsync(_ =>
        {
            secondDecided = true;
            return null;
        });

        // Act
        _navigator.Reset();
        await firstStarted.Task;
        for (var i = 0; i < 20 && !secondDecided; i++)
            await Task.Run(() => { });
        var decidedBeforeTheFirstFinished = secondDecided;
        secondStep.TrySetResult();
        await firstJoin;
        await secondJoin;

        // Assert
        decidedBeforeTheFirstFinished.Should().BeFalse("only the stuck step is let go; the queue keeps its order");
        secondDecided.Should().BeTrue();
    }

    [Fact]
    public async Task PushAsync_ThePushThrows_TheNextStepStillRuns()
    {
        // Arrange
        _stack.FailNextStep();
        Func<Task> failing = async () => await _navigator.PushAsync(_ => new object());
        await failing.Should().ThrowAsync<InvalidOperationException>();
        var sheet = new object();

        // Act
        var pushed = await _navigator.PushAsync(_ => sheet);

        // Assert
        pushed.Should().BeTrue();
        _stack.Pages.Should().Equal(sheet);
    }

    [Fact]
    public void IsOnTop_ThePageIsUnderAnother_IsFalse()
    {
        // Arrange
        var join = new object();
        var consent = new object();
        _stack.Pages.AddRange([join, consent]);

        // Act
        var joinOnTop = _navigator.IsOnTop(join);
        var consentOnTop = _navigator.IsOnTop(consent);

        // Assert
        joinOnTop.Should().BeFalse();
        consentOnTop.Should().BeTrue();
        _navigator.Contains(join).Should().BeTrue();
    }

    /// <summary>The window's modal stack: the next step can be held (an animation running) or made to fail (the platform refused it).</summary>
    private sealed class FakeModalPages : IModalPages
    {
        private readonly Queue<TaskCompletionSource> _holds = new();
        private readonly List<TaskCompletionSource> _all = [];
        private bool _failNext;

        public List<object> Pages { get; } = [];

        public int Pushes { get; private set; }

        IReadOnlyList<object> IModalPages.Pages => Pages;

        /// <summary>The next step not yet held waits for this gate (steps are held in the order they run).</summary>
        public TaskCompletionSource HoldNextStep()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _holds.Enqueue(gate);
            _all.Add(gate);
            return gate;
        }

        public void ReleaseStep()
        {
            foreach (var gate in _all)
                gate.TrySetResult();
        }

        public void FailNextStep() => _failNext = true;

        public async Task PushAsync(object page)
        {
            Refuse();
            Pushes++;
            Pages.Add(page);
            await HoldAsync();
        }

        public async Task PopAsync()
        {
            Refuse();
            Pages.RemoveAt(Pages.Count - 1);
            await HoldAsync();
        }

        private void Refuse()
        {
            if (!_failNext)
                return;
            _failNext = false;
            throw new InvalidOperationException("The platform refused the step.");
        }

        private async Task HoldAsync()
        {
            if (_holds.TryDequeue(out var gate))
                await gate.Task;
        }
    }
}
