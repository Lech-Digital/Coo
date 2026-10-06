using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Joining;

/// <summary>Spec 011 §3's sheet where the person is (inviting, joining), through the prompter so it is never shown twice.</summary>
internal sealed class MauiConsentPrompt(IServiceProvider services) : IConsentPrompt
{
    /// <summary>The prompter works on the main thread only; a flow that awaited off it is brought back.</summary>
    public Task<bool> AskAsync()
    {
        var prompter = services.GetRequiredService<Sync.SyncPrompter>();
        return MainThread.IsMainThread ? prompter.AskConsentAsync() : MainThread.InvokeOnMainThreadAsync(prompter.AskConsentAsync);
    }
}
