using Coo.Domain.CareLog.Kinds.Feeding;
using Coo.Mobile.Application.CareLog;
using Coo.Mobile.Presentation.Localization;

namespace Coo.Mobile.Presentation.Notifications;

/// <summary>The short confirmation after a one-tap log from a widget or a notification: "Coo: Logged · Wet".</summary>
public static class QuickActionText
{
    public static string Describe(QuickActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Done is not { } done)
            return Strings.Error(result.Error);
        var what = done switch
        {
            QuickAction.FeedLeft or QuickAction.FeedRight =>
                Strings.Get(result.Side == BreastSide.Right || (result.Side is null && done == QuickAction.FeedRight) ? "Hero.FeedingRight" : "Hero.FeedingLeft"),
            QuickAction.SwitchSide => Strings.Get("Action.SwitchSide"),
            QuickAction.PauseFeed => Strings.Get("Hero.FeedingPaused"),
            QuickAction.StopFeed => Strings.Get("Kind.Feed"),
            QuickAction.Wet => Strings.Get("Diaper.Wet"),
            QuickAction.Dirty => Strings.Get("Diaper.Dirty"),
            QuickAction.Mixed => Strings.Get("Diaper.Mixed"),
            QuickAction.Sleep => Strings.Get("Hero.Sleeping"),
            QuickAction.WokeUp => Strings.Get("Action.WokeUp"),
            QuickAction.Pump => Strings.Get("Hero.Pumping"),
            _ => Strings.Get("Kind.Pump"),
        };
        return Strings.Format("Widget.Logged", Strings.Format("Toast.Logged", what));
    }
}
