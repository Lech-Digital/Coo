using Coo.Domain.Common;

namespace Coo.Api.Application.Advisor;

public record AdvisorMessageHistory(string Text, bool IsUser);
public record AskAdvisorRequest(string Question, Guid BabyId, string TimeZoneId, List<AdvisorMessageHistory>? History = null);
public record AskAdvisorResponse(string Answer);

public interface IAdvisorService
{
    Task<AskAdvisorResponse> AskAsync(HouseholdId householdId, AskAdvisorRequest request, CancellationToken cancellationToken);
}
