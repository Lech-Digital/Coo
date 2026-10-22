namespace Coo.Mobile.Application.Advisor;

public interface IAdvisorApi
{
    Task<AskAdvisorResponse> AskAsync(AskAdvisorRequest request, CancellationToken cancellationToken);
    Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken cancellationToken);
}

public class AskAdvisorRequest
{
    public Guid HouseholdId { get; set; }
    public Guid BabyId { get; set; }
    public string? Question { get; set; }
    public string? TimeZoneId { get; set; }
    public List<AdvisorMessageHistory>? History { get; set; }
    public string? Context { get; set; }
    public string? Language { get; set; }
}

public class GenerateAvatarRequest
{
    public Guid HouseholdId { get; set; }
    public Guid BabyId { get; set; }
    public string? ImageBase64 { get; set; }
}

public class GenerateAvatarResponse
{
    public string? ImageBase64 { get; set; }
    public string? ErrorCode { get; set; }
}

public class AdvisorMessageHistory
{
    public string? Text { get; set; }
    public bool IsUser { get; set; }
}

public class AskAdvisorResponse
{
    public string? Answer { get; set; }

    /// <summary>advisor.unavailable, advisor.daily-limit or advisor.free-limit; the app words it.</summary>
    public string? ErrorCode { get; set; }

    public int? Limit { get; set; }
}
