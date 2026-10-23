using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;
using Coo.Mobile.Application.Notifications;

namespace Coo.Mobile;

/// <summary>
/// The home-screen widget's adapter: the app hands it the lines after every write and sync, it keeps them in shared
/// preferences (a widget added later, or redrawn by the launcher, shows them without the app) and redraws every widget.
/// </summary>
public sealed class HomeWidget : IHomeWidget
{
    private const string Store = "coo.widget";

    public void Show(WidgetContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var context = global::Android.App.Application.Context;
        context.GetSharedPreferences(Store, FileCreationMode.Private)?.Edit()?
            .PutString("title", content.Title)?
            .PutString("feed", content.Feed)?
            .PutString("sleep", content.Sleep)?
            .PutString("diaper", content.Diaper)?
            .PutString("tell", Coo.Mobile.Presentation.Localization.Strings.Get("Widget.Tell"))?
            .Apply();
        var manager = AppWidgetManager.GetInstance(context);
        var ids = manager?.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(CooWidgetProvider))));
        if (manager is not null && ids is { Length: > 0 })
            manager.UpdateAppWidget(ids, Views(context));
    }

    internal static RemoteViews Views(Context context)
    {
        var stored = context.GetSharedPreferences(Store, FileCreationMode.Private);
        string Read(string key, string fallback) => stored?.GetString(key, null) ?? fallback;

        var views = new RemoteViews(context.PackageName, Resource.Layout.coo_widget);
        views.SetTextViewText(Resource.Id.widget_title, Read("title", "Coo"));
        views.SetTextViewText(Resource.Id.widget_feed, Read("feed", ""));
        var sleep = Read("sleep", "");
        views.SetTextViewText(Resource.Id.widget_sleep, sleep);
        views.SetViewVisibility(Resource.Id.widget_sleep, sleep.Length == 0 ? global::Android.Views.ViewStates.Gone : global::Android.Views.ViewStates.Visible);
        views.SetTextViewText(Resource.Id.widget_diaper, Read("diaper", ""));
        views.SetTextViewText(Resource.Id.widget_tell, Read("tell", "Tell Coo"));

        var open = new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        views.SetOnClickPendingIntent(Resource.Id.widget_root,
            PendingIntent.GetActivity(context, 1, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
        var tell = new Intent(context, typeof(MainActivity)).SetAction(VoiceShortcut.Action).SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        views.SetOnClickPendingIntent(Resource.Id.widget_tell,
            PendingIntent.GetActivity(context, 2, tell, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable));
        return views;
    }
}

/// <summary>The widget as Android knows it: drawn from what the app last stored.</summary>
[BroadcastReceiver(Exported = true, Label = "Coo")]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/coo_widget_info")]
public sealed class CooWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null || appWidgetManager is null || appWidgetIds is null)
            return;
        appWidgetManager.UpdateAppWidget(appWidgetIds, HomeWidget.Views(context));
    }
}
