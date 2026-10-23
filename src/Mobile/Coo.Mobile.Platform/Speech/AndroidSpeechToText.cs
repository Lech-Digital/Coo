using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Speech;
using Coo.Mobile.Application.AiLogging;
using Microsoft.Maui.ApplicationModel;

namespace Coo.Mobile.Platform.Speech;

/// <summary>
/// Dictation through Android's <see cref="SpeechRecognizer"/> (ADR 0005 §2), the microphone asked for on first use (never
/// at launch), on-device recognition preferred. Audio stays with Android's recogniser: Coo never records or sends it, only
/// the resulting text. Never throws: any failure is null.
/// </summary>
internal sealed class AndroidSpeechToText : ISpeechToText
{
    private static Context Context => global::Android.App.Application.Context;

    private ListeningSession? _current;

    public bool IsAvailable => SpeechRecognizer.IsRecognitionAvailable(Context);

    public async Task<string?> ListenAsync(string language, Action<string>? onHeard, CancellationToken cancellationToken, IReadOnlyList<string>? hints = null)
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

            var session = new ListeningSession(language, onHeard, hints ?? []);
            _current = session;
            using var registration = cancellationToken.Register(session.Cancel);
            return await session.RunAsync();
        }
#pragma warning disable CA1031 // A denied permission or a platform quirk must not break Today; the person can type instead.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    public void Finish() => _current?.Finish();

    /// <summary>
    /// One "Tell Coo", as long as it takes: Android's recogniser ends a session at the first pause, so each one that ends is
    /// started again and the words are joined, until the person lets go or taps, or stops talking for
    /// <see cref="QuietAfterSpeech"/>. Before any words it waits <see cref="WaitForSpeech"/>: a parent picking up a baby
    /// does not start talking at once. A missing offline language pack fails at once on some phones: then Android's online
    /// recogniser is used. Everything runs on the main thread, where Android calls the listener.
    /// </summary>
    private sealed class ListeningSession(string language, Action<string>? onHeard, IReadOnlyList<string> hints) : Java.Lang.Object, IRecognitionListener
    {
        private const long WaitForSpeech = 20_000;
        private const long LongestSession = 90_000;
        private const long QuietAfterSpeech = 3_000;
        private const long FinishGrace = 1_500;
        private const long RestartDelay = 120;

        private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Handler _main = new(Looper.MainLooper!);
        private readonly List<string> _finals = [];
        private SpeechRecognizer? _recognizer;
        private string _partial = "";
        private bool _offline = true;
        private bool _finishing;
        private long _startedAt;
        private int _quietTimer;

        private string Heard => string.Join(' ', _partial.Length > 0 ? [.. _finals, _partial] : _finals).Trim();

        public Task<string?> RunAsync()
        {
            MainThread.BeginInvokeOnMainThread(Start);
            return _done.Task;
        }

        /// <summary>Let go or tapped: what was heard so far is the answer, once Android hands over the last words.</summary>
        public void Finish() => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_done.Task.IsCompleted || _finishing)
                return;
            _finishing = true;
            Try(() => _recognizer?.StopListening());
            _main.PostDelayed(() => Complete(Heard), FinishGrace);
        });

        public void Cancel() => MainThread.BeginInvokeOnMainThread(() => Complete(null));

        private void Start()
        {
            try
            {
                _recognizer = SpeechRecognizer.CreateSpeechRecognizer(Context);
                if (_recognizer is null || _done.Task.IsCompleted)
                {
                    Complete(null);
                    return;
                }

                _recognizer.SetRecognitionListener(this);
                _startedAt = SystemClock.ElapsedRealtime();
                Begin();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                Complete(null);
            }
        }

        private void Begin()
        {
            if (_done.Task.IsCompleted || _recognizer is null)
                return;
            var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
            intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
            intent.PutExtra(RecognizerIntent.ExtraLanguage, Java.Util.Locale.ForLanguageTag(language).ToLanguageTag());
            intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
            // On the device where it can be (ADR 0005 §2); online when this phone has no offline pack for the language.
            intent.PutExtra(RecognizerIntent.ExtraPreferOffline, _offline);
            intent.PutExtra(RecognizerIntent.ExtraSpeechInputCompleteSilenceLengthMillis, 2500);
            intent.PutExtra(RecognizerIntent.ExtraSpeechInputPossiblyCompleteSilenceLengthMillis, 2000);
            // Android 13+: the baby's name and the care words, so "Lena, left side" is not heard as "Lana lift side".
            if (OperatingSystem.IsAndroidVersionAtLeast(33) && hints.Count > 0)
                intent.PutStringArrayListExtra(RecognizerIntent.ExtraBiasingStrings, [.. hints]);
            try
            {
                _recognizer.StartListening(intent);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                Complete(Heard);
            }
        }

        private void Restart() => _main.PostDelayed(Begin, RestartDelay);

        private long Elapsed => SystemClock.ElapsedRealtime() - _startedAt;

        private void Complete(string? result)
        {
            if (!_done.TrySetResult(string.IsNullOrWhiteSpace(result) ? null : result))
                return;
            _main.RemoveCallbacksAndMessages(null);
            if (_recognizer is { } recognizer)
            {
                _recognizer = null;
                Try(recognizer.Cancel);
                Try(recognizer.Destroy);
                Try(recognizer.Dispose);
            }
        }

        // After words, a quiet spell ends it; any new words restart the count.
        private void ArmQuietTimer()
        {
            var timer = ++_quietTimer;
            _main.PostDelayed(() =>
            {
                if (timer == _quietTimer && !_finishing)
                    Complete(Heard);
            }, QuietAfterSpeech);
        }

        public void OnPartialResults(Bundle? partialResults)
        {
            var phrase = partialResults?.GetStringArrayList(SpeechRecognizer.ResultsRecognition) is { Count: > 0 } list ? list[0] : null;
            if (string.IsNullOrWhiteSpace(phrase))
                return;
            _partial = phrase;
            _quietTimer++;
            onHeard?.Invoke(Heard);
        }

        public void OnResults(Bundle? results)
        {
            var phrase = results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition) is { Count: > 0 } list ? list[0] : _partial;
            if (!string.IsNullOrWhiteSpace(phrase))
                _finals.Add(phrase);
            _partial = "";
            onHeard?.Invoke(Heard);

            if (_finishing || Elapsed > LongestSession)
            {
                Complete(Heard);
                return;
            }

            if (_finals.Count > 0)
                ArmQuietTimer();
            Restart();
        }

        public void OnError([GeneratedEnum] SpeechRecognizerError error)
        {
            if (_finishing)
            {
                Complete(Heard);
                return;
            }

            switch ((int)error)
            {
                // No offline pack for this language (12, 13), or the on-device service gave up (11): go online.
                case 11 or 12 or 13 when _offline:
                    _offline = false;
                    Restart();
                    return;

                // Silence or nothing understood (7, 6), or the recogniser still closing the last session (5, 8).
                case 5 or 6 or 7 or 8:
                    if (Heard.Length > 0)
                    {
                        // Words then silence: they are the answer, unless the quiet timer is still waiting for more.
                        ArmQuietTimer();
                        Restart();
                    }
                    else if (Elapsed < WaitForSpeech)
                    {
                        Restart();
                    }
                    else
                    {
                        Complete(null);
                    }

                    return;

                default:
                    Complete(Heard);
                    return;
            }
        }

        public void OnReadyForSpeech(Bundle? @params)
        {
        }

        public void OnBeginningOfSpeech() => _quietTimer++;

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

        private static void Try(Action action)
        {
#pragma warning disable CA1031 // Teardown: a throw here would turn a clean exit into a crash.
            try { action(); } catch (Exception) { }
#pragma warning restore CA1031
        }
    }
}
