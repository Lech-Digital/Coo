using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Coo.Api.Application.AiParsing;
using Coo.Domain.CareLog.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coo.Api.Infrastructure.Ai;

/// <summary>
/// Gemini behind <see cref="ICareLogTextParser"/> (ADR 0005 §5–6): a pinned model, temperature 0 with a fixed seed, and a
/// response schema, so the model extracts and never invents structure. It does no time arithmetic: times come back as
/// expressions that deterministic code resolves.
/// </summary>
internal sealed partial class GeminiCareLogParser(IConfiguration configuration, ILogger<GeminiCareLogParser> logger) : ICareLogTextParser
{
    /// <summary>Bumped with every change to <see cref="SystemPrompt"/> or the schema: every parse reports the version it used.</summary>
    public const string PromptVersion = "coo-parse-v1";

    /// <summary>Pinned, never a "-latest" alias: an alias could change the model under a fixed prompt (ADR 0005 §6).</summary>
    public const string DefaultModel = "gemini-3.5-flash-lite";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Care-log parse failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);

    internal const string SystemPrompt = """
        You turn what a parent says or types about their baby into care-log entries for the app Coo.
        The text can be in any language (often English, Polish, Dutch, German, French, Spanish or Italian) and may mix them.
        Names have been replaced: the baby is "Baby A"; parents are "Caregiver 1", "Caregiver 2".

        Entry types:
        - BreastStart: a breast feed starts now or at a time ("started on the left", "she's latching on the right"). Needs side.
        - BreastSwitch: the running breast feed continues on the other side ("switched", "other side now").
        - BreastStop: the running breast feed ends ("done feeding", "she's finished").
        - BreastFeeding: a finished breast feed with minutes per side ("fed 10 minutes left and 5 right at 3").
        - BottleFeeding: a bottle with an amount; milk is "formula" or "breast" (expressed milk) if said.
        - Diaper: contents "wet", "dirty" (poo) or "mixed" (both).
        - SleepStart: the baby falls asleep now or at a time. SleepEnd: the running sleep ends ("she woke up").
        - Sleep: a finished sleep with a start and an end ("napped from 1 to 2:30").
        - PumpStart, PumpStop (with amount if said), Pumping: a finished pumping session with an amount.

        Rules:
        - Use the context to resolve short phrases: "other side" is the side opposite the last one; "woke up" ends the
          running sleep; "done" ends whatever is running.
        - Times: "now" for something happening now; "clock" with hh and mm (24-hour) for a stated time; "minutesAgo" with n
          for "20 minutes ago" or "half an hour ago" (n=30); "unspecified" when no time is given for a past event.
          Never compute dates yourself.
        - For a finished Sleep or BreastFeeding, put the start in time and the end in endTime when given.
        - Amounts: the number as said, with unit "ml" or "oz" if said, otherwise leave unit empty.
        - If the parent corrects themselves ("no wait, 100"), use the correction only.
        - sourceText: the exact words this entry came from.
        - Anything that is not a care event, or that you cannot map, goes to unparsed. Never invent entries.
        """;

    private static readonly object TimeSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            kind = new { type = "STRING", @enum = new[] { "now", "clock", "minutesAgo", "unspecified" } },
            hh = new { type = "INTEGER" },
            mm = new { type = "INTEGER" },
            n = new { type = "INTEGER" },
        },
        required = new[] { "kind" },
    };

    private static readonly object ResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            entries = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        type = new
                        {
                            type = "STRING",
                            @enum = new[]
                            {
                                "BreastStart", "BreastSwitch", "BreastStop", "BreastFeeding", "BottleFeeding", "Diaper",
                                "SleepStart", "SleepEnd", "Sleep", "PumpStart", "PumpStop", "Pumping",
                            },
                        },
                        side = new { type = "STRING", @enum = new[] { "left", "right" } },
                        leftMinutes = new { type = "INTEGER" },
                        rightMinutes = new { type = "INTEGER" },
                        amount = new { type = "NUMBER" },
                        unit = new { type = "STRING", @enum = new[] { "ml", "oz" } },
                        milk = new { type = "STRING", @enum = new[] { "formula", "breast" } },
                        contents = new { type = "STRING", @enum = new[] { "wet", "dirty", "mixed" } },
                        time = TimeSchema,
                        endTime = TimeSchema,
                        sourceText = new { type = "STRING" },
                    },
                    required = new[] { "type", "time", "sourceText" },
                },
            },
            unparsed = new { type = "ARRAY", items = new { type = "STRING" } },
        },
        required = new[] { "entries", "unparsed" },
    };

    public async Task<ParsedText?> ParseAsync(ParsePrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var apiKey = configuration["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return null;
        var model = configuration["AiParsing:Model"] ?? DefaultModel;

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = $"Context:\n{prompt.Context}\nText:\n{prompt.Text}" } } } },
            generationConfig = new { temperature = 0.0, seed = 7, responseMimeType = "application/json", responseSchema = ResponseSchema },
        };

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"))
            {
                Content = JsonContent.Create(body),
            };
            message.Headers.Add("x-goog-api-key", apiKey);
            using var response = await Http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed(logger, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var text = envelope.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            return text is null ? null : Read(text);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException
                                       or TaskCanceledException { InnerException: TimeoutException })
        {
            // The type only: the exception text could quote the model's answer, which quotes the parent.
            LogFailed(logger, e.GetType().Name);
            return null;
        }
    }

    /// <summary>The model's JSON as untrusted input: unknown shapes are skipped, never guessed.</summary>
    internal static ParsedText Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var entries = new List<ParsedEntry>();
        if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || Str(e, "type") is not { } type || !e.TryGetProperty("time", out var time))
                    continue;
                entries.Add(new ParsedEntry(
                    type,
                    Str(e, "side"),
                    Int(e, "leftMinutes"),
                    Int(e, "rightMinutes"),
                    e.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number ? amount.GetDecimal() : null,
                    Str(e, "unit"),
                    Str(e, "milk"),
                    Str(e, "contents"),
                    Time(time),
                    e.TryGetProperty("endTime", out var end) ? Time(end) : null,
                    Str(e, "sourceText") ?? ""));
            }
        }

        var unparsed = root.TryGetProperty("unparsed", out var rest) && rest.ValueKind == JsonValueKind.Array
            ? rest.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).Where(u => u.Length > 0).ToList()
            : [];
        return new ParsedText(entries, unparsed, PromptVersion);
    }

    private static TimeExpression Time(JsonElement time) => Str(time, "kind") switch
    {
        "now" => TimeExpression.Now,
        "clock" when Int(time, "hh") is { } hh && Int(time, "mm") is var mm => TimeExpression.Clock(hh, mm ?? 0),
        "minutesAgo" when Int(time, "n") is { } n => TimeExpression.MinutesAgo(n),
        _ => TimeExpression.Unspecified,
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(p.GetDouble(), MidpointRounding.AwayFromZero)
            : null;
}

