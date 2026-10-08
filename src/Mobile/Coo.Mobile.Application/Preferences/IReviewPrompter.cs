namespace Coo.Mobile.Application.Preferences;

public interface IReviewPrompter
{
    Task CheckAndPromptAsync(CancellationToken cancellationToken);
    void MarkAsRated();
}
