using Coo.Domain.Common;
using Coo.Domain.Common.Versioning;
using Coo.Mobile.Application.Sync;

namespace Coo.Mobile.Application.Tests;

/// <summary>ADR 0002 §6: a pulled record replaces the local one unless a pending local change is newer.</summary>
public sealed class PullRuleTests
{
    private static readonly DeviceId Phone = new(Guid.Parse("7a1c0000-0000-7000-8000-0000000000d1"));

    [Theory]
    [InlineData(null, 5, false)]
    [InlineData(4L, 5, false)]
    [InlineData(5L, 5, false)]
    [InlineData(6L, 5, true)]
    public void KeepsLocal_PendingVersionAgainstIncoming_KeepsOnlyANewerPendingEdit(long? pending, long incoming, bool keeps)
    {
        // Act
        var result = PullRule.KeepsLocal(pending is { } p ? new HlcStamp(p, 0, Phone) : null, new HlcStamp(incoming, 0, Phone));

        // Assert
        result.Should().Be(keeps);
    }
}
