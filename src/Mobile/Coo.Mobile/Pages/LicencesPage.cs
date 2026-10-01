using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;

namespace Coo.Mobile.Pages;

/// <summary>Open-source licences (Spec 009 §3.6): one card per component; a tap shows its full licence text.</summary>
public sealed class LicencesPage : ContentPage
{
    public LicencesPage()
    {
        Shell.SetNavBarIsVisible(this, false);
        var stack = new VerticalStackLayout { Spacing = 12, Padding = new Thickness(24, 32, 24, 32) };
        stack.Add(Ui.BackHeader(Strings.Get("Settings.Licences"), () => Navigation.PopAsync()));
        foreach (var licence in Licences.All)
            stack.Add(LicenceCard(licence));
        Content = new ScrollView { Content = stack };
    }

    protected override bool OnBackButtonPressed()
    {
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    private static Card LicenceCard(Licence licence)
    {
        var text = Ui.Text("", "Meta", "Ink2").Tap(l => l.IsVisible = false);
        var card = new Card(new VerticalStackLayout
        {
            Spacing = 4,
            Children = { Ui.Text(licence.Name, "BodyStrong"), Ui.Text(licence.LicenceName, "Meta", "Ink2"), text },
        });
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                if (string.IsNullOrEmpty(text.Text))
                {
                    try
                    {
                        await using var stream = await FileSystem.OpenAppPackageFileAsync($"licences/{licence.AssetFile}");
                        using var reader = new StreamReader(stream);
                        text.Text = await reader.ReadToEndAsync();
                    }
                    catch (FileNotFoundException)
                    {
                        // A missing text must never crash About; the name and licence above still say what applies.
                        return;
                    }
                }

                text.IsVisible = !text.IsVisible;
            }),
        });
        return card;
    }
}
