using Coo.Mobile.Pages;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile;

/// <summary>Three tabs: Today, Log and Settings (Spec 006 §2, Spec 009 §2), styled as the design system's bottom navigation.</summary>
public sealed class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        SetDynamicResource(BackgroundColorProperty, "Ground");
        SetDynamicResource(TabBarBackgroundColorProperty, "Surface");
        SetDynamicResource(TabBarForegroundColorProperty, "Ink");
        SetDynamicResource(TabBarTitleColorProperty, "Ink");
        SetDynamicResource(TabBarUnselectedColorProperty, "Ink2");
        FlyoutBehavior = FlyoutBehavior.Disabled;

        Items.Add(new TabBar
        {
            Items =
            {
                new ShellContent
                {
                    Title = Strings.Get("Tab.Today"),
                    Icon = "tab_today.png",
                    Route = "today",
                    ContentTemplate = new DataTemplate(services.GetRequiredService<TodayPage>),
                },
                new ShellContent
                {
                    Title = Strings.Get("Tab.Log"),
                    Icon = "tab_log.png",
                    Route = "log",
                    ContentTemplate = new DataTemplate(services.GetRequiredService<LogPage>),
                },
                new ShellContent
                {
                    Title = Strings.Get("Tab.Settings"),
                    Icon = "tab_settings.png",
                    Route = "settings",
                    ContentTemplate = new DataTemplate(services.GetRequiredService<SettingsPage>),
                },
            },
        });
    }
}
