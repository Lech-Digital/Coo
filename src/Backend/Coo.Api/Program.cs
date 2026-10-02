using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Coo.Api.Application.Auth;
using Coo.Api.Application.CareLog;
using Coo.Api.Application.Health;
using Coo.Api.Application.Sync;
using Coo.Api.Hosting;
using Coo.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var verifyOnly = args is ["verify", ..];

// Composition root (ADR 0001 §2): the only place on the backend that creates the real clock.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApiInfrastructure(
    StartupChecks.RequireConnectionString(builder.Configuration.GetConnectionString(StartupChecks.ConnectionStringName)));

// Sign-in (Spec 010 §4.3): Production refuses to start without the Google Web client ID and a signing key.
var auth = StartupChecks.SelectAuth(
    builder.Configuration[StartupChecks.GoogleWebClientIdKey],
    builder.Configuration.GetSection(StartupChecks.SigningKeysSection).GetChildren().Select(c => KeyValuePair.Create(c.Key, c.Value)),
    builder.Configuration[StartupChecks.ActiveSigningKeyIdKey],
    builder.Environment.IsProduction(),
    RandomNumberGenerator.GetBytes(StartupChecks.MinimumKeyBytes));
builder.Services.AddApiIdentity(auth.GoogleWebClientId, auth.ActiveKeyId, auth.SigningKeys);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorization();
builder.Services.AddAuthRateLimiting(builder.Configuration.GetValue("Auth:PermitsPerMinute", AuthEndpoints.DefaultPermitsPerMinute));
builder.Services.AddSingleton<IssueNonce>();
builder.Services.AddSingleton<RefreshSession>();
builder.Services.AddSingleton<SignOutSession>();
builder.Services.AddSingleton<PurgeAuthNonces>();

// Scoped: they use IAccountStore, which shares the request's database context with Identity.
builder.Services.AddScoped<SignInWithGoogle>();
builder.Services.AddScoped<GetMe>();
builder.Services.AddScoped<DeleteAccount>();
builder.Services.AddSingleton<VerifyIntegrity>();

// Sync (Spec 011 §7). The stores are singletons over the data source, so the use cases are too.
builder.Services.AddSingleton<SyncAccess>();
builder.Services.AddSingleton<PushChanges>();
builder.Services.AddSingleton<PullChanges>();
builder.Services.AddSingleton<RecordConsent>();
builder.Services.AddSingleton<GetMembershipStatus>();
builder.Services.AddSingleton<PurgeSyncHistory>();
if (!verifyOnly)
{
    builder.Services.AddHostedService<IntegrityScanJob>();
    builder.Services.AddHostedService<AuthNonceCleanupJob>();
    builder.Services.AddHostedService<SyncHistoryCleanupJob>();
}

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

// Before authentication: the limiter partitions on the forwarded client address (Spec 010 §4.1).
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// "0.11.0+<commit>": the deploy script compares the commit with the one it shipped (Spec 008 §5).
var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

app.MapGet("/health", async (IDatabaseProbe database, CancellationToken cancellationToken) =>
    await database.IsReachableAsync(cancellationToken)
        ? Results.Ok(new { status = "ok", version, database = "ok" })
        : Results.Json(new { status = "unavailable", version, database = "unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapAuthEndpoints();
app.MapSyncEndpoints();

await app.RunAsync();
return 0;

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
