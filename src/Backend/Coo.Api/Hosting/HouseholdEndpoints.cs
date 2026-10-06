using System.Security.Claims;
using Coo.Api.Application.Auth;
using Coo.Api.Application.Households;
using Coo.Contracts.Auth;
using Coo.Contracts.Households;
using Coo.Contracts.Sync;
using Coo.Domain.Common;
using Coo.Domain.Households;

namespace Coo.Api.Hosting;

/// <summary>
/// Members and invitations (Spec 011b §10.1). Every household endpoint checks an active membership per request in its use
/// case; a caller who is not one gets <c>403 household.forbidden</c>, never ADR 0002 §10's problem body. Codes travel in
/// bodies only. Refusals are codes (ADR 0006).
/// </summary>
internal static class HouseholdEndpoints
{
    private static readonly Dictionary<string, int> Statuses = new(StringComparer.Ordinal)
    {
        [HouseholdErrors.InvitationInvalid] = StatusCodes.Status404NotFound,
        [HouseholdErrors.InvitationRoleUnsupported] = StatusCodes.Status400BadRequest,
        [HouseholdErrors.InvitationTooMany] = StatusCodes.Status409Conflict,
        [HouseholdErrors.Forbidden] = StatusCodes.Status403Forbidden,
        [HouseholdErrors.MemberNotFound] = StatusCodes.Status404NotFound,
        [HouseholdErrors.OwnerCannotLeave] = StatusCodes.Status409Conflict,
        [HouseholdErrors.LastFamily] = StatusCodes.Status409Conflict,
        [HouseholdErrors.ConfirmationRequired] = StatusCodes.Status400BadRequest,
        [HouseholdErrors.IdTaken] = StatusCodes.Status409Conflict,
        [HouseholdErrors.AlreadyHasFamily] = StatusCodes.Status409Conflict,
        [HouseholdErrors.Malformed] = StatusCodes.Status400BadRequest,
        [SyncErrors.ConsentRequired] = StatusCodes.Status403Forbidden,
    };

    public static void MapHouseholdEndpoints(this IEndpointRouteBuilder app)
    {
        var households = app.MapGroup("/api/households").RequireAuthorization();

        households.MapPost("/{id:guid}/invitations", async Task<IResult> (
            Guid id, HttpRequest http, ClaimsPrincipal principal, CreateInvitation create, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty || await AuthEndpoints.TryReadJsonAsync<CreateInvitationRequest>(http, cancellationToken) is not { } request)
                return AuthEndpoints.Refused(HouseholdErrors.Malformed, StatusCodes.Status400BadRequest);
            var result = await create.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), request.Role, cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code);
            var code = result.Value.Code.Value;
            return Results.Json(
                new InvitationCreatedResponse(result.Value.Id.Value, code, InvitationLinks.For(code), result.Value.ExpiresAt),
                statusCode: StatusCodes.Status201Created);
        });

        households.MapDelete("/{id:guid}/invitations/{invitationId:guid}", async Task<IResult> (
            Guid id, Guid invitationId, ClaimsPrincipal principal, WithdrawInvitation withdraw, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty || invitationId == Guid.Empty)
                return Refused(HouseholdErrors.Forbidden);
            var result = await withdraw.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), new InvitationId(invitationId), cancellationToken);
            return result.IsFailure ? Refused(result.Error!.Code) : Results.NoContent();
        });

        households.MapGet("/{id:guid}/members", async Task<IResult> (
            Guid id, ClaimsPrincipal principal, ListMembers list, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty)
                return Refused(HouseholdErrors.Forbidden);
            var user = AuthEndpoints.UserOf(principal);
            var result = await list.ExecuteAsync(user, new HouseholdId(id), cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code);
            return Results.Ok(new MembersResponse(
                [.. result.Value.Members.Select(m => new MemberDto(m.UserId.Value, m.Name, m.Email, AuthEndpoints.RoleName(m.Role), m.JoinedAt, m.UserId == user))],
                [.. result.Value.Invitations.Select(i => new InvitationDto(i.Id.Value, AuthEndpoints.RoleName(i.Role), i.CreatedBy.Value, i.CreatedAt, i.ExpiresAt))]));
        });

        households.MapDelete("/{id:guid}/members/{userId:guid}", async Task<IResult> (
            Guid id, Guid userId, ClaimsPrincipal principal, RemoveMember remove, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty || userId == Guid.Empty)
                return Refused(HouseholdErrors.MemberNotFound);
            var result = await remove.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), new UserId(userId), cancellationToken);
            return result.IsFailure ? Refused(result.Error!.Code) : Results.NoContent();
        });

        households.MapPost("/{id:guid}/leave", async Task<IResult> (
            Guid id, HttpRequest http, ClaimsPrincipal principal, LeaveHousehold leave, CancellationToken cancellationToken) =>
        {
            if (id == Guid.Empty)
                return Refused(HouseholdErrors.Forbidden);
            var confirm = (await AuthEndpoints.TryReadJsonAsync<LeaveRequest>(http, cancellationToken))?.Confirm == true;
            var result = await leave.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), confirm, cancellationToken);
            return result.IsFailure
                ? Refused(result.Error!.Code)
                : Results.Ok(new LeaveResponse(result.Value == LeaveOutcome.Deleted ? LeaveOutcomes.Deleted : LeaveOutcomes.Left));
        });

        households.MapPost("", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, CreateHousehold create, CancellationToken cancellationToken) =>
        {
            if (await AuthEndpoints.TryReadJsonAsync<CreateHouseholdRequest>(http, cancellationToken) is not { HouseholdId: var id } || id == Guid.Empty)
                return AuthEndpoints.Refused(HouseholdErrors.Malformed, StatusCodes.Status400BadRequest);
            var result = await create.ExecuteAsync(AuthEndpoints.UserOf(principal), new HouseholdId(id), cancellationToken);
            return result.IsFailure ? Refused(result.Error!.Code) : Results.StatusCode(StatusCodes.Status201Created);
        });

        var invitations = app.MapGroup("/api/invitations").RequireAuthorization();

        invitations.MapPost("/preview", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, PreviewInvitation preview, CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<InvitationCodeRequest>(http, cancellationToken);
            var result = await preview.ExecuteAsync(AuthEndpoints.UserOf(principal), request?.Code, cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code);
            var p = result.Value;
            return Results.Ok(new InvitationPreviewResponse(p.Household.Value, p.InviterName, p.BabyNames, AuthEndpoints.RoleName(p.Role), p.ExpiresAt, p.AlreadyMember));
        });

        invitations.MapPost("/accept", async Task<IResult> (
            HttpRequest http, ClaimsPrincipal principal, AcceptInvitation accept, CancellationToken cancellationToken) =>
        {
            var request = await AuthEndpoints.TryReadJsonAsync<InvitationCodeRequest>(http, cancellationToken);
            var result = await accept.ExecuteAsync(AuthEndpoints.UserOf(principal), request?.Code, cancellationToken);
            if (result.IsFailure)
                return Refused(result.Error!.Code);
            var outcome = result.Value.Outcome switch
            {
                AcceptOutcome.Rejoined => AcceptOutcomes.Rejoined,
                AcceptOutcome.AlreadyMember => AcceptOutcomes.AlreadyMember,
                _ => AcceptOutcomes.Joined,
            };
            return Results.Ok(new AcceptInvitationResponse(result.Value.Household.Value, AuthEndpoints.RoleName(result.Value.Role), outcome));
        });
    }

    private static IResult Refused(string code) =>
        AuthEndpoints.Refused(code, Statuses.TryGetValue(code, out var status) ? status : StatusCodes.Status400BadRequest);
}
