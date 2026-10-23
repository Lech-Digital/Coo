using Coo.Contracts.AiParsing;

namespace Coo.Mobile.Application.AiLogging;

/// <summary>The server's <c>/api/ai/parse</c> (ADR 0005). Null when it could not be reached: the words become a pending note.</summary>
public interface IAiParseApi
{
    Task<AiParseResponse?> ParseAsync(AiParseRequest request, CancellationToken cancellationToken);

    /// <summary>A photo of a health record to proposals; null when the server could not be reached.</summary>
    Task<AiParseResponse?> ParseImageAsync(AiImageRequest request, CancellationToken cancellationToken);
}

/// <summary>On-device dictation (ADR 0005 §2): audio never leaves the phone, only the text does.</summary>
public interface ISpeechToText
{
    bool IsAvailable { get; }

    /// <summary>
    /// Listens until <see cref="Finish"/>, or until the person stops talking; everything heard, or null on silence,
    /// refusal or error. Cancelling drops what was heard.
    /// </summary>
    /// <param name="language">The app's language, two letters, so Polish is heard as Polish.</param>
    /// <param name="onHeard">The words so far, as they come, on the main thread.</param>
    /// <param name="hints">Words to expect (the baby's name, "left", "bottle"): recognised more readily where Android can.</param>
    Task<string?> ListenAsync(string language, Action<string>? onHeard, CancellationToken cancellationToken, IReadOnlyList<string>? hints = null);

    /// <summary>The person let go of the button or tapped it: stop listening and keep what was heard.</summary>
    void Finish();
}

/// <summary>Words that could not be parsed yet (offline, server busy): kept on this phone until reviewed (ADR 0005 §3).</summary>
public sealed record PendingNote(Guid Id, string Text, DateTimeOffset CapturedAt);

public interface IPendingNoteStore
{
    Task<IReadOnlyList<PendingNote>> ListAsync(CancellationToken cancellationToken);

    Task AddAsync(PendingNote note, CancellationToken cancellationToken);

    Task RemoveAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>The language this phone's parent speaks to Tell Coo in, when it is not the app's; per device, never synced.</summary>
public interface ISpeechLanguagePreference
{
    /// <returns>A two-letter code, or null for the app's language.</returns>
    Task<string?> GetAsync(CancellationToken cancellationToken);

    Task SetAsync(string? language, CancellationToken cancellationToken);
}
