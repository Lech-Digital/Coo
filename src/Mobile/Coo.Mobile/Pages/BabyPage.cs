using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Theme;

namespace Coo.Mobile.Pages;

/// <summary>The baby (Spec 009 §3.1): name, nicknames and dates, one Save. A pushed page, so the keyboard and Back work.</summary>
public sealed class BabyPage : ContentPage
{
    private readonly BabyProfileViewModel _model;
    private AvatarPickerSheet? _pickerSheet;

    public BabyPage(BabyProfileViewModel model)
    {
        _model = model;
        BindingContext = model;
        Shell.SetNavBarIsVisible(this, false);
        model.Saved += async (_, _) => await Navigation.PopAsync();
    }

    /// <summary>Back returns to Settings (Shell does not pop pushed pages on Back; found in Increment 7).</summary>
    protected override bool OnBackButtonPressed()
    {
        if (_pickerSheet?.IsVisible == true)
        {
            Dispatcher.Dispatch(async () => await _pickerSheet.HideAsync());
            return true;
        }
        Dispatcher.Dispatch(async () => await Navigation.PopAsync());
        return true;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _model.LoadAsync();
        _pickerSheet = new AvatarPickerSheet(_model);
        var pageRoot = new Grid
        {
            Children =
            {
                new ScrollView { Content = Build() },
                _pickerSheet,
            }
        };
        Content = pageRoot;
    }

