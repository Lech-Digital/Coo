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
    /// language). One JSON file per language in <c>Advisor/Content</c> to edit and translate, made into C# by
    /// <c>eng/gen-advisor-content.py</c>; English when the language has none.
    /// </summary>
    internal static AdvisorContent Content(string language) =>
        AdvisorContentData.All.TryGetValue(language, out var content) ? content : AdvisorContentData.All["en"];

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

    /// <summary>
    /// Questions from her own log (owner, 2026-11-06: questions should fit this baby, not only her age): what changed
    /// between this week and the one before, in the phone's language, at most two, the biggest change first.
    /// </summary>
    public static IReadOnlyList<string> PersonalQuestions(
        string name, int correctedDays, IReadOnlyList<Coo.Mobile.Application.CareLog.DayCare> thisWeek, IReadOnlyList<Coo.Mobile.Application.CareLog.DayCare> lastWeek) =>
        PersonalQuestions(name, correctedDays, Coo.Mobile.Application.CareLog.WeekComparison.Of(thisWeek, lastWeek), null);

    /// <summary>The same from a comparison already made (Today's "This week" uses it too), with any nap transition.</summary>
    public static IReadOnlyList<string> PersonalQuestions(
        string name, int correctedDays, Coo.Mobile.Application.CareLog.WeekComparison week, Coo.Domain.Insights.NapTransition? naps)
    {
        ArgumentNullException.ThrowIfNull(week);
        if (!week.HasThisWeek)
            return [];

        var found = new List<(double Size, string Text)>();
        if (week.Compared)
        {
            if (week.Sleep.TotalMinutes <= -60)
                found.Add((-week.Sleep.TotalMinutes / 60, Localization.Strings.Format("Advisor.Ask.SleepLess", name)));
            if (week.Longest.TotalMinutes <= -60)
                found.Add((-week.Longest.TotalMinutes / 60, Localization.Strings.Format("Advisor.Ask.LongestShorter", name)));
            else if (week.Longest.TotalMinutes >= 60)
                found.Add((week.Longest.TotalMinutes / 90, Localization.Strings.Format("Advisor.Ask.LongestLonger", name)));
            if (week.Feeds >= 2)
                found.Add((week.Feeds / 2, Localization.Strings.Format("Advisor.Ask.FeedsUp", name)));
        }

        if (naps is not null)
            found.Add((2.5, Localization.Strings.Format("Advisor.Ask.Naps", name, naps.From, naps.To)));

        // Fewer than five wet diapers a day under six months is worth asking about, whatever last week was.
        if (correctedDays is >= 5 and < 183 && week.WetPerDay < 5)
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
