namespace Coo.Mobile.Application.Preferences;

/// <summary>Whether this phone has seen "How Coo works", the tour shown at the first start. Per device, never synced.</summary>
public interface ITourState
{
    Task<bool> HasSeenAsync(CancellationToken cancellationToken);

    Task MarkSeenAsync(CancellationToken cancellationToken);
}
