using Coo.Mobile.Application.Auth;

namespace Coo.Mobile.Auth;

/// <summary>
/// MAUI <c>SecureStorage</c>, backed by the Android Keystore (Spec 010 §3.3). It lives in the host because
/// <c>Coo.Mobile.Platform</c> has no MAUI reference (the deviation of Spec 009 §6, recorded in Spec 010 §10). A Keystore
/// that can no longer decrypt (restored to another phone, reset) reads as "signed out", never as a crash.
/// </summary>
internal sealed class MauiSecretStore : ISecretStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            try
            {
                SecureStorage.Default.Remove(key);
            }
            catch (Exception)
            {
                // The Keystore is beyond reading and removing: still "signed out", never a crash.
            }

            return null;
        }
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken) => SecureStorage.Default.SetAsync(key, value);

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}
