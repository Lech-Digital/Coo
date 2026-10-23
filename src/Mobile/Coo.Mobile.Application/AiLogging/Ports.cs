using Coo.Contracts.AiParsing;

namespace Coo.Mobile.Application.AiLogging;

/// <summary>The server's <c>/api/ai/parse</c> (ADR 0005). Null when it could not be reached: the words become a pending note.</summary>
public interface IAiParseApi
{
    Task<AiParseResponse?> ParseAsync(AiParseRequest request, CancellationToken cancellationToken);
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
    Task<string?> ListenAsync(string language, Action<string>? onHeard, CancellationToken cancellationToken);

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
