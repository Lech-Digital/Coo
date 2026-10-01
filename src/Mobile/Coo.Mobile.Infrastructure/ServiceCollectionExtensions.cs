using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Preferences;
using Coo.Mobile.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Mobile.Infrastructure;

/// <summary>The one public entry point of this project (ADR 0001 §2).</summary>
public static class ServiceCollectionExtensions
{
    /// <param name="databasePath">The SQLite file, inside the app's private data folder.</param>
    /// <param name="appVersion">The running app version: the integrity scan runs once per version (Spec 004 §7).</param>
    public static IServiceCollection AddMobileInfrastructure(this IServiceCollection services, string databasePath, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);

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

        // There are no data migrations in v1 (Spec 004 §7). New ones are registered here, by hand.
        services.AddSingleton(sp => new DataMigrationRunner([], sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAppStartup>(sp => new AppStartup(
            sp.GetRequiredService<LocalDatabase>(), sp.GetRequiredService<DataMigrationRunner>(), sp.GetRequiredService<DeviceState>(), appVersion));
        return services;
    }
}
