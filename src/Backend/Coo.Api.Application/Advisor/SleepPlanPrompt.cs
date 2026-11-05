using System.Text.RegularExpressions;

namespace Coo.Api.Application.Advisor;

/// <summary>What the plan's words are about: the plan itself, or a check-in's measures and suggestion.</summary>
public enum SleepPlanWordsKind
{
    Plan,
    CheckIn,
}

/// <param name="Facts">
/// The phone's computed plan in plain English: the numbers, the approach's reviewed text, the measures, the suggestion. The
/// baby is written as {baby}: her name never leaves the phone (ADR 0005), the phone puts it back.
/// </param>
public record SleepPlanWordsRequest(Guid BabyId, string TimeZoneId, SleepPlanWordsKind Kind, string Facts, string? Language = null);

/// <summary>The text, or an <see cref="ErrorCode"/>: premium_required, advisor.daily-limit, advisor.unavailable.</summary>
public record SleepPlanWordsResponse(string? Text, string? ErrorCode = null, int? Limit = null);

/// <summary>
/// Spec 019 §4: the Advisor writes the plan's words (Premium) from numbers the phone computed. The model adapts tone, not
/// substance, and the reply is checked to quote only the facts' clock times; a reply that invents one is not shown.
/// </summary>
public static partial class SleepPlanPrompt
{
    /// <summary>Per member and day: a plan's words are written once and kept on the phone, so this is plenty.</summary>
    public const int DailyTexts = 10;

    public const int MaxFactsLength = 6000;

    public const string BabyToken = "{baby}";

    public static string System(SleepPlanWordsKind kind, string? language)
    {
        var lines = new List<string>
        {
            "You write the words of a baby's sleep plan in Mochi, a baby-tracking app, for tired parents. Warm, calm, practical, concrete.",
            "The plan was computed by the app from the baby's own log and mainstream guidance (AAP, NHS). You do not change it: you explain it for this baby and this family.",
            "",
            "Rules:",
            $"- The baby is written as {BabyToken}. Write {BabyToken} wherever you use the baby's name, exactly like that. Never invent a name.",
            "- Quote only the times and numbers in the facts, exactly as written there. Never add a clock time, a duration or a number of your own.",
            "- Follow the settling approach's text in the facts. Do not contradict it, and do not suggest another method.",
            "- Never suggest leaving the baby to cry alone, sleep training under 4 months, keeping the baby awake on purpose, or anything against safe sleep (back to sleep, own flat surface, nothing loose in the cot).",
            "- No diagnosis, no medicine. Do not add disclaimers: the app shows the safe-sleep line and \"general guidance, not medical advice\" under the plan.",
            "- Plain text only: no markdown, no asterisks, no headings, no emoji. Short paragraphs; simple dashes for a list.",
            "",
        };
        lines.AddRange(kind switch
        {
            SleepPlanWordsKind.CheckIn =>
            [
                "Write the check-in note, about 90 words: what changed since the plan started (from the measures, honestly: say so if little changed or there is little logged), then the suggested change and why it may help, and one encouraging sentence.",
            ],
            _ =>
            [
                "Write the plan, about 180 words, in three short parts:",
                "- the day: why these times suit the baby now (her own windows, her age);",
                "- the bedtime routine and how to settle with the chosen approach, as steps for this family;",
                "- what to expect in the first week.",
            ],
        });
        lines.Add("");
        lines.Add($"Always write in {AdvisorPrompt.LanguageName(language)}.");
        return string.Join("\n", lines);
    }

    public static string User(string facts) => "The plan's facts:\n" + AdvisorPrompt.Truncate(facts, MaxFactsLength);

    /// <summary>
    /// True when every clock time in <paramref name="reply"/> is one of the facts' (09:45, 9:45 and 9.45 count as the
    /// same). The reply may say fewer times; it may not make one up.
    /// </summary>
    public static bool QuotesOnlyTheFacts(string reply, string facts)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(facts);
        var allowed = Times(facts).ToHashSet();
        return Times(reply).All(allowed.Contains);
    }

    /// <summary>A reply that may be shown: not empty, quoting only the facts' times, with the baby's token in place.</summary>
    public static string? Accept(string? reply, string facts)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return null;
        var text = reply.Trim();
        return QuotesOnlyTheFacts(text, facts) ? text : null;
    }

    private static IEnumerable<int> Times(string text) =>
        ClockTime().Matches(text).Select(m => int.Parse(m.Groups[1].Value, global::System.Globalization.CultureInfo.InvariantCulture) * 60
                                              + int.Parse(m.Groups[2].Value, global::System.Globalization.CultureInfo.InvariantCulture));

    [GeneratedRegex(@"(?<![\d:.])([01]?\d|2[0-3])[:.]([0-5]\d)(?![\d:])")]
    private static partial Regex ClockTime();
}
