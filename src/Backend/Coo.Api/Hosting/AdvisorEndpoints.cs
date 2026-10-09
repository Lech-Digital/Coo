using System.Security.Claims;
using Coo.Api.Application.Advisor;
using Coo.Api.Application.Sync;
using Coo.Domain.Common;

namespace Coo.Api.Hosting;

internal static class AdvisorEndpoints
{
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
            var allowed = await access.CheckAsync(AuthEndpoints.UserOf(principal), household, cancellationToken);
            if (allowed.Denial != AccessDenial.None)
                return Results.Forbid(); // simplified for now

            var advisorRequest = new AskAdvisorRequest(request.Question, request.BabyId, request.TimeZoneId ?? "UTC", request.History);

            var response = await advisorService.AskAsync(household, advisorRequest, cancellationToken);
            return Results.Json(
                new System.Collections.Generic.Dictionary<string, string> { ["Answer"] = response.Answer, ["answer"] = response.Answer },
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = null, DictionaryKeyPolicy = null });
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
}
