using Coo.Mobile.Presentation.Attention;
using Coo.Mobile.Presentation.Edit;
using Coo.Mobile.Presentation.Localization;
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
        services.AddSingleton<LanguageController>();
        services.AddSingleton<UndoService>();
        services.AddSingleton(sp => new HomeActions(
            sp.GetRequiredService<Coo.Mobile.Application.CareLog.LogEntry>(), sp.GetRequiredService<Coo.Mobile.Application.CareLog.ReviseEntry>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<UndoService>()));
        services.AddSingleton<IHomeSection, BabyHeaderSection>();
        services.AddSingleton<IHomeSection, DueDateSection>();
        services.AddSingleton<IHomeSection, StatusHeroSection>();
        services.AddSingleton<IHomeSection, NightSection>();
        services.AddSingleton<IHomeSection, AttentionSection>();
        services.AddSingleton<IHomeSection, RunningSection>();
        services.AddSingleton<IHomeSection, LogTilesSection>();
        services.AddSingleton<IHomeSection, TimelineSection>();
        services.AddSingleton<TodayViewModel>();
        services.AddTransient<SetupViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<Settings.SettingsViewModel>();
        services.AddSingleton<Settings.AccountViewModel>();
        services.AddSingleton<Settings.FamilySectionViewModel>();
        services.AddTransient<Settings.FamilyPageViewModel>();
        services.AddTransient<Settings.BabyProfileViewModel>();
        services.AddTransient<Settings.ConsentViewModel>();
        services.AddTransient<Settings.BringOverViewModel>();
        services.AddTransient<EditViewModel>();
        services.AddTransient<AttentionViewModel>();
        return services;
    }
}
