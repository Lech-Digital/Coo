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
        var loadingIndicator = new ActivityIndicator
        {
            IsRunning = true,
            Color = Color.FromArgb("#70C1B3"),
            WidthRequest = 48,
            HeightRequest = 48,
            HorizontalOptions = LayoutOptions.Center,
        };
        var loadingText = Ui.Text("Creating Magic AI Avatar…", "Body", "Ink");
        loadingText.HorizontalOptions = LayoutOptions.Center;
        var loadingSubtext = Ui.Text("Analyzing photo and illustrating baby features", "Label", "Ink2");
        loadingSubtext.HorizontalOptions = LayoutOptions.Center;

        var loadingCard = new Card(new VerticalStackLayout
        {
            Spacing = 12,
            Padding = new Thickness(24, 20),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { loadingIndicator, loadingText, loadingSubtext }
        });
        loadingCard.HorizontalOptions = LayoutOptions.Center;
        loadingCard.VerticalOptions = LayoutOptions.Center;

        var loadingOverlay = new Grid
        {
            BackgroundColor = Color.FromRgba(0, 0, 0, 0.4),
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            InputTransparent = false,
            Children = { loadingCard }
        };
        loadingOverlay.SetBinding(VisualElement.IsVisibleProperty, new Binding(nameof(BabyProfileViewModel.IsGeneratingAvatar), source: _model));

        var pageRoot = new Grid
        {
            Children =
            {
                new ScrollView { Content = Build() },
                _pickerSheet,
                loadingOverlay,
            }
        };
        Content = pageRoot;
    }

    private VerticalStackLayout Build()
    {
        var avatarHero = BuildAvatarHero();
        var name = TextEntry(nameof(BabyProfileViewModel.Name), Strings.Get("Setup.Name"));

        var girl = new Button { Text = Strings.Get("Baby.Girl"), Command = _model.ChooseSexCommand, CommandParameter = Coo.Domain.Households.BabySex.Girl };
        var boy = new Button { Text = Strings.Get("Baby.Boy"), Command = _model.ChooseSexCommand, CommandParameter = Coo.Domain.Households.BabySex.Boy };

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
            girl.SetDynamicResource(VisualElement.StyleProperty, _model.Sex == Coo.Domain.Households.BabySex.Girl ? "PrimaryButton" : "GhostButton");
            boy.SetDynamicResource(VisualElement.StyleProperty, _model.Sex == Coo.Domain.Households.BabySex.Boy ? "PrimaryButton" : "GhostButton");
            birthLabel.IsVisible = birth.IsVisible = bornEarly.IsVisible = _model.IsBorn;
            dueLabel.IsVisible = due.IsVisible = !_model.IsBorn || _model.HasDueDate;
        }

        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BabyProfileViewModel.HasDueDate) or nameof(BabyProfileViewModel.IsBorn) or nameof(BabyProfileViewModel.Sex))
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
                Ui.Text(Strings.Get("Baby.Sex"), "Label"),
                new HorizontalStackLayout { Spacing = 8, Children = { girl, boy } },
                Ui.Text(Strings.Get("Baby.SexHint"), "Meta", "Ink2"),
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

    /// <summary>The avatar's choices in Coo's dialog: a preset, a photo, adjusting it, the AI illustration, or none.</summary>
    private async Task ShowAvatarOptionsAsync()
    {
        var isPremium = _model.IsPremium;
        var hasLocalPhoto = _model.AvatarRef != null && _model.AvatarRef.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        var actions = new List<(string Label, Func<Task> Run)>
        {
            (Strings.Get("Avatar.Preset"), ShowPresetSelectorAsync),
            (Strings.Get("Photo.Take"), TakePhotoAsync),
            (Strings.Get("Photo.Choose"), PickPhotoAsync),
        };
        if (hasLocalPhoto)
        {
            actions.Add((Strings.Get("Avatar.Adjust"), async () =>
            {
                var path = _model.AvatarRef!["file:".Length..];
                if (File.Exists(path))
                    await Navigation.PushAsync(new AdjustAvatarPage(path, _model));
            }));
        }

        actions.Add((Strings.Get(isPremium ? "Avatar.Magic" : "Avatar.MagicPremium"), async () =>
        {
            if (!isPremium)
            {
                await Components.CooDialog.AlertAsync(this, "Coo Premium", Strings.Get("Avatar.PremiumOnly"));
                return;
            }

            await GenerateMagicAvatarAsync();
        }));
        if (_model.AvatarRef != null)
            actions.Add((Strings.Get("Avatar.Remove"), () => _model.ClearAvatarCommand.ExecuteAsync(null)));

        var chosen = await Components.CooDialog.ChooseAsync(this, Strings.Get("Avatar.Title"), [.. actions.Select(a => a.Label)]);
        if (chosen >= 0)
            await actions[chosen].Run();
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
                if (!await Components.CooDialog.PrimeAsync<Permissions.Camera>(this, "Permission.CameraTitle", "Permission.CameraBody"))
                    return;
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
                await Components.CooDialog.AlertAsync(this, Strings.Get("Avatar.Title"), Strings.Get("Avatar.NoCamera"));
            }
        }