/// <summary>
/// Stands in for Gemini without a key (Development, tests), so the whole flow runs end to end (ADR 0005 §7). It knows a
/// few English phrases; anything else is unparsed.
/// </summary>
internal sealed class FakeCareLogParser : ICareLogTextParser
{
    public Task<ParsedText?> ParseAsync(ParsePrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var text = prompt.Text.ToLower(CultureInfo.InvariantCulture);
        ParsedEntry Entry(string type, string? side = null, decimal? amount = null, string? contents = null) =>
            new(type, side, null, null, amount, null, null, contents, TimeExpression.Now, null, prompt.Text);

        ParsedEntry? entry =
            text.Contains("left", StringComparison.Ordinal) ? Entry("BreastStart", side: "left")
            : text.Contains("right", StringComparison.Ordinal) ? Entry("BreastStart", side: "right")
            : text.Contains("woke", StringComparison.Ordinal) ? Entry("SleepEnd")
            : text.Contains("asleep", StringComparison.Ordinal) ? Entry("SleepStart")
            : text.Contains("wet", StringComparison.Ordinal) ? Entry("Diaper", contents: "wet")
            : text.Contains("ml", StringComparison.Ordinal) && decimal.TryParse(new string([.. text.Where(char.IsDigit)]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ml)
                ? Entry("BottleFeeding", amount: ml)
            : null;
        return Task.FromResult<ParsedText?>(entry is null
            ? new ParsedText([], [prompt.Text], "fake")
            : new ParsedText([entry], [], "fake"));
    }
}
