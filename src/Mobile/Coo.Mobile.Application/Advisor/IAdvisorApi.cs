namespace Coo.Mobile.Application.Advisor;

public interface IAdvisorApi
{
    Task<AskAdvisorResponse> AskAsync(AskAdvisorRequest request, CancellationToken cancellationToken);
}

public class AskAdvisorRequest
{
    public Guid HouseholdId { get; set; }
    public Guid BabyId { get; set; }
    public string? Question { get; set; }
    public string? TimeZoneId { get; set; }
    public List<AdvisorMessageHistory>? History { get; set; }
}

public class AdvisorMessageHistory
{
    public string? Text { get; set; }
    public bool IsUser { get; set; }
}

public class AskAdvisorResponse
{
    public string? Answer { get; set; }
}
