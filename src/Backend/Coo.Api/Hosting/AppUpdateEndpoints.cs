using Microsoft.AspNetCore.Mvc;

namespace Coo.Api.Hosting;

public static class AppUpdateEndpoints
{
    public static void MapAppUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/app-version", ([FromServices] IConfiguration configuration) =>
        {
            var latest = configuration.GetValue<int>("AppUpdates:Latest", 25);
            var minimum = configuration.GetValue<int>("AppUpdates:Minimum", 15);
            var isForced = configuration.GetValue<bool>("AppUpdates:IsForced", false);

            return Results.Ok(new
            {
                latest,
                minimum,
                isForced
            });
        });
    }
}
