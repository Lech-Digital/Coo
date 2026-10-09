using Coo.Domain.Common;

namespace Coo.Api.Application.Advisor;

public record AdvisorMessageHistory(string Text, bool IsUser);
public record AskAdvisorRequest(string Question, Guid BabyId, string TimeZoneId, List<AdvisorMessageHistory>? History = null);
public record AskAdvisorResponse(string Answer);

public record GenerateAvatarRequest(Guid BabyId, string ImageBase64);
public record GenerateAvatarResponse(string? ImageBase64, string? ErrorCode);

public interface IAdvisorService
{
    Task<AskAdvisorResponse> AskAsync(HouseholdId householdId, AskAdvisorRequest request, CancellationToken cancellationToken);
    Task<GenerateAvatarResponse> GenerateAvatarAsync(HouseholdId householdId, GenerateAvatarRequest request, CancellationToken cancellationToken);
}
