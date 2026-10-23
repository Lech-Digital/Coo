using Coo.Domain.CareLog.Time;
using Coo.Domain.Common;

namespace Coo.Api.Application.AiParsing;

/// <summary>What the model is given (ADR 0005 §4): pseudonymised text and the context that makes it resolvable.</summary>
/// <param name="Text">The input, names already replaced.</param>
/// <param name="Context">Plain-language facts: what is running, the last breast side, the unit, the language.</param>
public sealed record ParsePrompt(string Text, string Context);

/// <summary>One entry as the model read it. Untrusted: everything is checked before it becomes a proposal.</summary>
/// <param name="Type">BreastStart, BreastSwitch, BreastStop, BreastFeeding, BottleFeeding, Diaper, SleepStart, SleepEnd, Sleep,
/// PumpStart, PumpStop or Pumping.</param>
/// <param name="Unit">"ml" or "oz" for <see cref="Amount"/>; null when not said.</param>
public sealed record ParsedEntry(
    string Type,
    string? Side,
    int? LeftMinutes,
    int? RightMinutes,
    decimal? Amount,
    string? Unit,
    string? Milk,
    string? Contents,
    TimeExpression Time,
    TimeExpression? EndTime,
    string SourceText);

public sealed record ParsedText(IReadOnlyList<ParsedEntry> Entries, IReadOnlyList<string> Unparsed, string PromptVersion);

/// <summary>The model behind AI logging (ADR 0005 §9): Gemini in production, a fake in development.</summary>
public interface ICareLogTextParser
{
    /// <returns>Null when the model could not be reached or answered with nothing usable.</returns>
    Task<ParsedText?> ParseAsync(ParsePrompt prompt, CancellationToken cancellationToken);
}

/// <summary>The names that must not reach the model (ADR 0005 §4a), and the daily counts that bound its cost (§7).</summary>
public interface IAiParsingStore
{
    /// <summary>The household's babies' names and nicknames, and its members' display names.</summary>
    Task<(IReadOnlyList<string> Babies, IReadOnlyList<string> Caregivers)> NamesAsync(HouseholdId household, CancellationToken cancellationToken);

    /// <summary>Counts one parse for the household's day and returns how many it has made that day, this one included.</summary>
    Task<int> CountAsync(HouseholdId household, DateOnly day, CancellationToken cancellationToken);

    /// <summary>How many parses the household has made that day.</summary>
    Task<int> UsedAsync(HouseholdId household, DateOnly day, CancellationToken cancellationToken);
}
