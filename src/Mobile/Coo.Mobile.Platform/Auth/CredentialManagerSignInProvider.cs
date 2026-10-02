using Android.App;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.Content;
using AndroidX.Credentials;
using Coo.Mobile.Application.Auth;
using Xamarin.GoogleAndroid.Libraries.Identity.GoogleId;

namespace Coo.Mobile.Platform.Auth;

/// <summary>
/// Android's own Google account sheet through Credential Manager (ADR 0003 §2), as Spec 010a proved it in a trimmed
/// Release build on the Pixel 10: a Google ID token whose audience is Coo's Web client and whose nonce is the server's.
/// </summary>
internal sealed class CredentialManagerSignInProvider(Func<Activity?> currentActivity, string webClientId) : ISignInProvider
{
    private const string CancelledError = "androidx.credentials.exceptions.GetCredentialCancellationException";

    public async Task<string?> GetIdTokenAsync(string nonce, CancellationToken cancellationToken)
    {
        var activity = currentActivity() ?? throw new SignInUnavailableException("There is no activity to show the account sheet on.");
        var option = new GetGoogleIdOption.Builder()
            .SetServerClientId(webClientId)
            .SetFilterByAuthorizedAccounts(false)
            .SetNonce(nonce)
            .Build();
        var request = new GetCredentialRequest.Builder().AddCredentialOption(option).Build();
        var callback = new Callback();
        using var signal = new CancellationSignal();
        using var registration = cancellationToken.Register(signal.Cancel);

        // Context.MainExecutor needs API 28; Coo's minimum is 26, hence ContextCompat (Spec 010a §5).
        CredentialManager.Create(activity).GetCredentialAsync(activity, request, signal, ContextCompat.GetMainExecutor(activity)!, callback);
        return await callback.Result;
    }

    private sealed class Callback : Java.Lang.Object, ICredentialManagerCallback
    {
        private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> Result => _result.Task;

        public void OnResult(Java.Lang.Object? result)
        {
            var credential = result?.JavaCast<GetCredentialResponse>()?.Credential;
            if (credential is not null && credential.Type == GoogleIdTokenCredential.TypeGoogleIdTokenCredential)
                _result.TrySetResult(GoogleIdTokenCredential.CreateFrom(credential.Data).IdToken);
            else
                _result.TrySetException(new SignInUnavailableException("The account sheet returned no Google ID token."));
        }

        // The error is a Java exception object: read through its class name, not cast to Throwable (Spec 010a §5).
        public void OnError(Java.Lang.Object? error)
        {
            if (error?.Class.Name == CancelledError)
                _result.TrySetResult(null);
            else
                _result.TrySetException(new SignInUnavailableException(error?.Class.Name + ": " + error));
        }
    }
}
