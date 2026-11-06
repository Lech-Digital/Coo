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
        services.AddSingleton<IHomeSection, SiblingsSection>();
        services.AddSingleton<IHomeSection, HandoverSection>();
        services.AddSingleton<IHomeSection, LastNightSection>();
        services.AddSingleton<IHomeSection, DueDateSection>();
        services.AddSingleton<IHomeSection, StatusHeroSection>();
        services.AddSingleton<IHomeSection, NightSection>();
        services.AddSingleton<IHomeSection, AttentionSection>();
        services.AddSingleton<IHomeSection, RunningSection>();
        services.AddSingleton<IHomeSection, LogTilesSection>();
        services.AddSingleton<IHomeSection, NextUpSection>();
        services.AddSingleton<IHomeSection, WeekSection>();
        // Daily progress, the tip of the day and recent activity left Today (owner, 2026-11-05): the Log's totals compare
        // with the usual day, the Advisor has the tip, and the Log is the history.
        services.AddSingleton<IHomeSection, TimelineSection>();
        services.AddSingleton<IHomeSection, PausedSection>();
        services.AddSingleton<IHomeSection, ViewOnlySection>();
        services.AddTransient<Settings.ImportViewModel>();
        services.AddSingleton<WhoLabelsSource>();
        services.AddSingleton<TodayViewModel>();
        services.AddTransient<SetupViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<TrendsViewModel>();
        services.AddSingleton<Log.AveragesViewModel>();
        services.AddSingleton<Log.SleepPatternViewModel>();
        services.AddTransient<Tour.TourViewModel>();
        services.AddSingleton<QuickLog.QuickLogViewModel>();
        services.AddSingleton<Settings.SettingsViewModel>();
        services.AddTransient<Settings.PremiumViewModel>();
        services.AddTransient<Settings.RemindersViewModel>();
        services.AddTransient<Growth.GrowthViewModel>();
        services.AddTransient<SleepPlan.SleepPlanViewModel>();
        services.AddTransient<MilkStash.MilkStashViewModel>();
        services.AddSingleton<Log.WeeklySummaryViewModel>();
        services.AddSingleton<Reports.ReportViewModel>();
        services.AddSingleton<Notifications.ReminderRefresher>();
        services.AddSingleton<Settings.AccountViewModel>();
        services.AddSingleton<Settings.FamilySectionViewModel>();
        services.AddTransient<Settings.FamilyPageViewModel>();
        services.AddTransient<Settings.BabyProfileViewModel>();
        services.AddTransient<Settings.ConsentViewModel>();
        services.AddTransient<Settings.BringOverViewModel>();
        services.AddTransient<Settings.FamilyBringOverViewModel>();
        services.AddTransient<Settings.JoinViewModel>();
        services.AddTransient<Settings.EnterCodeViewModel>();
        services.AddTransient<Settings.RemovalViewModel>();
        services.AddTransient<EditViewModel>();
        services.AddTransient<AttentionViewModel>();
        services.AddTransient<Advisor.AdvisorViewModel>();
        return services;
    }
}