#pragma warning disable CA1031 // The camera refusing says so in Coo's words; it never takes the page down.
        catch (Exception)
#pragma warning restore CA1031
        {
            await Components.CooDialog.AlertAsync(this, Strings.Get("Avatar.Title"), Strings.Get("Error.Generic"));
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
#pragma warning disable CA1031 // As above.
        catch (Exception)
#pragma warning restore CA1031
        {
            await Components.CooDialog.AlertAsync(this, Strings.Get("Avatar.Title"), Strings.Get("Error.Generic"));
        }
    }

    private async Task GenerateMagicAvatarAsync()
    {
        try
        {
            var sourceChoice = await Components.CooDialog.ChooseAsync(this, Strings.Get("Avatar.MagicSource"), [Strings.Get("Photo.Take"), Strings.Get("Photo.Choose")]);
            if (sourceChoice < 0)
                return;

            FileResult? photo = null;
            if (sourceChoice == 0)
            {
                if (!MediaPicker.Default.IsCaptureSupported)
                {
                    await Components.CooDialog.AlertAsync(this, Strings.Get("Avatar.Title"), Strings.Get("Avatar.NoCamera"));
                    return;
                }
                photo = await MediaPicker.Default.CapturePhotoAsync();
            }
            else
            {
                var photos = await MediaPicker.Default.PickPhotosAsync();
                photo = photos?.FirstOrDefault();
            }

            if (photo == null)
                return;

            _model.IsGeneratingAvatar = true;
            string base64;
            try
            {
                base64 = await PrepareImageForAiAsync(photo);
            }
            catch
            {
                _model.IsGeneratingAvatar = false;
                throw;
            }

            var id = Guid.CreateVersion7(_model.Time.GetUtcNow()).ToString("N");
            var targetPath = Path.Combine(FileSystem.AppDataDirectory, $"ai_avatar_{id}.png");
            var success = await _model.GenerateAiAvatarAsync(base64, targetPath);
            if (!success)
            {
                var errorMsg = !string.IsNullOrEmpty(_model.Error) ? _model.Error : Strings.Get("Avatar.Failed");
                await Components.CooDialog.AlertAsync(this, Strings.Get("Avatar.Title"), errorMsg);
            }
            else
            {
                await Components.CooDialog.AlertAsync(this, Strings.Get("Avatar.Title"), Strings.Get("Avatar.Done"));
            }
        }
#pragma warning disable CA1031 // As above.
        catch (Exception)
#pragma warning restore CA1031
        {
            await Components.CooDialog.AlertAsync(this, Strings.Get("Avatar.Title"), Strings.Get("Avatar.Failed"));
        }
    }

    private Task<string> PrepareImageForAiAsync(FileResult photo)
    {
        return Task.Run(async () =>
        {
            var id = Guid.CreateVersion7(_model.Time.GetUtcNow()).ToString("N");
            var tempPath = Path.Combine(FileSystem.CacheDirectory, $"temp_ai_{id}.jpg");
            try
            {
                await using (var inStream = await photo.OpenReadAsync())
                await using (var outStream = File.Create(tempPath))
                {
                    await inStream.CopyToAsync(outStream);
                }

                // 1. Read EXIF orientation
                var exif = new Android.Media.ExifInterface(tempPath);
                var orientation = exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, (int)Android.Media.Orientation.Normal);
                int degrees = orientation switch
                {
                    (int)Android.Media.Orientation.Rotate90 => 90,
                    (int)Android.Media.Orientation.Rotate180 => 180,
                    (int)Android.Media.Orientation.Rotate270 => 270,
                    _ => 0
                };

                // 2. Decode bitmap
                var original = Android.Graphics.BitmapFactory.DecodeFile(tempPath)
                    ?? throw new InvalidOperationException("Could not decode selected photo.");

                Android.Graphics.Bitmap oriented;
                if (degrees != 0)
                {
                    var matrix = new Android.Graphics.Matrix();
                    matrix.PostRotate(degrees);
                    oriented = Android.Graphics.Bitmap.CreateBitmap(original, 0, 0, original.Width, original.Height, matrix, true)!;
                    if (oriented != original) original.Recycle();
                }
                else
                {
                    oriented = original;
                }

                // 3. Downscale to max 768px on longest edge
                int maxDim = 768;
                int w = oriented.Width;
                int h = oriented.Height;
                if (w > maxDim || h > maxDim)
                {
                    float ratio = Math.Min((float)maxDim / w, (float)maxDim / h);
                    int targetW = (int)(w * ratio);
                    int targetH = (int)(h * ratio);
                    var scaled = Android.Graphics.Bitmap.CreateScaledBitmap(oriented, targetW, targetH, true)!;
                    if (scaled != oriented) oriented.Recycle();
                    oriented = scaled;
                }

                // 4. Compress to clean JPEG quality 85
                using var ms = new MemoryStream();
                oriented.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 85, ms);
                oriented.Recycle();

                return Convert.ToBase64String(ms.ToArray());
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        });
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