    private VerticalStackLayout Build()
    {
        var avatarHero = BuildAvatarHero();
        var name = TextEntry(nameof(BabyProfileViewModel.Name), Strings.Get("Setup.Name"));
        var nicknames = TextEntry(nameof(BabyProfileViewModel.Nicknames), Strings.Get("Baby.NicknamesHint"));

        var birthLabel = Ui.Text(Strings.Get("Setup.BirthDate"), "Label");
        var birth = DatePicker(nameof(BabyProfileViewModel.BirthDate), max: _model.Today);
        var early = new CheckBox();
        early.SetBinding(CheckBox.IsCheckedProperty, nameof(BabyProfileViewModel.HasDueDate));
        early.SetDynamicResource(CheckBox.ColorProperty, "Accent");
        var bornEarly = new HorizontalStackLayout { Spacing = 8, Children = { early, Ui.Text(Strings.Get("Setup.BornEarly"), "Body").Tap(l => l.VerticalOptions = LayoutOptions.Center) } };
        var dueLabel = Ui.Text(Strings.Get("Setup.DueDate"), "Label");
        var due = DatePicker(nameof(BabyProfileViewModel.DueDate), max: _model.Today.AddDays(308));

        var error = Ui.Text("", "Body", "FeedInk");
        error.SetBinding(Label.TextProperty, nameof(BabyProfileViewModel.Error));
        var save = new Button { Text = Strings.Get("Action.Save"), Command = _model.SaveCommand, Margin = new Thickness(0, 16, 0, 0) };
        save.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");

        void Sync()
        {
            birthLabel.IsVisible = birth.IsVisible = bornEarly.IsVisible = _model.IsBorn;
            dueLabel.IsVisible = due.IsVisible = !_model.IsBorn || _model.HasDueDate;
        }

        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BabyProfileViewModel.HasDueDate) or nameof(BabyProfileViewModel.IsBorn))
                Sync();
            if (e.PropertyName is nameof(BabyProfileViewModel.AvatarRef))
                RebuildAvatarHero(avatarHero);
        };
        Sync();

        return new VerticalStackLayout
        {
            Padding = new Thickness(24, 32, 24, 32),
            Spacing = 14,
            Children =
            {
                Ui.BackHeader(Strings.Get("Settings.Baby"), () => Navigation.PopAsync()),
                avatarHero,
                Ui.Text(Strings.Get("Setup.Name"), "Label").Tap(l => l.Margin = new Thickness(0, 8, 0, 0)),
                name,
                Ui.Text(Strings.Get("Baby.Nicknames"), "Label"),
                nicknames,
                birthLabel,
                birth,
                bornEarly,
                dueLabel,
                due,
                error,
                save,
            },
        };
    }

    private ContentView BuildAvatarHero()
    {
        var container = new ContentView { HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 8, 0, 8) };
        RebuildAvatarHero(container);
        return container;
    }

    private void RebuildAvatarHero(ContentView container)
    {
        var initial = _model.Name.Length > 0 ? _model.Name[..1].ToUpper(Strings.Culture) : "B";
        var avatar = CooAvatar.Create(_model.AvatarRef, initial, size: 110);

        var editIcon = new Label
        {
            Text = "📷",
            FontSize = 16,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
        var editBadge = Ui.Rounded(editIcon, "Cheek", 999, new Thickness(0), "Line");
        editBadge.WidthRequest = editBadge.HeightRequest = 34;
        editBadge.HorizontalOptions = LayoutOptions.End;
        editBadge.VerticalOptions = LayoutOptions.End;

        var avatarGrid = new Grid
        {
            WidthRequest = 110,
            HeightRequest = 110,
            Children = { avatar, editBadge }
        };

        var label = Ui.Text("Change Avatar or Photo", "Label", "Ink2");
        label.HorizontalOptions = LayoutOptions.Center;

        var stack = new VerticalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children = { avatarGrid, label }
        };

        stack.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await ShowAvatarOptionsAsync())
        });

        container.Content = stack;
    }

    private async Task ShowAvatarOptionsAsync()
    {
        var isPremium = _model.IsPremium;
        var magicAiOption = isPremium ? "✨ Generate Magic AI Avatar (Premium)" : "✨ Generate Magic AI Avatar (Upgrade to Premium)";
        var hasLocalPhoto = _model.AvatarRef != null && _model.AvatarRef.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

        var options = new List<string>
        {
            "🐻 Choose Preset Avatar",
            "📷 Take Photo",
            "🖼️ Choose from Gallery",
        };
        if (hasLocalPhoto)
            options.Add("✂️ Adjust Current Photo");
        options.Add(magicAiOption);

        var action = await DisplayActionSheetAsync(
            "Baby Avatar",
            "Cancel",
            _model.AvatarRef != null ? "Remove Avatar (Use Monogram)" : null,
            options.ToArray());

        if (action == "🐻 Choose Preset Avatar")
        {
            await ShowPresetSelectorAsync();
        }
        else if (action == "📷 Take Photo")
        {
            await TakePhotoAsync();
        }
        else if (action == "🖼️ Choose from Gallery")
        {
            await PickPhotoAsync();
        }
        else if (action == "✂️ Adjust Current Photo" && hasLocalPhoto)
        {
            var path = _model.AvatarRef!["file:".Length..];
            if (File.Exists(path))
            {
                await Navigation.PushAsync(new AdjustAvatarPage(path, _model));
            }
        }
        else if (action == magicAiOption)
        {
            if (!isPremium)
            {
                await DisplayAlertAsync("Coo Premium", "Magic AI Avatars are exclusively available for Coo Premium subscribers.", "OK");
                return;
            }
            await GenerateMagicAvatarAsync();
        }
        else if (action == "Remove Avatar (Use Monogram)")
        {
            await _model.ClearAvatarCommand.ExecuteAsync(null);
        }
    }

    private async Task ShowPresetSelectorAsync()
    {
        if (_pickerSheet != null)
        {
            await _pickerSheet.ShowAsync();
        }
    }

    private async Task TakePhotoAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    var id = Guid.CreateVersion7(_model.Time.GetUtcNow()).ToString("N");
                    var targetPath = Path.Combine(FileSystem.AppDataDirectory, $"raw_avatar_{id}.jpg");
                    await using (var stream = await photo.OpenReadAsync())
                    await using (var targetStream = File.Create(targetPath))
                    {
                        await stream.CopyToAsync(targetStream);
                    }
                    await Navigation.PushAsync(new AdjustAvatarPage(targetPath, _model));
                }
            }
            else
            {
                await DisplayAlertAsync("Camera", "Camera capture is not supported on this device.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Could not capture photo: {ex.Message}", "OK");
        }
    }

    private async Task PickPhotoAsync()
    {
        try
        {
            var photos = await MediaPicker.Default.PickPhotosAsync();
            var photo = photos?.FirstOrDefault();
            if (photo != null)
            {
                var id = Guid.CreateVersion7(_model.Time.GetUtcNow()).ToString("N");
                var targetPath = Path.Combine(FileSystem.AppDataDirectory, $"raw_avatar_{id}.jpg");
                await using (var stream = await photo.OpenReadAsync())
                await using (var targetStream = File.Create(targetPath))
                {
                    await stream.CopyToAsync(targetStream);
                }
                await Navigation.PushAsync(new AdjustAvatarPage(targetPath, _model));
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Could not select photo: {ex.Message}", "OK");
        }
    }

    private async Task GenerateMagicAvatarAsync()
    {
        try
        {
            var photos = await MediaPicker.Default.PickPhotosAsync();
            var photo = photos?.FirstOrDefault();
            if (photo == null)
                return;

            using var memStream = new MemoryStream();
            await using (var stream = await photo.OpenReadAsync())
            {
                await stream.CopyToAsync(memStream);
            }
            var base64 = Convert.ToBase64String(memStream.ToArray());

            var id = Guid.CreateVersion7(_model.Time.GetUtcNow()).ToString("N");
            var targetPath = Path.Combine(FileSystem.AppDataDirectory, $"ai_avatar_{id}.png");
            var success = await _model.GenerateAiAvatarAsync(base64, targetPath);
            if (!success && !string.IsNullOrEmpty(_model.Error))
            {
                await DisplayAlertAsync("AI Avatar", _model.Error, "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Could not generate avatar: {ex.Message}", "OK");
        }
    }

    private static View TextEntry(string property, string placeholder)
    {
        var entry = new Entry { Placeholder = placeholder, FontFamily = "FigtreeRegular", FontSize = 17 };
        entry.SetBinding(Entry.TextProperty, property);
        entry.SetDynamicResource(Entry.TextColorProperty, "Ink");
        entry.SetDynamicResource(Entry.PlaceholderColorProperty, "Ink2");
        return Ui.Rounded(entry, "Surface", 12, new Thickness(16, 4), "Line");
    }

    private static View DatePicker(string property, DateTime max)
    {
        var picker = new DatePicker { MaximumDate = max, FontFamily = "FigtreeRegular", FontSize = 17, Format = "D" };
        picker.SetBinding(Microsoft.Maui.Controls.DatePicker.DateProperty, property);
        picker.SetDynamicResource(Microsoft.Maui.Controls.DatePicker.TextColorProperty, "Ink");
        return Ui.Rounded(picker, "Surface", 12, new Thickness(16, 4), "Line");
    }
}
