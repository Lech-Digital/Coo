using Coo.Mobile.Application.Sync;
using NSubstitute;

namespace Coo.Mobile.Presentation.Tests;

/// <summary>An <see cref="ISyncControl"/> substitute whose exclusive section runs its work at once (no cycle to wait for).</summary>
internal static class SyncControls
{
    public static ISyncControl PassThrough()
    {
        var sync = Substitute.For<ISyncControl>();
        sync.RunExclusiveAsync(default!, default).ReturnsForAnyArgs(c => c.Arg<Func<CancellationToken, Task>>()(c.Arg<CancellationToken>()));
        return sync;
    }
}
