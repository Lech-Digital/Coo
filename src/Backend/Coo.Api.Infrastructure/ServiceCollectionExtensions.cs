using Coo.Api.Application.Auth;
using Coo.Api.Application.CareLog;
using Coo.Api.Application.Health;
using Coo.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Coo.Api.Infrastructure;

/// <summary>The one public entry point of this project (ADR 0001 §2).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// PostgreSQL storage. <paramref name="connectionString"/> is required: the host checks it at start-up, so a
    /// missing setting never becomes a runtime failure (Spec 008 §3).
    /// </summary>
    public static IServiceCollection AddApiInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContextFactory<CooDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<ICareEntryStore, CareEntryStore>();
        services.AddSingleton<IHouseholdRegistry, HouseholdRegistry>();
        services.AddSingleton<IDatabaseProbe, DatabaseProbe>();
        services.AddSingleton<IAuthNonceStore, AuthNonceStore>();
        services.AddSingleton<IDeviceSessionStore, DeviceSessionStore>();

        // Identity's user store and AccountStore share one scoped context per request (AddDbContextFactory registers
        // it scoped too), so an account is created in one transaction (Spec 010 §4.2). User names are "google:<sub>".
        services.AddIdentityCore<CooUser>(options => options.User.AllowedUserNameCharacters += ":")
            .AddEntityFrameworkStores<CooDbContext>();
        services.AddScoped<IAccountStore, AccountStore>();

        // Registered first, so it finishes before any other hosted service starts and before the server listens.
        services.AddHostedService<DatabaseStartup>();
        return services;
    }
}
