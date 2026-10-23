using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Speech;
using Coo.Mobile.Application.AiLogging;
using Microsoft.Maui.ApplicationModel;

namespace Coo.Mobile.Platform.Speech;

/// <summary>
/// Dictation through Android's <see cref="SpeechRecognizer"/> (ADR 0005 §2), after CoreChoice's AndroidVoiceDictation: one
/// session per tap, the microphone asked for on first use (never at launch), on-device recognition preferred. Audio stays
/// with Android's recogniser: Coo never records or sends it, only the resulting text. Never throws: any failure is null.
/// </summary>
internal sealed class AndroidSpeechToText : ISpeechToText
{
    private static Context Context => global::Android.App.Application.Context;

    public bool IsAvailable => SpeechRecognizer.IsRecognitionAvailable(Context);

    public async Task<string?> ListenAsync(string language, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            return null;

        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted || cancellationToken.IsCancellationRequested)
                return null;

            return await ListenOnMainThreadAsync(language, cancellationToken);
        }
#pragma warning disable CA1031 // A denied permission or a platform quirk must not break Today; the person can type instead.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static Task<string?> ListenOnMainThreadAsync(string language, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>();
        var session = new RecognizerSession();

        // One exit for every outcome, so the recogniser is torn down exactly once and the microphone always closes.
        void Complete(string? result)
        {
            completion.TrySetResult(result);
            session.Close();
        }

        var registration = cancellationToken.Register(() => Complete(null));
        completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            SpeechRecognizer? recognizer = null;
            try
            {
                recognizer = SpeechRecognizer.CreateSpeechRecognizer(Context);
                if (recognizer is null)
                {
                    Complete(null);
                    return;
                }

                // Cancelled before the main thread got here: destroy it now, never start it.
                if (!session.TryAdopt(recognizer))
                {
                    RecognizerSession.Release(recognizer);
                    return;
                }

                recognizer.SetRecognitionListener(new OneShotListener(Complete));
                var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
                intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
                intent.PutExtra(RecognizerIntent.ExtraLanguage, Java.Util.Locale.ForLanguageTag(language).ToLanguageTag());
                // On the device where it can be (ADR 0005 §2); Android falls back to its online service otherwise.
                intent.PutExtra(RecognizerIntent.ExtraPreferOffline, true);
                // A pause in a tired sentence ("Lena ate… 120 ml") should not end it.
                intent.PutExtra(RecognizerIntent.ExtraSpeechInputCompleteSilenceLengthMillis, 2500);
                recognizer.StartListening(intent);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                if (recognizer is not null)
                    RecognizerSession.Release(recognizer);
                Complete(null);
            }
        });

        return completion.Task;
    }

    /// <summary>Owns the one recogniser of a listening attempt and releases it exactly once, on the main thread.</summary>
    private sealed class RecognizerSession
    {
        private readonly Lock _gate = new();
        private SpeechRecognizer? _recognizer;
        private bool _closed;

        public bool TryAdopt(SpeechRecognizer recognizer)
        {
            lock (_gate)
            {
                if (_closed)
                    return false;
                _recognizer = recognizer;
                return true;
            }
        }

        public void Close()
        {
            SpeechRecognizer? toRelease;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                toRelease = _recognizer;
                _recognizer = null;
            }

            if (toRelease is not null)
                MainThread.BeginInvokeOnMainThread(() => Release(toRelease));
        }

        public static void Release(SpeechRecognizer recognizer)
        {
#pragma warning disable CA1031 // Teardown: a throw here would turn a clean exit into a crash.
            try { recognizer.Cancel(); } catch (Exception) { }
            try { recognizer.Destroy(); } catch (Exception) { }
            try { recognizer.Dispose(); } catch (Exception) { }
#pragma warning restore CA1031
        }
    }

    /// <summary>Reports exactly one outcome: the best phrase, or null on silence, cancellation or error.</summary>
    private sealed class OneShotListener(Action<string?> onDone) : Java.Lang.Object, IRecognitionListener
    {
        private bool _done;

        private void Complete(string? result)
        {
            if (_done)
                return;
            _done = true;
            onDone(result);
        }

        public void OnResults(Bundle? results)
        {
            var phrases = results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
            Complete(phrases is { Count: > 0 } ? phrases[0] : null);
        }

        public void OnError([GeneratedEnum] SpeechRecognizerError error) => Complete(null);

        public void OnPartialResults(Bundle? partialResults)
        {
        }

        public void OnReadyForSpeech(Bundle? @params)
        {
        }

        public void OnBeginningOfSpeech()
        {
        }

        public void OnRmsChanged(float rmsdB)
        {
        }

        public void OnBufferReceived(byte[]? buffer)
        {
        }

        public void OnEndOfSpeech()
        {
        }

        public void OnEvent(int eventType, Bundle? @params)
        {
        }
    }
}
