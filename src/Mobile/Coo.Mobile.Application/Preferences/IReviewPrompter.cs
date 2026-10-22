namespace Coo.Mobile.Application.Preferences;

/// <summary>Asks for a Play review when <see cref="AppVersion.ReviewPolicy"/> says the moment is right.</summary>
public interface IReviewPrompter
{
    /// <summary>Called right after a log was saved.</summary>
    Task CheckAndPromptAsync(CancellationToken cancellationToken);
}
