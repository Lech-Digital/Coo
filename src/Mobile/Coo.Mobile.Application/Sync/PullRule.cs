using Coo.Domain.Common.Versioning;

namespace Coo.Mobile.Application.Sync;

/// <summary>
/// ADR 0002 §6 on the phone: a pulled record replaces the local one unless the local one has a pending change with a
/// higher version; the next push lets the server settle it with the same rule.
/// </summary>
public static class PullRule
{
    /// <param name="pendingLocalVersion">The local record's version when it has an outbox row; null when nothing is pending.</param>
    public static bool KeepsLocal(HlcStamp? pendingLocalVersion, HlcStamp incoming) =>
        pendingLocalVersion is { } local && local > incoming;
}
