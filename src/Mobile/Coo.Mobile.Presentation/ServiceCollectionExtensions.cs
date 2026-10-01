using Coo.Mobile.Presentation.Log;
using Coo.Mobile.Presentation.Setup;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Presentation.Today;
using Microsoft.Extensions.DependencyInjection;

namespace Coo.Mobile.Presentation;

/// <summary>
/// Registers the view models and Today's sections. A new home section (pregnancy tips, a contraction timer) is one
/// more line here (Spec 006 §3a).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMobilePresentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ThemeController>();
        services.AddSingleton<HomeActions>();
        services.AddSingleton<IHomeSection, BabyHeaderSection>();
        services.AddSingleton<IHomeSection, DueDateSection>();
        services.AddSingleton<IHomeSection, StatusHeroSection>();
        services.AddSingleton<IHomeSection, NightSection>();
        services.AddSingleton<IHomeSection, RunningSection>();
        services.AddSingleton<IHomeSection, LogTilesSection>();
        services.AddSingleton<IHomeSection, TimelineSection>();
        services.AddSingleton<TodayViewModel>();
        services.AddTransient<SetupViewModel>();
        services.AddSingleton<LogViewModel>();
        return services;
    }
}
