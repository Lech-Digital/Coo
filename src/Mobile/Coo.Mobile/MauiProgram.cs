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
        builder.Services.AddMobileInfrastructure();
        builder.Services.AddMobilePlatform();
        builder.Services.AddTransient<PlaceholderPage>();

        return builder.Build();
    }
}
