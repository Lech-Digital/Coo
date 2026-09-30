using System.Reflection;
using Coo.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Composition root (ADR 0001 §2): the only place on the backend that creates the real clock.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApiInfrastructure();

var app = builder.Build();

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    .Split('+')[0] ?? "unknown";

app.MapGet("/health", () => Results.Ok(new { status = "ok", version }));

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
