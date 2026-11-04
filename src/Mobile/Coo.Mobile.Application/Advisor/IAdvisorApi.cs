namespace Coo.Mobile.Application.Advisor;

public interface IAdvisorApi
{
    Task<AskAdvisorResponse> AskAsync(AskAdvisorRequest request, CancellationToken cancellationToken);
    Task<GenerateAvatarResponse> GenerateAvatarAsync(GenerateAvatarRequest request, CancellationToken cancellationToken);

    /// <summary>The sleep plan's words; throws when the server cannot be reached or refuses.</summary>
    Task<SleepPlanWordsResponse> WriteSleepPlanAsync(SleepPlanWordsRequest request, CancellationToken cancellationToken);
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

    /// <summary>How long this phone waits for the answer; older apps send nothing and wait 20 s.</summary>
    public int? WaitSeconds { get; set; }
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

/// <summary>Spec 019 §4: the facts of a plan or a check-in, for the Advisor to write in words (Premium).</summary>
public class SleepPlanWordsRequest
{
    public Guid HouseholdId { get; set; }
    public Guid BabyId { get; set; }

    /// <summary>"plan" or "checkin".</summary>
    public string? Kind { get; set; }

    /// <summary>The computed plan in English, the baby written as {baby}: her name stays on the phone.</summary>
    public string? Facts { get; set; }

    public string? TimeZoneId { get; set; }
    public string? Language { get; set; }
}

public class SleepPlanWordsResponse
{
    public string? Text { get; set; }

    /// <summary>premium_required, advisor.daily-limit or advisor.unavailable.</summary>
    public string? ErrorCode { get; set; }

    public int? Limit { get; set; }
}
