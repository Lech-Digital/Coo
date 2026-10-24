using Coo.Contracts.AiParsing;
using Coo.Mobile.Application.Households;

namespace Coo.Mobile.Application.AiLogging;

/// <param name="Text">What was said; empty when nothing was or on an error.</param>
/// <param name="ErrorCode">ai.voice-daily-limit, ai.voice-free-limit, ai.signed-out, ai.unavailable, or ai.no-baby.</param>
public sealed record VoiceText(string Text, string? ErrorCode = null, int? Limit = null);

/// <summary>
/// A recording to text for Tell Coo or the Advisor, for the shown family. Not kept anywhere: an unreadable or failed
/// recording is simply said again or typed.
/// </summary>
public sealed class TranscribeVoice(IHouseholdStore households, IAiParseApi api)
{
    public const string ForLog = "log";
    public const string ForAdvisor = "advisor";

    public async Task<VoiceText> ExecuteAsync(
        VoiceRecording recording, string purpose, string language, IReadOnlyList<string> hints, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var (household, _) = await PrepareQuickLog.ShownAsync(households, cancellationToken);
        if (household is null)
            return new VoiceText("", "ai.no-baby");
        var request = new AiTranscribeRequest(
            household.Id.Value, Convert.ToBase64String(recording.Audio), recording.MimeType, household.TimeZoneId, language, hints, purpose);
        AiTranscribeResponse? response;
        try
        {
            response = await api.TranscribeAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response = null;
        }

        return response is null ? new VoiceText("", "ai.unavailable") : new VoiceText(response.Text ?? "", response.ErrorCode, response.Limit);
    }
}
