using Coo.Domain.Common;

namespace Coo.Api.Application.Advisor;

public record AdvisorMessageHistory(string Text, bool IsUser);

/// <param name="Context">The phone's summary of the baby's recent log (sleep, feeds, diapers per day), in plain text.</param>
/// <param name="Language">The app's language, two letters: the answer is written in it.</param>
public record AskAdvisorRequest(
    string Question, Guid BabyId, string TimeZoneId, List<AdvisorMessageHistory>? History = null, string? Context = null, string? Language = null);

/// <summary>An answer, or an <see cref="ErrorCode"/> the app shows in its own words (advisor.daily-limit, advisor.unavailable).</summary>
public record AskAdvisorResponse(string? Answer, string? ErrorCode = null, int? Limit = null);

public record GenerateAvatarRequest(Guid BabyId, string ImageBase64);
public record GenerateAvatarResponse(string? ImageBase64, string? ErrorCode);

public interface IAdvisorService
{
    Task<AskAdvisorResponse> AskAsync(HouseholdId householdId, UserId user, AskAdvisorRequest request, CancellationToken cancellationToken);
    /// <summary>The sleep plan's words (Spec 019 §4): Premium, with its own daily allowance.</summary>
    Task<SleepPlanWordsResponse> WriteSleepPlanAsync(HouseholdId householdId, UserId user, SleepPlanWordsRequest request, CancellationToken cancellationToken);

    Task<GenerateAvatarResponse> GenerateAvatarAsync(HouseholdId householdId, GenerateAvatarRequest request, CancellationToken cancellationToken);
}
