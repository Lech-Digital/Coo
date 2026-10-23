using Coo.Mobile.Application.Advisor;
using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Application.Sync;
using Coo.Mobile.Infrastructure.Advisor;
using Coo.Mobile.Infrastructure.AppVersion;
using Coo.Mobile.Infrastructure.Auth;
using Coo.Mobile.Infrastructure.Households;
using Coo.Mobile.Infrastructure.Persistence;
using Coo.Mobile.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Mobile.Infrastructure;

/// <summary>The one public entry point of this project (ADR 0001 §2).</summary>
public static class ServiceCollectionExtensions
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);

    /// <param name="databasePath">The SQLite file, inside the app's private data folder.</param>
    /// <param name="appVersion">The running app version: the integrity scan runs once per version (Spec 004 §7).</param>
    /// <param name="apiBaseAddress">The API, ending in a slash: <c>https://api.coo.lechdigital.nl/</c> (Spec 010 §6).</param>
    public static IServiceCollection AddMobileInfrastructure(this IServiceCollection services, string databasePath, string appVersion, Uri apiBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentNullException.ThrowIfNull(apiBaseAddress);

        services.AddSingleton(new LocalDatabase(databasePath));
        services.AddSingleton<DeviceState>();
        services.AddSingleton<IVersionClock>(sp => sp.GetRequiredService<DeviceState>());
        services.AddSingleton<IDeviceIdentity>(sp => sp.GetRequiredService<DeviceState>());
        services.AddSingleton<ICareLogStore, CareLogStore>();
        services.AddSingleton<IHouseholdStore, HouseholdStore>();
        services.AddSingleton<IDeviceLocale, SystemDeviceLocale>();
        services.AddSingleton<IStorageDiagnostics, StorageDiagnostics>();
        services.AddSingleton<IDevicePreferences, DevicePreferences>();
        services.AddSingleton<IDuplicateDismissalStore, DuplicateDismissalStore>();

        // Sync (Spec 011 §6).
        services.AddSingleton<ISyncStore, SyncStore>();
        services.AddSingleton<IHouseholdMoves, HouseholdMoves>();
        services.AddSingleton<ISyncState, SyncState>();

        // Members and invitations (Spec 011b §7).
        services.AddSingleton<IFamilyStore, FamilyStore>();
        services.AddSingleton<IInvitationCodes, InvitationCodeStore>();
        services.AddSingleton<IFamilyRecords, FamilyRecords>();

        // There are no data migrations in v1 (Spec 004 §7). New ones are registered here, by hand.
        services.AddSingleton(sp => new DataMigrationRunner([], sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAppStartup>(sp => new AppStartup(
            sp.GetRequiredService<LocalDatabase>(), sp.GetRequiredService<DataMigrationRunner>(), sp.GetRequiredService<DeviceState>(), appVersion));

        // Sign-in and sessions (Spec 010 §6). The host registers ISecretStore (SecureStorage).
        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<IAuthApi>(_ => new AuthClient(new HttpClient { BaseAddress = apiBaseAddress, Timeout = HttpTimeout }));
        services.AddSingleton(sp => new TokenRefresher(
            sp.GetRequiredService<ISessionStore>(), sp.GetRequiredService<IAuthApi>(), sp.GetRequiredService<Account>(), sp.GetRequiredService<TimeProvider>()));
        HttpClient Authenticated(IServiceProvider sp) =>
            new(new AuthenticatingHandler(sp.GetRequiredService<TokenRefresher>()) { InnerHandler = new HttpClientHandler() })
            {
                BaseAddress = apiBaseAddress,
                Timeout = HttpTimeout,
            };

        services.AddSingleton<IAccountApi>(sp => new AccountClient(Authenticated(sp)));

        // Sync (Spec 011 §7.1): the same refreshing handler.
        services.AddSingleton(sp => new SyncClient(Authenticated(sp)));
        services.AddSingleton<ISyncApi>(sp => sp.GetRequiredService<SyncClient>());
        services.AddSingleton<IConsentApi>(sp => sp.GetRequiredService<SyncClient>());

        services.AddSingleton<Coo.Mobile.Application.AppVersion.IAppVersionApi>(sp => new VersionClient(Authenticated(sp)));

        // Members and invitations (Spec 011b §10.1): the same refreshing handler.
        services.AddSingleton<IHouseholdApi>(sp => new HouseholdClient(Authenticated(sp)));

        // Advisor
        services.AddSingleton<IAdvisorApi>(sp => new AdvisorClient(Authenticated(sp)));
        services.AddSingleton<Coo.Mobile.Application.Billing.IBillingApi>(sp => new Billing.BillingClient(Authenticated(sp)));

        // AI logging (ADR 0005): the parse endpoint, and words kept on the phone until they can be parsed.
        services.AddSingleton<Coo.Mobile.Application.AiLogging.IAiParseApi>(sp => new AiLogging.AiParseClient(Authenticated(sp)));
        services.AddSingleton<Coo.Mobile.Application.AiLogging.IPendingNoteStore, Persistence.PendingNoteStore>();
        services.AddSingleton<IAdvisorThreadStore, AdvisorThreadStore>();

        // Baby Avatar
        services.AddSingleton<Coo.Mobile.Application.Households.IBabyAvatarApi>(sp => new Households.BabyAvatarClient(Authenticated(sp)));

        return services;
    }
}
