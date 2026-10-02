using System.Globalization;
using Coo.Api.Application.Auth;
using Coo.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Api.IntegrationTests;

/// <summary>The API as production runs it (Production environment), with test settings.</summary>
internal static class TestApi
{
    public const string ClientId = "test-client.apps.googleusercontent.com";
    public static readonly string SigningKey = Convert.ToBase64String([.. Enumerable.Repeat((byte)7, 32)]);

    /// <param name="google">Replaces the real Google verifier (ADR 0003 §5's port), when given.</param>
    /// <param name="permitsPerMinute">The auth rate limit; null keeps the production default of 20.</param>
    public static WebApplicationFactory<Program> Create(
        string connectionString, IExternalIdentityVerifier? google = null, int? permitsPerMinute = 1000, Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Production");
            host.UseSetting($"ConnectionStrings:{StartupChecks.ConnectionStringName}", connectionString);
            host.UseSetting(StartupChecks.GoogleWebClientIdKey, ClientId);
            host.UseSetting(StartupChecks.SigningKeysSection + ":k1", SigningKey);
            if (permitsPerMinute is { } permits)
                host.UseSetting("Auth:PermitsPerMinute", permits.ToString(CultureInfo.InvariantCulture));
            if (google is not null)
                host.ConfigureTestServices(services => services.AddSingleton(google));
            configure?.Invoke(host);
        });
}
