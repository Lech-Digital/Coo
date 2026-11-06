namespace Coo.Mobile.Presentation.Advisor;

public sealed record DevelopmentStageInfo(
    string StageBadge,
    string Summary,
    string MotorSkills,
    string CognitiveSensory,
    string SocialSpeech,
    string RhythmSleep,
    string PediatricTip);

public static class AdvisorKnowledgeBase
{
    /// <summary>
    /// The age guide and its questions in the phone's language (owner, 2026-11-06: the questions were English in every
    /// language). One file per language in <c>Advisor/Content</c>, English when the language has none.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AdvisorContent> Loaded = new(StringComparer.Ordinal);

    internal static AdvisorContent Content(string language) => Loaded.GetOrAdd(language, Load);

    private static AdvisorContent Load(string language)
    {
        var assembly = typeof(AdvisorKnowledgeBase).Assembly;
        using var stream = assembly.GetManifestResourceStream($"Advisor.Content.{language}.json")
            ?? assembly.GetManifestResourceStream("Advisor.Content.en.json")
            ?? throw new InvalidOperationException("The English Advisor content is missing.");
        return System.Text.Json.JsonSerializer.Deserialize<AdvisorContent>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"The Advisor content for {language} is empty.");
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The age's stage card, in the phone's language.</summary>
    public static DevelopmentStageInfo GetStage(int daysOld)
    {
        var stage = Content(Localization.Strings.Language).Stages.Last(s => s.From <= Math.Max(0, daysOld));
        static string Bullets(IEnumerable<string> lines) => string.Join("\n", lines.Select(l => "• " + l));
        return new DevelopmentStageInfo(
            "✨ " + stage.Badge, stage.Summary, Bullets(stage.Motor), Bullets(stage.Cognitive), Bullets(stage.Social), Bullets(stage.Rhythm), stage.Tip);
    }

    /// <summary>The whole card under its headings, for "Show full guide".</summary>
    public static string FullGuide(DevelopmentStageInfo stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var h = Content(Localization.Strings.Language).Headings;
        return $"🏃 {h.Motor}\n{stage.MotorSkills}\n\n🧠 {h.Cognitive}\n{stage.CognitiveSensory}\n\n💬 {h.Social}\n{stage.SocialSpeech}\n\n💤 {h.Rhythm}\n{stage.RhythmSleep}\n\n💡 {h.Tip}\n{stage.PediatricTip}";
    }

    /// <summary>The age's twelve questions, in the phone's language.</summary>
    public static string[] GetQuestionPool(int daysOld) =>
        [.. Content(Localization.Strings.Language).Questions.Last(q => q.From <= Math.Max(0, daysOld)).Items];

    /// <summary>Questions for when the birth date is not known.</summary>
    public static string[] GetFallbackQuestions() => [.. Content(Localization.Strings.Language).Fallback];

    /// <summary>Logged days needed in each week before this week is compared with the one before.</summary>
    public const int DaysToCompare = 4;

    /// <summary>
    /// Questions from her own log (owner, 2026-11-06: questions should fit this baby, not only her age): what changed
    /// between this week and the one before, in the phone's language, at most two, the biggest change first.
    /// </summary>
    public static IReadOnlyList<string> PersonalQuestions(
        string name, int correctedDays, IReadOnlyList<Coo.Mobile.Application.CareLog.DayCare> thisWeek, IReadOnlyList<Coo.Mobile.Application.CareLog.DayCare> lastWeek)
    {
        ArgumentNullException.ThrowIfNull(thisWeek);
        ArgumentNullException.ThrowIfNull(lastWeek);
        var now = thisWeek.Where(d => d.HasEntries).ToList();
        var before = lastWeek.Where(d => d.HasEntries).ToList();
        if (now.Count < DaysToCompare)
            return [];

        var found = new List<(double Size, string Text)>();
        if (before.Count >= DaysToCompare)
        {
            var sleep = now.Average(d => d.Sleep.TotalMinutes) - before.Average(d => d.Sleep.TotalMinutes);
            if (sleep <= -60)
                found.Add((-sleep / 60, Localization.Strings.Format("Advisor.Ask.SleepLess", name)));
            var longest = now.Average(d => d.LongestSleep.TotalMinutes) - before.Average(d => d.LongestSleep.TotalMinutes);
            if (longest <= -60)
                found.Add((-longest / 60, Localization.Strings.Format("Advisor.Ask.LongestShorter", name)));
            else if (longest >= 60)
                found.Add((longest / 90, Localization.Strings.Format("Advisor.Ask.LongestLonger", name)));
            var feeds = now.Average(d => d.Feeds) - before.Average(d => d.Feeds);
            if (feeds >= 2)
                found.Add((feeds / 2, Localization.Strings.Format("Advisor.Ask.FeedsUp", name)));
        }

        // Fewer than five wet diapers a day under six months is worth asking about, whatever last week was.
        if (correctedDays is >= 5 and < 183 && now.All(d => d.Diapers > 0) && now.Average(d => d.Wet) < 5)
            found.Add((2, Localization.Strings.Format("Advisor.Ask.FewWet", name)));

        return [.. found.OrderByDescending(f => f.Size).Take(2).Select(f => f.Text)];
    }


}

/// <summary>The Advisor's age content for one language (one JSON file in Advisor/Content).</summary>
internal sealed record AdvisorContent(AdvisorHeadings Headings, IReadOnlyList<AdvisorStage> Stages, IReadOnlyList<AdvisorQuestions> Questions, IReadOnlyList<string> Fallback);

internal sealed record AdvisorHeadings(string Motor, string Cognitive, string Social, string Rhythm, string Tip);

/// <param name="From">The stage starts at this age in days.</param>
internal sealed record AdvisorStage(
    int From, string Badge, string Summary, IReadOnlyList<string> Motor, IReadOnlyList<string> Cognitive, IReadOnlyList<string> Social,
    IReadOnlyList<string> Rhythm, string Tip);

/// <param name="From">The questions apply from this age in days.</param>
internal sealed record AdvisorQuestions(int From, IReadOnlyList<string> Items);
