namespace Coo.Mobile.Components;

/// <summary>
/// Tabular figures, candidate 1 (Spec 005 §4.2): labels with the "Tabular" style class get Android's
/// <c>fontFeatureSettings = "tnum"</c>, so Figtree's fixed-width digits are used and timers do not jitter.
/// </summary>
internal static class Tabular
{
    public const string StyleClass = "Tabular";

    public static void Register() =>
        Microsoft.Maui.Handlers.LabelHandler.Mapper.AppendToMapping("CooTabular", (handler, label) =>
        {
#if ANDROID
            if (label is Label { StyleClass: { } classes } && classes.Contains(StyleClass))
                handler.PlatformView.FontFeatureSettings = "tnum";
#endif
        });
}
