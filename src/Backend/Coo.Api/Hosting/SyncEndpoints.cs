using System.Globalization;
using System.Security.Claims;
using Coo.Api.Application.Sync;
using Coo.Contracts.Sync;
using Coo.Domain.Common;

namespace Coo.Api.Hosting;

/// <summary>
/// Sync (Spec 011 §7.1). Every call needs the access token (the bearer handler refuses an inactive session) and, per
/// request, an active membership of its household and a consent to the current wording. Refusals are codes; only the
/// membership-revoked answer is a problem body, ADR 0002 §10's exact signal.
/// </summary>
internal static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var sync = app.MapGroup("/api/sync").RequireAuthorization();

        sync.MapPost("/push", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, SyncAccess access, PushChanges push, CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<PushRequest>(http, cancellationToken);
            if (request is null || request.HouseholdId == Guid.Empty || request.DeviceId == Guid.Empty || request.Changes is null
                || !request.Changes.All(SyncMapping.IsAnswerable))
                return AuthEndpoints.Refused(SyncErrors.Malformed, StatusCodes.Status400BadRequest);
            if (request.Changes.Count > SyncLimits.MaxPushChanges)
                return AuthEndpoints.Refused(SyncErrors.BatchTooLarge, StatusCodes.Status400BadRequest);
            if (request.SchemaVersion < SchemaVersions.Oldest)
                return AuthEndpoints.Refused(SyncErrors.ClientTooOld, StatusCodes.Status426UpgradeRequired);

            var household = new HouseholdId(request.HouseholdId);
            var allowed = await access.CheckAsync(AuthEndpoints.UserOf(principal), household, cancellationToken);
            if (Denied(allowed, household) is { } denied)
                return denied;

            var verdicts = await push.ExecuteAsync(
                household, allowed.Membership!, request.SchemaVersion, [.. request.Changes.Select(SyncMapping.ToChange)], cancellationToken);
            return Results.Ok(new PushResponse([.. verdicts.Select(SyncMapping.ToDto)]));
        });

        sync.MapGet("/pull", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, SyncAccess access, PullChanges pull, CancellationToken cancellationToken) =>
        {
            var query = http.Query;
            long? head = null;
            if (!Guid.TryParse(query["household"], out var id) || id == Guid.Empty
                || !long.TryParse(query["cursor"], NumberStyles.None, CultureInfo.InvariantCulture, out var cursor)
                || !int.TryParse(query["schemaVersion"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var schema))
                return AuthEndpoints.Refused(SyncErrors.Malformed, StatusCodes.Status400BadRequest);
            if (!string.IsNullOrEmpty(query["bootstrapHead"]))
            {
                if (!long.TryParse(query["bootstrapHead"], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    return AuthEndpoints.Refused(SyncErrors.Malformed, StatusCodes.Status400BadRequest);
                head = parsed;
            }

            if (schema < SchemaVersions.Oldest)
                return AuthEndpoints.Refused(SyncErrors.ClientTooOld, StatusCodes.Status426UpgradeRequired);

            var household = new HouseholdId(id);
            var allowed = await access.CheckAsync(AuthEndpoints.UserOf(principal), household, cancellationToken);
            if (Denied(allowed, household) is { } denied)
                return denied;

            return Results.Ok(SyncMapping.ToDto(await pull.ExecuteAsync(household, cursor, head, cancellationToken)));
        });

        app.MapGet("/api/households/{id:guid}/membership", async Task<IResult> (
            Guid id, ClaimsPrincipal principal, GetMembershipStatus status, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty)
                return Results.NotFound();
            var found = await status.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), cancellationToken);
            return found.Membership is null
                ? Results.NotFound()
                : Results.Ok(new MembershipStatusResponse(found.IsRevoked ? MembershipStatuses.Revoked : MembershipStatuses.Active));
        }).RequireAuthorization();

        app.MapPost("/api/consents", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, RecordConsent record, CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<ConsentRequest>(http, cancellationToken);
            var result = await record.ExecuteAsync(
                AuthEndpoints.UserOf(principal), request?.Purpose, request?.Wording, request?.AppVersion, request?.Language, cancellationToken);
            return result.IsFailure ? AuthEndpoints.Refused(result.Error!.Code, StatusCodes.Status400BadRequest) : Results.NoContent();
        }).RequireAuthorization();
    }

    private static IResult? Denied(SyncAccessResult access, HouseholdId household) => access.Denial switch
    {
        AccessDenial.None => null,
        AccessDenial.NotAMember => AuthEndpoints.Refused(SyncErrors.NotAMember, StatusCodes.Status403Forbidden),
        AccessDenial.ConsentRequired => AuthEndpoints.Refused(SyncErrors.ConsentRequired, StatusCodes.Status403Forbidden),
        AccessDenial.MembershipRevoked => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            type: SyncErrors.MembershipRevokedType,
            title: "membership-revoked",
            extensions: new Dictionary<string, object?> { ["householdId"] = household.Value }),
        _ => throw new ArgumentOutOfRangeException(nameof(access), access.Denial, "Unknown denial."),
    };
}
