using Coo.Api.Application.Auth;
using Coo.Api.Application.CareLog;
using Coo.Api.Application.Health;
using Coo.Api.Application.Households;
using Coo.Api.Application.Sync;
using Coo.Api.Infrastructure.Identity;
using Coo.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

        // Sync (Spec 011 §7): singletons over the data source, like the other raw-SQL stores.
        services.AddSingleton<ISyncStore, SyncStore>();
        services.AddSingleton<IMembershipStore, MembershipStore>();
        services.AddSingleton<IConsentStore, ConsentStore>();

        // Members and invitations (Spec 011b §10): singletons over the data source, like the sync stores.
        services.AddSingleton<IInvitationStore, InvitationStore>();
        services.AddSingleton<IHouseholdMembershipStore, HouseholdMembershipStore>();

        // Identity's user store and AccountStore share one scoped context per request (AddDbContextFactory registers
        // it scoped too), so an account is created in one transaction (Spec 010 §4.2). User names are "google:<sub>".
        services.AddIdentityCore<CooUser>(options => options.User.AllowedUserNameCharacters += ":")
            .AddEntityFrameworkStores<CooDbContext>();
        services.AddScoped<IAccountStore, AccountStore>();

        services.AddSingleton<Coo.Api.Application.Billing.IBillingStore, Coo.Api.Infrastructure.Persistence.BillingStore>();
        services.AddSingleton<Coo.Api.Application.Billing.IGooglePlayValidator, Coo.Api.Infrastructure.Billing.GooglePlayValidator>();
        services.AddSingleton<Coo.Api.Application.Advisor.IAdvisorService, Coo.Api.Infrastructure.Persistence.AdvisorService>();

        // Registered first, so it finishes before any other hosted service starts and before the server listens.
        services.AddHostedService<DatabaseStartup>();
        return services;
    }

    /// <summary>
    /// Sign-in (Spec 010 §4): the access-token issuer and the bearer validation (one or two signing keys, one of them
    /// active), and the Google ID token verifier for <paramref name="googleWebClientId"/>. A null client ID refuses every
    /// sign-in. The host checks the settings at start-up and calls <c>AddAuthentication().AddJwtBearer()</c> itself.
    /// </summary>
    public static IServiceCollection AddApiIdentity(
        this IServiceCollection services, string? googleWebClientId, string activeKeyId, IReadOnlyDictionary<string, byte[]> signingKeys)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new SigningKeyRing(activeKeyId, signingKeys));
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<IExternalIdentityVerifier>(sp => new GoogleIdTokenVerifier(
            googleWebClientId, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<GoogleIdTokenVerifier>>()));
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, JwtBearerSetup>();
        return services;
    }
}
