using Coo.Api.Application.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Coo.Api.Infrastructure.Identity;

/// <summary>
/// The bearer handler's settings. Beyond the signature and lifetime, the device session must still be active: a
/// signed-out or replaced session is refused at once, not when its token expires (Spec 010 §8, deliverable 6).
/// </summary>
internal sealed class JwtBearerSetup(SigningKeyRing keys, TimeProvider time, IDeviceSessionStore sessions) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (name != JwtBearerDefaults.AuthenticationScheme)
            return;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = AccessTokenValidation.Parameters(keys, time);
        options.Events = new JwtBearerEvents { OnTokenValidated = OnTokenValidatedAsync };
    }

    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

    private async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var session = context.Principal?.FindFirst(AccessTokenClaims.Session)?.Value;
        if (!Guid.TryParse(session, out var id) || id == Guid.Empty
            || !await sessions.IsActiveAsync(new DeviceSessionId(id), context.HttpContext.RequestAborted))
            context.Fail("The device session is not active.");
    }
}
