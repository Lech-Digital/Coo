using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Application.Households;
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
        return services;
    }
}
