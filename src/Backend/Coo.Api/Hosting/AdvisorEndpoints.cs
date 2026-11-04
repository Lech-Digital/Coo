using System.Security.Claims;
using Coo.Api.Application.Advisor;
using Coo.Api.Application.Sync;
using Coo.Domain.Common;

namespace Coo.Api.Hosting;

internal static partial class AdvisorEndpoints
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Generate avatar warning: {Reason}")]
    private static partial void LogAvatarWarning(ILogger logger, string reason);

    public static void MapAdvisorEndpoints(this IEndpointRouteBuilder app)
    {
        var advisor = app.MapGroup("/api/advisor").RequireAuthorization();

        advisor.MapPost("/ask", async Task<IResult> (
            HttpRequest http,
            ClaimsPrincipal principal,
            SyncAccess access,
            IAdvisorService advisorService,
            CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<AskAdvisorHttpRequest>(http, cancellationToken);
            if (request is null || string.IsNullOrWhiteSpace(request.Question) || request.HouseholdId == Guid.Empty || request.BabyId == Guid.Empty)
                return Results.BadRequest(new { Code = "advisor.malformed" });

            var household = new HouseholdId(request.HouseholdId);
            var user = AuthEndpoints.UserOf(principal);
            var allowed = await access.CheckAsync(user, household, cancellationToken);
            if (allowed.Denial != AccessDenial.None)
                return Results.Forbid();

            var advisorRequest = new AskAdvisorRequest(
                request.Question, request.BabyId, request.TimeZoneId ?? "UTC", request.History, request.Context, request.Language);
            var response = await advisorService.AskAsync(household, user, advisorRequest, cancellationToken);
            return Results.Ok(response);
        });

        // Spec 019 §4: the sleep plan's words, Premium. The phone sends computed facts with the baby as {baby}.
        advisor.MapPost("/sleep-plan", async Task<IResult> (
            HttpRequest http,
            ClaimsPrincipal principal,
            SyncAccess access,
            IAdvisorService advisorService,
            CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<SleepPlanWordsHttpRequest>(http, cancellationToken);
            if (request is null || string.IsNullOrWhiteSpace(request.Facts) || request.HouseholdId == Guid.Empty || request.BabyId == Guid.Empty
                || !Enum.TryParse<SleepPlanWordsKind>(request.Kind, ignoreCase: true, out var kind))
                return Results.BadRequest(new { Code = "advisor.malformed" });

            var household = new HouseholdId(request.HouseholdId);
            var user = AuthEndpoints.UserOf(principal);
            var allowed = await access.CheckAsync(user, household, cancellationToken);
            if (allowed.Denial != AccessDenial.None)
                return Results.Forbid();

            var response = await advisorService.WriteSleepPlanAsync(
                household, user, new SleepPlanWordsRequest(request.BabyId, request.TimeZoneId ?? "UTC", kind, request.Facts, request.Language),
                cancellationToken);
            return response.ErrorCode == "premium_required" ? Results.StatusCode(402) : Results.Ok(response);
        });

        advisor.MapPost("/generate-avatar", async Task<IResult> (
            HttpRequest http,
            ClaimsPrincipal principal,
            SyncAccess access,
            IAdvisorService advisorService,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("AdvisorEndpoints");
            var request = await AuthEndpoints.TryReadJsonAsync<GenerateAvatarHttpRequest>(http, cancellationToken);
            if (request is null)
            {
                LogAvatarWarning(logger, "request is null / JSON deserialization failed.");
                return Results.BadRequest(new { Code = "avatar.malformed_json" });
            }
            if (string.IsNullOrWhiteSpace(request.ImageBase64))
            {
                LogAvatarWarning(logger, "ImageBase64 is empty.");
                return Results.BadRequest(new { Code = "avatar.empty_image" });
            }
            if (request.HouseholdId == Guid.Empty || request.BabyId == Guid.Empty)
            {
                LogAvatarWarning(logger, $"empty ids (household={request.HouseholdId}, baby={request.BabyId})");
                return Results.BadRequest(new { Code = "avatar.empty_ids" });
            }

            var household = new HouseholdId(request.HouseholdId);
            var allowed = await access.CheckAsync(AuthEndpoints.UserOf(principal), household, cancellationToken);
            if (allowed.Denial != AccessDenial.None)
            {
                LogAvatarWarning(logger, $"forbidden for household {household.Value} (denial={allowed.Denial})");
                return Results.Forbid();
            }

            var genRequest = new GenerateAvatarRequest(request.BabyId, request.ImageBase64);
            var response = await advisorService.GenerateAvatarAsync(household, genRequest, cancellationToken);
            if (response.ErrorCode == "premium_required")
                return Results.StatusCode(402); // Payment required
            if (response.ErrorCode != null && response.ImageBase64 == null)
            {
                LogAvatarWarning(logger, $"service error: {response.ErrorCode}");
                return Results.BadRequest(new { Code = response.ErrorCode });
            }

            return Results.Json(response);
        });
    }
}

public class AskAdvisorHttpRequest
{
    public Guid HouseholdId { get; set; }
    public Guid BabyId { get; set; }
    public string? Question { get; set; }
    public string? TimeZoneId { get; set; }
    public List<AdvisorMessageHistory>? History { get; set; }
    public string? Context { get; set; }
    public string? Language { get; set; }
}

public class SleepPlanWordsHttpRequest
{
    public Guid HouseholdId { get; set; }
    public Guid BabyId { get; set; }

    /// <summary>"plan" or "checkin".</summary>
    public string? Kind { get; set; }

    public string? Facts { get; set; }
    public string? TimeZoneId { get; set; }
    public string? Language { get; set; }
}

public class GenerateAvatarHttpRequest
{
    public Guid HouseholdId { get; set; }
    public Guid BabyId { get; set; }
    public string? ImageBase64 { get; set; }
}
