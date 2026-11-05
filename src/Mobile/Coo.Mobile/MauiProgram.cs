using Coo.Mobile.Application;
using Coo.Mobile.Components;
using Coo.Mobile.Infrastructure;
using Coo.Mobile.Pages;
using Coo.Mobile.Platform;
using Coo.Mobile.Presentation;
using Coo.Mobile.Presentation.Theme;
using Coo.Mobile.Resources.Styles;

namespace Coo.Mobile;

public static class MauiProgram
{
    private static readonly string[] Weights = ["Light", "Regular", "Medium", "SemiBold", "Bold"];

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().ConfigureFonts(fonts =>
        {
            // One alias per weight: MAUI selects weights by file on Android (Spec 005 §4.1).
            foreach (var weight in Weights)
            {
                fonts.AddFont($"Figtree-{weight}.ttf", $"Figtree{weight}");
            }
        });
        Tabular.Register();
        RemoveUnderlines();
#if ANDROID
        builder.ConfigureMauiHandlers(h => h.AddHandler<Shell, Platforms.Android.CooShellRenderer>());
#endif

        // Composition root (ADR 0001 §2): the only place on the mobile side that creates the real clock.
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddMobileApplication();
        builder.Services.AddMobileInfrastructure(
            databasePath: Path.Combine(FileSystem.AppDataDirectory, "coo.db"),
            appVersion: $"{AppInfo.Current.VersionString}+{AppInfo.Current.BuildString}",
            apiBaseAddress: BuildConstants.ApiBaseAddress);
        builder.Services.AddMobilePlatform(() => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity, BuildConstants.GoogleWebClientId);
        builder.Services.AddSingleton<Coo.Mobile.Application.Auth.ISecretStore, Auth.MauiSecretStore>();
        builder.Services.AddMobilePresentation();

        builder.Services.AddSingleton<ThemeHost>();
        builder.Services.AddSingleton<IOsAppearance>(sp => sp.GetRequiredService<ThemeHost>());

        builder.Services.AddTransient<StartupPage>();
        builder.Services.AddTransient<SetupPage>();
        builder.Services.AddTransient<TodayPage>();
        builder.Services.AddTransient<LogPage>();
        builder.Services.AddTransient<StatsPage>();
        builder.Services.AddTransient<EditPage>();
        builder.Services.AddTransient<AttentionPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<PremiumPage>();
        builder.Services.AddTransient<RemindersPage>();
        builder.Services.AddTransient<GrowthPage>();
        builder.Services.AddTransient<SleepPlanPage>();
        builder.Services.AddTransient<MilkStashPage>();
        builder.Services.AddTransient<TourPage>();
        builder.Services.AddSingleton<Coo.Mobile.Application.Notifications.IHomeWidget, HomeWidget>();
        builder.Services.AddSingleton<Coo.Mobile.Application.Notifications.ITimerNotifications, TimerNotifications>();
        builder.Services.AddTransient<BabyPage>();
        builder.Services.AddTransient<LicencesPage>();
        builder.Services.AddTransient<ConsentPage>();
        builder.Services.AddTransient<BringOverPage>();
        builder.Services.AddTransient<JoinPage>();
        builder.Services.AddTransient<EnterCodePage>();
        builder.Services.AddTransient<RemovalPage>();
        builder.Services.AddTransient<FamilyPage>();
        builder.Services.AddTransient<FamilyBringOverPage>();
        builder.Services.AddTransient<FamilySwitcherPage>();
        builder.Services.AddTransient<AdvisorPage>();
        builder.Services.AddSingleton<Presentation.Sheets.IModalPages, Sync.WindowModalPages>();
        builder.Services.AddSingleton<Presentation.Sheets.SheetNavigator>();
        builder.Services.AddSingleton<Sync.SyncPrompter>();
        builder.Services.AddSingleton<Joining.InvitationInbox>();
        builder.Services.AddSingleton<AppVersion.UpdatePrompter>();
        builder.Services.AddSingleton<Coo.Mobile.Application.Preferences.IReviewPrompter, AppVersion.StoreReviewTrigger>();
        builder.Services.AddSingleton<Coo.Mobile.Presentation.Settings.IConsentPrompt, Joining.MauiConsentPrompt>();
        builder.Services.AddSingleton<Coo.Mobile.Presentation.Settings.IAboutInfo, Settings.MauiAboutInfo>();
        builder.Services.AddSingleton<Coo.Mobile.Presentation.Settings.IExternalLauncher, Settings.MauiExternalLauncher>();
        builder.Services.AddSingleton<Coo.Mobile.Application.Households.BabyAvatarSyncService>(sp =>
            new Coo.Mobile.Application.Households.BabyAvatarSyncService(
                sp.GetRequiredService<Coo.Mobile.Application.Households.IBabyAvatarApi>(),
                sp.GetRequiredService<Coo.Mobile.Application.Preferences.IDevicePreferences>(),
                FileSystem.AppDataDirectory));
        builder.Services.AddTransient<AppShell>();
#if DEBUG_SCREEN
        builder.Services.AddTransient<DebugScreen.DebugPage>();
        builder.Services.AddTransient<DebugScreen.GalleryPage>();
#endif

        return builder.Build();
    }

    private static void RemoveUnderlines()
    {
#if ANDROID
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (h, v) => h.PlatformView.Background = null);
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("NoUnderline", (h, v) => h.PlatformView.Background = null);
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("NoUnderline", (h, v) => h.PlatformView.Background = null);
        Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping("NoUnderline", (h, v) => h.PlatformView.Background = null);
        Microsoft.Maui.Handlers.TimePickerHandler.Mapper.AppendToMapping("NoUnderline", (h, v) => h.PlatformView.Background = null);
#endif
    }
}
