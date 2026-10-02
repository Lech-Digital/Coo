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
        var callback = new Callback();
        using var signal = new CancellationSignal();
        using var registration = cancellationToken.Register(signal.Cancel);

        // Anything thrown while opening the sheet is the port's SignInUnavailableException, never a raw Java exception.
        try
        {
            var option = new GetGoogleIdOption.Builder()
                .SetServerClientId(webClientId)
                .SetFilterByAuthorizedAccounts(false)
                .SetNonce(nonce)
                .Build();
            var request = new GetCredentialRequest.Builder().AddCredentialOption(option).Build();

            // Context.MainExecutor needs API 28; Coo's minimum is 26, hence ContextCompat (Spec 010a §5).
            var executor = ContextCompat.GetMainExecutor(activity) ?? throw new SignInUnavailableException("There is no main executor.");
            CredentialManager.Create(activity).GetCredentialAsync(activity, request, signal, executor, callback);
        }
        catch (Exception e) when (e is not SignInUnavailableException and not System.OperationCanceledException)
        {
            throw new SignInUnavailableException("The account sheet could not be opened: " + e.GetType().Name, e);
        }

        return await callback.Result;
    }

    private sealed class Callback : Java.Lang.Object, ICredentialManagerCallback
    {
        private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> Result => _result.Task;

        // Both run on the main thread inside a Java callback: no managed exception may escape (an escape crashes the app).
        public void OnResult(Java.Lang.Object? result)
        {
            try
            {
                var credential = result?.JavaCast<GetCredentialResponse>()?.Credential;
                if (credential is not null && credential.Type == GoogleIdTokenCredential.TypeGoogleIdTokenCredential)
                    _result.TrySetResult(GoogleIdTokenCredential.CreateFrom(credential.Data).IdToken);
                else
                    _result.TrySetException(new SignInUnavailableException("The account sheet returned no Google ID token."));
            }
            catch (Exception e)
            {
                _result.TrySetException(new SignInUnavailableException("The account sheet's answer could not be read: " + e.GetType().Name, e));
            }
        }

        // The error is a Java exception object: read through its class name, not cast to Throwable (Spec 010a §5).
        public void OnError(Java.Lang.Object? error)
        {
            try
            {
                var name = error?.Class.Name;
                if (name == CancelledError)
                    _result.TrySetResult(null);
                else
                    _result.TrySetException(new SignInUnavailableException(name ?? "The account sheet failed."));
            }
            catch (Exception e)
            {
                _result.TrySetException(new SignInUnavailableException("The account sheet failed: " + e.GetType().Name, e));
            }
        }
    }
}
