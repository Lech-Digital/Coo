using Coo.Mobile.Pages;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile;

/// <summary>Two tabs for now, Today and Log (Spec 006 §2, Q1), styled as the design system's bottom navigation.</summary>
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
                    ContentTemplate = new DataTemplate(services.GetRequiredService<TodayPage>),
                },
                new ShellContent
                {
                    Title = Strings.Get("Tab.Log"),
                    Icon = "tab_log.png",
                    ContentTemplate = new DataTemplate(services.GetRequiredService<LogPage>),
                },
            },
        });
    }
}
