using Coo.Mobile.Application;
using Coo.Mobile.Infrastructure;
using Coo.Mobile.Platform;

namespace Coo.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Composition root (ADR 0001 §2): the only place on the mobile side that creates the real clock.
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddMobileApplication();
        builder.Services.AddMobileInfrastructure(
            databasePath: Path.Combine(FileSystem.AppDataDirectory, "coo.db"),
            appVersion: $"{AppInfo.Current.VersionString}+{AppInfo.Current.BuildString}");
        builder.Services.AddMobilePlatform();
        builder.Services.AddTransient<StartupPage>();
        builder.Services.AddTransient<PlaceholderPage>();
#if DEBUG_SCREEN
        builder.Services.AddTransient<DebugScreen.DebugPage>();
#endif

        return builder.Build();
    }
}
