using System.Security.Cryptography;
using System.Text;
using Coo.Domain.Households;
using Coo.Domain.Insights;
using Coo.Mobile.Application.Insights;
using Coo.Mobile.Presentation.Formatting;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.SleepPlan;

/// <summary>
/// What the Advisor is told to write a plan's words from (Spec 019 §4): the computed numbers and the reviewed texts, in
/// English and 24-hour times, with the baby written as <see cref="Baby"/> so her name never leaves the phone (ADR 0005).
/// </summary>
public static class SleepPlanFacts
{
    public const string Baby = "{baby}";

    private static readonly Formats Clock = new(true, VolumeUnit.Millilitres);

    public static string Plan(SleepPlanToday plan, int ageInWeeks)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var english = Strings.InLanguage("en");
        return string.Join("\n", Common(plan, ageInWeeks));
    }

    public static string CheckIn(SleepPlanToday plan, SleepCheckIn checkIn, int ageInWeeks)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checkIn);
        using var english = Strings.InLanguage("en");
        var lines = Common(plan, ageInWeeks);
        lines.Add("");
        lines.Add($"Check-in on day {plan.DayNumber}. Measures, per day or night: the week before the plan, then the days since it started ({checkIn.Now.Days} days):");
        lines.AddRange(SleepPlanViewModel.Measures(checkIn, Clock).Select(m => $"- {m.Label}: {m.Before} before, {m.Now} now"));
        lines.Add("Suggested change: " + SleepPlanViewModel.Suggestion(checkIn.Suggestion, Baby));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Where the words for these facts are kept: the plan, what is written, the language and a fingerprint of the facts,
    /// so a changed plan, check-in or language is written anew and an unchanged one never twice.
    /// </summary>
    public static string Key(SleepPlanToday plan, SleepPlanWordsKind kind, string language, string facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{language}|{facts}")))[..24];
        return $"{plan.Entry.Id.Value:N}.{hash}".ToLowerInvariant();
    }

    private static List<string> Common(SleepPlanToday plan, int ageInWeeks)
    {
        var lines = new List<string>
        {
            $"Baby: {Baby}, {ageInWeeks} weeks old (corrected age). Day {plan.DayNumber} of the plan.",
            "Goals: " + string.Join(", ", plan.Details.Goals.Select(SleepPlanViewModel.GoalName)) + ".",
        };
        if (plan.Day is { } day)
        {
            lines.Add("Planned day:");
            lines.AddRange(SleepPlanViewModel.Rows(day, Clock).Select(r => r.Detail.Length > 0 ? $"- {r.Label} {r.Time}, {r.Detail}" : $"- {r.Label} {r.Time}"));
            lines.AddRange(SleepPlanViewModel.Notes(day, Baby).Select(n => "Why: " + n));
        }
        else
        {
            lines.Add($"The plan is paused: {Baby} has been unwell lately (a fever or a medicine in the last 3 days).");
        }

        lines.Add("");
        lines.Add($"Settling approach: {SleepPlanViewModel.ApproachName(plan.Approach)}. Its reviewed text:");
        lines.Add(SleepPlanViewModel.SettlingText(plan.Approach, Baby));
        lines.Add("Bedtime routine, reviewed text:");
        lines.Add(SleepPlanViewModel.Routine(Baby));
        lines.Add("What to expect, reviewed text:");
        lines.Add(SleepPlanViewModel.ExpectText(plan.Approach));
        return lines;
    }
}
