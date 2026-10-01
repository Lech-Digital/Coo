using System.Net;
using System.Reflection;
using Coo.Api.Application.CareLog;
using Coo.Api.Application.Health;
using Coo.Api.Hosting;
using Coo.Api.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var verifyOnly = args is ["verify", ..];

// Composition root (ADR 0001 §2): the only place on the backend that creates the real clock.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApiInfrastructure(
    StartupChecks.RequireConnectionString(builder.Configuration.GetConnectionString(StartupChecks.ConnectionStringName)));
builder.Services.AddSingleton<VerifyIntegrity>();
if (!verifyOnly)
    builder.Services.AddHostedService<IntegrityScanJob>();

var app = builder.Build();

// `dotnet Coo.Api.dll verify` runs the scan and exits; it never starts the server or the migrations.
if (verifyOnly)
    return await VerifyCommand.RunAsync(app.Services, Console.Out, CancellationToken.None);

// Behind Caddy, on the shared Docker network (ADR 0003 §5). The proxy's address is not fixed, so the private
// ranges are trusted for the forwarded headers; the container is not reachable from outside except through Caddy.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
};
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
forwarded.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
app.UseForwardedHeaders(forwarded);

// "0.8.0+<commit>": the deploy script compares the commit with the one it shipped (Spec 008 §5).
var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

app.MapGet("/health", async (IDatabaseProbe database, CancellationToken cancellationToken) =>
    await database.IsReachableAsync(cancellationToken)
        ? Results.Ok(new { status = "ok", version, database = "ok" })
        : Results.Json(new { status = "unavailable", version, database = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

await app.RunAsync();
return 0;

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
