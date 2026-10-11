using System.Security.Claims;
using Coo.Api.Application.Households;
using Coo.Api.Application.Sync;
using Coo.Contracts.Households;
using Coo.Domain.Common;

namespace Coo.Api.Hosting;

internal static class BabyAvatarEndpoints
{
    public static void MapBabyAvatarEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/households/{householdId:guid}/babies/{babyId:guid}/avatar")
            .RequireAuthorization();

        // 1. GET metadata
        group.MapGet("/", async Task<IResult> (
            Guid householdId,
            Guid babyId,
            ClaimsPrincipal principal,
            SyncAccess access,
            IBabyAvatarStore store,
            CancellationToken cancellationToken) =>
        {
            var user = AuthEndpoints.UserOf(principal);
            var household = new HouseholdId(householdId);
            var allowed = await access.CheckAsync(user, household, cancellationToken);
            if (allowed.Denial != AccessDenial.None)
                return Results.Forbid();

            var avatar = await store.GetAvatarAsync(householdId, babyId, cancellationToken);
            if (avatar == null || avatar.AvatarType == "none")
            {
                return Results.Ok(new BabyAvatarDto
                {
                    AvatarType = "none",
                    PresetKey = null,
                    Version = 0,
                    HasImage = false
                });
            }

            return Results.Ok(new BabyAvatarDto
            {
                AvatarType = avatar.AvatarType,
                PresetKey = avatar.PresetKey,
                Version = avatar.Version,
                HasImage = avatar.ImageData != null && avatar.ImageData.Length > 0
            });
        });

        // 2. GET image bytes
        group.MapGet("/image", async Task<IResult> (
            Guid householdId,
            Guid babyId,
            ClaimsPrincipal principal,
            SyncAccess access,
            IBabyAvatarStore store,
            CancellationToken cancellationToken) =>
        {
            var user = AuthEndpoints.UserOf(principal);
            var household = new HouseholdId(householdId);
            var allowed = await access.CheckAsync(user, household, cancellationToken);
            if (allowed.Denial != AccessDenial.None)
                return Results.Forbid();

            var avatar = await store.GetAvatarAsync(householdId, babyId, cancellationToken);
            if (avatar == null || avatar.ImageData == null || avatar.ImageData.Length == 0)
                return Results.NotFound();

            return Results.File(avatar.ImageData, "image/jpeg");
        });

        // 3. PUT set avatar
        group.MapPut("/", async Task<IResult> (
            Guid householdId,
            Guid babyId,
            HttpRequest http,
            ClaimsPrincipal principal,
            SyncAccess access,
            IBabyAvatarStore store,
            CancellationToken cancellationToken) =>
        {
            var user = AuthEndpoints.UserOf(principal);
            var household = new HouseholdId(householdId);
            var allowed = await access.CheckAsync(user, household, cancellationToken);
            if (allowed.Denial != AccessDenial.None)
                return Results.Forbid();

            var request = await AuthEndpoints.TryReadJsonAsync<SetBabyAvatarRequest>(http, cancellationToken);
            if (request == null)
                return Results.BadRequest(new { Code = "avatar.malformed" });

            byte[]? imageData = null;
            if (!string.IsNullOrEmpty(request.ImageBase64))
            {
                try
                {
                    imageData = Convert.FromBase64String(request.ImageBase64);
                }
                catch
                {
                    return Results.BadRequest(new { Code = "avatar.invalid_base64" });
                }
            }

            var avatar = await store.SetAvatarAsync(
                householdId, babyId, request.AvatarType, request.PresetKey, imageData, user.Value, cancellationToken);

            return Results.Ok(new BabyAvatarDto
            {
                AvatarType = avatar.AvatarType,
                PresetKey = avatar.PresetKey,
                Version = avatar.Version,
                HasImage = avatar.ImageData != null && avatar.ImageData.Length > 0
            });
        });
    }
}
