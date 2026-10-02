using Coo.Api.Application.Auth;
using Coo.Api.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Coo.Api.IntegrationTests;

/// <summary>Spec 010 §4.2: a failed clean-up waits for the next day; it never stops the API.</summary>
public sealed class AuthNonceCleanupJobTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly IAuthNonceStore _nonces = Substitute.For<IAuthNonceStore>();

    [Fact]
    public async Task ExecuteAsync_PurgeCancelledWhileTheApiRuns_KeepsRunningAndPurgesTheNextDay()
    {
        // Arrange: a database command timing out surfaces as a cancellation nobody asked for.
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nonces.DeleteIssuedBeforeAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(
            _ =>
            {
                first.TrySetResult();
                return Task.FromException<int>(new OperationCanceledException("The command timed out."));
            },
            _ =>
            {
                second.TrySetResult();
                return Task.FromResult(3);
            });
        using var job = new AuthNonceCleanupJob(new PurgeAuthNonces(_nonces, _time), _time, NullLogger<AuthNonceCleanupJob>.Instance);
        await job.StartAsync(CancellationToken.None);

        // Act
        await AdvanceUntilAsync(job, first.Task, TimeSpan.FromMinutes(15));
        await AdvanceUntilAsync(job, second.Task, TimeSpan.FromDays(1));

        // Assert
        job.ExecuteTask!.IsCompleted.Should().BeFalse("the job ended as {0}", job.ExecuteTask.Status);
        second.Task.IsCompleted.Should().BeTrue();
        await job.StopAsync(CancellationToken.None);
        job.ExecuteTask.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_PurgeThrows_KeepsRunningAndPurgesTheNextDay()
    {
        // Arrange
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nonces.DeleteIssuedBeforeAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromException<int>(new InvalidOperationException("The database is down.")),
            _ =>
            {
                second.TrySetResult();
                return Task.FromResult(0);
            });
        using var job = new AuthNonceCleanupJob(new PurgeAuthNonces(_nonces, _time), _time, NullLogger<AuthNonceCleanupJob>.Instance);
        await job.StartAsync(CancellationToken.None);

        // Act
        await AdvanceUntilAsync(job, second.Task, TimeSpan.FromHours(6));

        // Assert
        job.ExecuteTask!.IsCompleted.Should().BeFalse("the job ended as {0}", job.ExecuteTask.Status);
        second.Task.IsCompleted.Should().BeTrue();
        await job.StopAsync(CancellationToken.None);
        job.ExecuteTask.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_WhileWaiting_EndsWithoutAFault()
    {
        // Arrange
        using var job = new AuthNonceCleanupJob(new PurgeAuthNonces(_nonces, _time), _time, NullLogger<AuthNonceCleanupJob>.Instance);
        await job.StartAsync(CancellationToken.None);

        // Act
        await job.StopAsync(CancellationToken.None);

        // Assert
        job.ExecuteTask!.IsFaulted.Should().BeFalse();
        await _nonces.DidNotReceiveWithAnyArgs().DeleteIssuedBeforeAsync(default, default);
    }

    /// <summary>
    /// Moves the fake clock in steps until <paramref name="reached"/> completes or the job ends; the job's next timer may
    /// not exist yet when a step is taken.
    /// </summary>
    private async Task AdvanceUntilAsync(AuthNonceCleanupJob job, Task reached, TimeSpan step)
    {
        using var deadline = new CancellationTokenSource(Patience);
        while (!reached.IsCompleted && job.ExecuteTask is { IsCompleted: false })
        {
            deadline.Token.ThrowIfCancellationRequested();
            _time.Advance(step);
            await Task.WhenAny(reached, Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token));
        }
    }
}
