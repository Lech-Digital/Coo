using Coo.Mobile.Application.Auth;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
using Coo.Mobile.Application.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Mobile.Application;

/// <summary>Registers the use cases. The host calls it from the composition root (ADR 0001 §2).</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMobileApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddTransient<LogEntry>();
        services.AddTransient<ReviseEntry>();
        services.AddTransient<DeleteEntry>();
        services.AddTransient<GetEntries>();
        services.AddTransient<EnsureProvisionalHousehold>();
        services.AddTransient<GetToday>();
        services.AddTransient<GetDay>();
        services.AddTransient<SetUpBaby>();
        services.AddTransient<RecordBirth>();
        services.AddTransient<UpdateBabyProfile>();
        services.AddTransient<ChangeVolumeUnit>();
        services.AddTransient<SetHiddenKinds>();
        services.AddTransient<GetAttention>();
        services.AddTransient<DismissDuplicate>();

        // Sign-in and sessions (Spec 010 §6).
        services.AddSingleton<Account>();
        services.AddTransient<SignIn>();
        services.AddTransient<SignOut>();
        services.AddTransient<DeleteAccount>();
        services.AddTransient<RefreshAccount>();

        // Sync (Spec 011).
        services.AddSingleton<LocalWrites>();
        services.AddTransient<ConsentGate>();
        services.AddTransient<SyncCycle>();
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<ISyncStatus>(sp => sp.GetRequiredService<SyncEngine>());
        services.AddSingleton<ISyncControl>(sp => sp.GetRequiredService<SyncEngine>());
        services.AddTransient<RecordConsent>();
        services.AddTransient<DeclineConsent>();
        services.AddTransient<BringEntriesOver>();
        return services;
    }
}
