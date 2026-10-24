using Android.Media;
using Android.OS;
using Coo.Mobile.Application.AiLogging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Coo.Mobile.Platform.Speech;

/// <summary>
/// The microphone through <see cref="MediaRecorder"/>: mono AAC at 16 kHz and 32 kbit/s (about 4 kB a second), in an
/// ADTS file Gemini reads as <c>audio/aac</c>. The level is sampled five times a second: after the first words, a quiet
/// spell of <see cref="QuietAfterSpeech"/> ends it, as does <see cref="Longest"/>; with no words in
/// <see cref="WaitForSpeech"/> it gives up. The file is read into memory and deleted at once. Never throws: any failure
/// is null, and the person can type.
/// </summary>
internal sealed class AndroidVoiceRecorder : IVoiceRecorder
{
    private static readonly TimeSpan QuietAfterSpeech = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan WaitForSpeech = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Sample = TimeSpan.FromMilliseconds(200);

    // MediaRecorder's maximum amplitude is 0–32767: speech near a phone is well above this, a quiet room below.
    private const int SpeechLevel = 2_500;

    private volatile bool _finish;

    public bool IsAvailable => Android.App.Application.Context.PackageManager?.HasSystemFeature(Android.Content.PM.PackageManager.FeatureMicrophone) == true;

    public void Finish() => _finish = true;

    public async Task<VoiceRecording?> RecordAsync(CancellationToken cancellationToken)
    {
        _finish = false;
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted || cancellationToken.IsCancellationRequested)
                return null;
        }
#pragma warning disable CA1031 // A permission that cannot be asked: the person types instead.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        var path = Path.Combine(FileSystem.CacheDirectory, $"coo-voice-{Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(Java.Lang.JavaSystem.CurrentTimeMillis()))}.aac");
        MediaRecorder? recorder = null;
        try
        {
            recorder = OperatingSystem.IsAndroidVersionAtLeast(31) ? new MediaRecorder(Android.App.Application.Context) : new MediaRecorder();
            recorder.SetAudioSource(AudioSource.VoiceRecognition);
            recorder.SetOutputFormat(OutputFormat.AacAdts);
            recorder.SetAudioEncoder(AudioEncoder.Aac);
            recorder.SetAudioChannels(1);
            recorder.SetAudioSamplingRate(16_000);
            recorder.SetAudioEncodingBitRate(32_000);
            recorder.SetOutputFile(path);
            recorder.Prepare();
            recorder.Start();

            var started = SystemClock.ElapsedRealtime();
            long? lastSpeech = null;
            while (!cancellationToken.IsCancellationRequested && !_finish)
            {
                await Task.Delay(Sample, CancellationToken.None);
                var now = SystemClock.ElapsedRealtime();
                if (recorder.MaxAmplitude > SpeechLevel)
                    lastSpeech = now;
                var elapsed = TimeSpan.FromMilliseconds(now - started);
                if (elapsed >= Longest)
                    break;
                if (lastSpeech is { } spoke ? TimeSpan.FromMilliseconds(now - spoke) >= QuietAfterSpeech : elapsed >= WaitForSpeech)
                    break;
            }

            var length = TimeSpan.FromMilliseconds(SystemClock.ElapsedRealtime() - started);
            Stop(recorder);
            recorder = null;
            if (cancellationToken.IsCancellationRequested || (lastSpeech is null && !_finish))
                return null;
            var audio = await File.ReadAllBytesAsync(path, CancellationToken.None);
            return audio.Length < 256 ? null : new VoiceRecording(audio, "audio/aac", length);
        }
#pragma warning disable CA1031 // A microphone busy with a call, or a recorder that refuses: the person types instead.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Android.Util.Log.Warn("Coo", $"Voice not recorded: {e.GetType().Name}");
            return null;
        }
        finally
        {
            if (recorder is not null)
                Stop(recorder);
            try
            {
                File.Delete(path);
            }
#pragma warning disable CA1031 // A cache file that stays is cleared with the cache.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private static void Stop(MediaRecorder recorder)
    {
#pragma warning disable CA1031 // Stop throws when nothing was recorded; either way the recorder goes.
        try { recorder.Stop(); } catch (Exception) { }
        try { recorder.Release(); } catch (Exception) { }
        try { recorder.Dispose(); } catch (Exception) { }
#pragma warning restore CA1031
    }
}
