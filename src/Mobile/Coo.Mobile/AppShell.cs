using Coo.Mobile.Pages;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile;

/// <summary>Five tabs: Today, Log, Stats, Advisor and Settings (Spec 006 §2, Spec 009 §2), styled as the design system's bottom navigation.</summary>
public sealed class AppShell : Shell
{
    private static readonly string[] TitleKeys = ["Tab.Today", "Tab.Log", "Tab.Stats", "Tab.Advisor", "Tab.Settings"];

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
                    Title = Strings.Get("Tab.Stats"),
                    Icon = "tab_stats.png",
                    Route = "stats",
                    ContentTemplate = new DataTemplate(services.GetRequiredService<StatsPage>),
                },
                new ShellContent
                {
                    Title = Strings.Get("Tab.Advisor"),
                    Icon = "tab_advisor.png",
                    Route = "advisor",
                    ContentTemplate = new DataTemplate(services.GetRequiredService<AdvisorPage>),
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

    /// <summary>
    /// A language change relabels the tabs in place (Spec 009 §3.5); the pages redraw themselves. Rebuilding the whole
    /// shell instead took over a second on the Pixel 10.
    /// </summary>
    public void Relabel()
    {
        var sections = Items[0].Items;
        for (var i = 0; i < sections.Count && i < TitleKeys.Length; i++)
        {
            var title = Strings.Get(TitleKeys[i]);
            sections[i].Title = title;
            foreach (var content in sections[i].Items)
                content.Title = title;
        }
    }
}
