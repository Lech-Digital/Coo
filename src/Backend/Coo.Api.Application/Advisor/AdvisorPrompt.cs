namespace Coo.Api.Application.Advisor;

/// <summary>What Gemini is told: who it is, its limits, the baby's log, and how much of the conversation it sees.</summary>
public static class AdvisorPrompt
{
    public const int FreeDailyQuestions = 3;
    public const int PremiumDailyQuestions = 100;
    public const int MaxQuestionLength = 1000;
    public const int MaxContextLength = 6000;
    public const int MaxHistoryMessages = 10;
    public const int MaxHistoryMessageLength = 2000;

    private static readonly Dictionary<string, string> Languages = new()
    {
        ["en"] = "English",
        ["de"] = "German",
        ["fr"] = "French",
        ["es"] = "Spanish",
        ["it"] = "Italian",
        ["pl"] = "Polish",
        ["nl"] = "Dutch",
    };

    public static string LanguageName(string? code) =>
        code is not null && Languages.TryGetValue(code.ToLowerInvariant(), out var name) ? name : "English";

    /// <summary>The system instruction: the role, the safety rules and the log the answer may rely on.</summary>
    public static string System(string? babyName, int? ageInWeeks, string? context, string? language)
    {
        var lines = new List<string>
        {
            "You are Coo's baby advisor: a warm, practical companion for the parents of a young baby, grounded in mainstream pediatric guidance (WHO, AAP, NHS).",
            "You are not a doctor. Never diagnose, never prescribe or dose medicines, and say so briefly when a question asks for it.",
            "If the question mentions a possible emergency or red-flag sign (trouble breathing, blue or grey lips, a fever in a baby under 3 months, fewer than 4 wet diapers a day in a newborn, unresponsiveness, a seizure, severe dehydration, repeated forceful vomiting, a bulging soft spot), start your answer by telling them to call emergency services or their doctor now.",
            "Use the baby's log below when it is relevant: quote real numbers from it (\"she slept 13 h 05 m a day this week\") and never invent entries that are not there. If the log does not cover something, say you cannot see it.",
            "Keep answers short and concrete: at most about 150 words unless asked for more. Plain text only: no markdown, no asterisks, no headings; use simple dashes for lists.",
            $"Always answer in {LanguageName(language)}.",
        };
        if (ageInWeeks is { } weeks)
            lines.Add($"The baby{(string.IsNullOrWhiteSpace(babyName) ? "" : $", {babyName},")} is {weeks} weeks old.");
        if (!string.IsNullOrWhiteSpace(context))
            lines.Add("The baby's log, as summarised by the app:\n" + Truncate(context, MaxContextLength));
        return string.Join("\n", lines);
    }

    /// <summary>The last few messages, each cut to a sane length: the phone sends the whole thread.</summary>
    public static IReadOnlyList<AdvisorMessageHistory> History(IEnumerable<AdvisorMessageHistory>? history) =>
        history is null
            ? []
            : [.. history.Where(m => !string.IsNullOrWhiteSpace(m.Text))
                .TakeLast(MaxHistoryMessages)
                .Select(m => m with { Text = Truncate(m.Text, MaxHistoryMessageLength) })];

    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
