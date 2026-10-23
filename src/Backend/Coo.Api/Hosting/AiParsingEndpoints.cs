using System.Security.Claims;
using Coo.Api.Application.AiParsing;
using Coo.Contracts.AiParsing;
using Microsoft.AspNetCore.Mvc;

namespace Coo.Api.Hosting;

/// <summary>AI logging (ADR 0005): text in, proposals out. The server never writes to the care log and keeps no text.</summary>
public static class AiParsingEndpoints
{
    public static void MapAiParsingEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/ai/parse", async Task<IResult> (
            HttpRequest http,
            ClaimsPrincipal principal,
            [FromServices] ParseCareLogText parse,
            CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<AiParseRequest>(http, cancellationToken);
            if (request is null || request.HouseholdId == Guid.Empty || request.Context is null || string.IsNullOrWhiteSpace(request.TimeZoneId))
                return Results.BadRequest(new { Code = "ai.malformed" });

            var outcome = await parse.ExecuteAsync(AuthEndpoints.UserOf(principal), request, cancellationToken);
            return outcome.Refusal switch
            {
                ParseRefusal.NotAMember => Results.Forbid(),
                ParseRefusal.TooLong => Results.BadRequest(new { Code = "ai.too-long" }),
                _ => Results.Ok(outcome.Response),
            };
        }).RequireAuthorization();
}
