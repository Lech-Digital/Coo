using Microsoft.Extensions.DependencyInjection;

namespace Coo.Mobile.Platform;

/// <summary>The one public entry point of this project (ADR 0001 §2).</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMobilePlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
