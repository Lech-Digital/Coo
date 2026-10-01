namespace Coo.Api.Application.Health;

/// <summary>Whether the database answers, for <c>/health</c> (Spec 008 §5).</summary>
public interface IDatabaseProbe
{
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
}
