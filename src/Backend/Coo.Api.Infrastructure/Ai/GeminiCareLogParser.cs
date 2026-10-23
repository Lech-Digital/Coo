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
    public const string PromptVersion = "coo-parse-v2";

    /// <summary>Pinned, never a "-latest" alias: an alias could change the model under a fixed prompt (ADR 0005 §6).</summary>
    public const string DefaultModel = "gemini-3.5-flash-lite";

    /// <summary>The photo reader's version, reported like <see cref="PromptVersion"/>.</summary>
    public const string ImagePromptVersion = "coo-image-v1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Reading a photo takes longer than a sentence, and still within the phone's 20 seconds.
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(18) };

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
        - Medicine: a medicine or supplement given to the baby ("gave her vitamin D", "2.5 ml paracetamol"). name is the
          medicine as said; dose is the amount as said, if any ("400 IU", "2.5 ml").
        - Temperature: a temperature reading; temperature is the number, temperatureUnit "c" or "f" if said.
        - Growth: a measurement; weight with weightUnit "kg", "g" or "lb"; length and head (circumference) with
          lengthUnit "cm" or "in". Only the values said.
        - Vaccine: a vaccination; name is the vaccine as said ("MMR", "the 8-week jabs").
        - Visit: a visit to a doctor, midwife or clinic; name is who, if said ("paediatrician").

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

    internal const string ImageSystemPrompt = """
        You read a photo of a baby's health record for the app Coo: a growth card, a clinic or midwife leaflet, a
        vaccination booklet or a hospital discharge note, in any language and often handwritten.

        Extract only:
        - Growth: each measurement, with weight (weightUnit "kg", "g" or "lb"), length or height, and head circumference
          (lengthUnit "cm" or "in"), as written. Several values on one row or date are one entry.
        - Vaccine: each vaccination given, name as written.

        Dates: for each entry give the date written next to it as date {y, m, d}. Read day-month order unless the record
        is clearly American. A two-digit year is 20yy. If the record gives the baby's age instead of a date, give ageDays
        (days or weeks times 7) or ageMonths; "at birth" is ageDays 0. Never compute a date from an age yourself, and
        never invent a date: an entry without a date or an age goes to unparsed.
        Use the context for the year when a date has none.

        sourceText: the row or line as written (short). Anything else on the photo — names, addresses, notes, feeding
        advice — is ignored and not repeated anywhere. A photo that is not a health record gives no entries.
        """;

    private static readonly object ImageSchema = new
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
                        type = new { type = "STRING", @enum = new[] { "Growth", "Vaccine" } },
                        date = new
                        {
                            type = "OBJECT",
                            properties = new { y = new { type = "INTEGER" }, m = new { type = "INTEGER" }, d = new { type = "INTEGER" } },
                        },
                        ageDays = new { type = "INTEGER" },
                        ageMonths = new { type = "INTEGER" },
                        name = new { type = "STRING" },
                        weight = new { type = "NUMBER" },
                        weightUnit = new { type = "STRING", @enum = new[] { "kg", "g", "lb" } },
                        length = new { type = "NUMBER" },
                        head = new { type = "NUMBER" },
                        lengthUnit = new { type = "STRING", @enum = new[] { "cm", "in" } },
                        sourceText = new { type = "STRING" },
                    },
                    required = new[] { "type", "sourceText" },
                },
            },
            unparsed = new { type = "ARRAY", items = new { type = "STRING" } },
        },
        required = new[] { "entries", "unparsed" },
    };

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
                                "Medicine", "Temperature", "Growth", "Vaccine", "Visit",
                            },
                        },
                        side = new { type = "STRING", @enum = new[] { "left", "right" } },
                        leftMinutes = new { type = "INTEGER" },
                        rightMinutes = new { type = "INTEGER" },
                        amount = new { type = "NUMBER" },
                        unit = new { type = "STRING", @enum = new[] { "ml", "oz" } },
                        milk = new { type = "STRING", @enum = new[] { "formula", "breast" } },
                        contents = new { type = "STRING", @enum = new[] { "wet", "dirty", "mixed" } },
                        name = new { type = "STRING" },
                        dose = new { type = "STRING" },
                        temperature = new { type = "NUMBER" },
                        temperatureUnit = new { type = "STRING", @enum = new[] { "c", "f" } },
                        weight = new { type = "NUMBER" },
                        weightUnit = new { type = "STRING", @enum = new[] { "kg", "g", "lb" } },
                        length = new { type = "NUMBER" },
                        head = new { type = "NUMBER" },
                        lengthUnit = new { type = "STRING", @enum = new[] { "cm", "in" } },
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
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = $"Context:\n{prompt.Context}\nText:\n{prompt.Text}" } } } },
            generationConfig = new { temperature = 0.0, seed = 7, responseMimeType = "application/json", responseSchema = ResponseSchema },
        };
        return await SendAsync(body, Http, json => Read(json), cancellationToken);
    }

    public async Task<ParsedText?> ReadImageAsync(ImagePrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = ImageSystemPrompt } } },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { inline_data = new { mime_type = prompt.MimeType, data = Convert.ToBase64String(prompt.Image) } },
                        new { text = $"Context:\n{prompt.Context}" },
                    },
                },
            },
            generationConfig = new { temperature = 0.0, seed = 7, responseMimeType = "application/json", responseSchema = ImageSchema },
        };
        return await SendAsync(body, ImageHttp, json => Read(json) with { PromptVersion = ImagePromptVersion }, cancellationToken);
    }

    private async Task<ParsedText?> SendAsync(object body, HttpClient http, Func<string, ParsedText> read, CancellationToken cancellationToken)
    {
        var apiKey = configuration["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return null;
        var model = configuration["AiParsing:Model"] ?? DefaultModel;

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"))
            {
                Content = JsonContent.Create(body),
            };
            message.Headers.Add("x-goog-api-key", apiKey);
            using var response = await http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed(logger, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var text = envelope.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            return text is null ? null : read(text);
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
                if (e.ValueKind != JsonValueKind.Object || Str(e, "type") is not { } type)
                    continue;
                // Words always carry a time; a photo's entries carry a date or an age instead.
                var time = e.TryGetProperty("time", out var said) ? said : default;
                var date = e.TryGetProperty("date", out var written) ? Date(written) : null;
                if (time.ValueKind != JsonValueKind.Object && date is null && Int(e, "ageDays") is null && Int(e, "ageMonths") is null)
                    continue;
                entries.Add(new ParsedEntry(
                    type,
                    Str(e, "side"),
                    Int(e, "leftMinutes"),
                    Int(e, "rightMinutes"),
                    Dec(e, "amount"),
                    Str(e, "unit"),
                    Str(e, "milk"),
                    Str(e, "contents"),
                    Time(time),
                    e.TryGetProperty("endTime", out var end) ? Time(end) : null,
                    Str(e, "sourceText") ?? "",
                    Str(e, "name"),
                    Str(e, "dose"),
                    Dec(e, "temperature"),
                    Str(e, "temperatureUnit"),
                    Dec(e, "weight"),
                    Str(e, "weightUnit"),
                    Dec(e, "length"),
                    Dec(e, "head"),
                    Str(e, "lengthUnit"),
                    date,
                    Int(e, "ageDays"),
                    Int(e, "ageMonths")));
            }
        }

        var unparsed = root.TryGetProperty("unparsed", out var rest) && rest.ValueKind == JsonValueKind.Array
            ? rest.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).Where(u => u.Length > 0).ToList()
            : [];
        return new ParsedText(entries, unparsed, PromptVersion);
    }

    // A date the model read: only a real calendar day counts.
    private static DateOnly? Date(JsonElement date) =>
        Int(date, "y") is { } y && Int(date, "m") is { } m && Int(date, "d") is { } d && y is >= 2000 and <= 2100 && m is >= 1 and <= 12
        && d >= 1 && d <= DateTime.DaysInMonth(y, m)
            ? new DateOnly(y, m, d)
            : null;

    private static TimeExpression Time(JsonElement time) => time.ValueKind != JsonValueKind.Object ? TimeExpression.Unspecified : Str(time, "kind") switch
    {
        "now" => TimeExpression.Now,
        "clock" when Int(time, "hh") is { } hh && Int(time, "mm") is var mm => TimeExpression.Clock(hh, mm ?? 0),
        "minutesAgo" when Int(time, "n") is { } n => TimeExpression.MinutesAgo(n),
        _ => TimeExpression.Unspecified,
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d) ? d : null;

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
            text.Contains("vitamin", StringComparison.Ordinal) ? Entry("Medicine") with { Name = "Vitamin D" }
            : text.Contains("left", StringComparison.Ordinal) ? Entry("BreastStart", side: "left")
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

    /// <summary>Any photo reads as a little growth card: a weight at birth, and a weight and length at six weeks.</summary>
    public Task<ParsedText?> ReadImageAsync(ImagePrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ParsedEntry Growth(int ageDays, decimal kg, decimal? cm, string source) =>
            new("Growth", null, null, null, null, null, null, null, TimeExpression.Unspecified, null, source,
                Weight: kg, WeightUnit: "kg", Length: cm, LengthUnit: cm is null ? null : "cm", AgeDays: ageDays);
        return Task.FromResult<ParsedText?>(new ParsedText(
            [Growth(0, 3.41m, null, "birth 3410 g"), Growth(42, 4.95m, 56m, "6 wk 4950 g 56 cm")], [], "fake-image"));
    }
}
