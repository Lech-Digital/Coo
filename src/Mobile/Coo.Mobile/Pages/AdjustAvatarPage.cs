using Coo.Mobile.Components;
using Coo.Mobile.Presentation.Localization;
using Coo.Mobile.Presentation.Settings;
using Coo.Mobile.Presentation.Theme;
using Microsoft.Maui.Controls.Shapes;

namespace Coo.Mobile.Pages;

/// <summary>
/// Allows the user to adjust, pan, zoom, rotate and crop a photo avatar to fit circular framing.
/// </summary>
public sealed class AdjustAvatarPage : ContentPage
{
    private const double ViewportSize = 260.0;
    private readonly string _sourcePath;
    private readonly BabyProfileViewModel _model;

    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private double _totalPanX;
    private double _totalPanY;
    private int _rotationDegrees;
    private bool _saving;

    public AdjustAvatarPage(string sourcePath, BabyProfileViewModel model)
    {
        _sourcePath = sourcePath;
        _model = model;
        Shell.SetNavBarIsVisible(this, false);
        Content = BuildUi();
    }

    private View BuildUi()
    {
        // 1. Header
        var title = Ui.Text("Adjust Photo", "Title");
        title.HorizontalOptions = LayoutOptions.Center;
        title.VerticalOptions = LayoutOptions.Center;

        var cancelBtn = new Button
        {
            Text = "Cancel",
            FontFamily = "FigtreeMedium",
            FontSize = 15,
            Command = new Command(async () => await Navigation.PopAsync()),
        };
        cancelBtn.SetDynamicResource(VisualElement.StyleProperty, "GhostButton");

        var saveBtn = new Button
        {
            Text = "Save",
            FontFamily = "FigtreeBold",
            FontSize = 15,
            Padding = new Thickness(16, 0),
            HeightRequest = 36,
        };
        saveBtn.SetDynamicResource(VisualElement.StyleProperty, "PrimaryButton");
        saveBtn.Clicked += async (_, _) => await OnSaveAsync(saveBtn);

        var header = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            ],
            Padding = new Thickness(16, 12, 16, 12),
        };
        Grid.SetColumn((BindableObject)cancelBtn, 0);
        Grid.SetColumn((BindableObject)title, 1);
        Grid.SetColumn((BindableObject)saveBtn, 2);
        header.Children.Add(cancelBtn);
        header.Children.Add(title);
        header.Children.Add(saveBtn);

        // 2. Viewport Image and Circular Frame
        var image = new Microsoft.Maui.Controls.Image
        {
            Source = ImageSource.FromFile(_sourcePath),
            Aspect = Aspect.AspectFill,
            WidthRequest = ViewportSize,
            HeightRequest = ViewportSize,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };

        var cropContainer = new Grid
        {
            WidthRequest = ViewportSize,
            HeightRequest = ViewportSize,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { image }
        };

        var viewportBorder = new Border
        {
            Content = cropContainer,
            WidthRequest = ViewportSize,
            HeightRequest = ViewportSize,
            StrokeShape = new RoundRectangle { CornerRadius = (float)(ViewportSize / 2) },
            StrokeThickness = 3,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        viewportBorder.SetDynamicResource(Border.StrokeProperty, "AccentBrush");

        // Faint outer guide card
        var cardWrapper = new Border
        {
            Content = viewportBorder,
            WidthRequest = ViewportSize + 24,
            HeightRequest = ViewportSize + 24,
            StrokeShape = new RoundRectangle { CornerRadius = 24 },
            StrokeThickness = 1,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Padding = new Thickness(12),
        };
        cardWrapper.SetDynamicResource(Border.StrokeProperty, "LineBrush");
        cardWrapper.SetDynamicResource(Border.BackgroundProperty, "SurfaceBrush");

        // Pan gesture
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, e) =>
        {
            switch (e.StatusType)
            {
                case GestureStatus.Running:
                    _panX = _totalPanX + e.TotalX;
                    _panY = _totalPanY + e.TotalY;
                    image.TranslationX = _panX;
                    image.TranslationY = _panY;
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    _totalPanX = _panX;
                    _totalPanY = _panY;
                    break;
            }
        };
        viewportBorder.GestureRecognizers.Add(pan);

        // 3. Zoom Slider
        var zoomLabel = Ui.Text("Zoom & Position", "Label", "Ink2");
        zoomLabel.HorizontalOptions = LayoutOptions.Center;

        var zoomSlider = new Slider
        {
            Minimum = 1.0,
            Maximum = 3.0,
            Value = 1.0,
            HorizontalOptions = LayoutOptions.Fill,
        };
        zoomSlider.SetDynamicResource(Slider.ThumbColorProperty, "Accent");
        zoomSlider.SetDynamicResource(Slider.MinimumTrackColorProperty, "Accent");
        zoomSlider.SetDynamicResource(Slider.MaximumTrackColorProperty, "Line");

        zoomSlider.ValueChanged += (_, e) =>
        {
            _zoom = e.NewValue;
            image.Scale = _zoom;
        };

        // Pinch gesture
        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += (_, e) =>
        {
            if (e.Status == GestureStatus.Running)
            {
                var newZoom = Math.Clamp(_zoom * e.Scale, 1.0, 3.0);
                _zoom = newZoom;
                zoomSlider.Value = _zoom;
                image.Scale = _zoom;
            }
        };
        viewportBorder.GestureRecognizers.Add(pinch);

        var sliderRow = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            ],
            ColumnSpacing = 8,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = new Thickness(32, 0, 32, 0),
        };
        var minLabel = Ui.Text("1x", "Meta", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center);
        var maxLabel = Ui.Text("3x", "Meta", "Ink2").Tap(l => l.VerticalOptions = LayoutOptions.Center);
        Grid.SetColumn((BindableObject)minLabel, 0);
        Grid.SetColumn((BindableObject)zoomSlider, 1);
        Grid.SetColumn((BindableObject)maxLabel, 2);
        sliderRow.Children.Add(minLabel);
        sliderRow.Children.Add(zoomSlider);
        sliderRow.Children.Add(maxLabel);

        // 4. Action Buttons (Rotate & Reset)
        var rotateLabel = Ui.Text("🔄 Rotate 90°", "BodyStrong");
        rotateLabel.VerticalOptions = LayoutOptions.Center;
        var rotateButton = Ui.Rounded(rotateLabel, "Surface", 999, new Thickness(16, 8), "Line");
        rotateButton.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                _rotationDegrees = (_rotationDegrees + 90) % 360;
                image.Rotation = _rotationDegrees;
            })
        });

        var resetLabel = Ui.Text("Reset", "BodyStrong", "Ink2");
        resetLabel.VerticalOptions = LayoutOptions.Center;
        var resetButton = Ui.Rounded(resetLabel, "Surface", 999, new Thickness(16, 8), "Line");
        resetButton.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                _zoom = 1.0;
                _panX = 0;
                _panY = 0;
                _totalPanX = 0;
                _totalPanY = 0;
                _rotationDegrees = 0;
                zoomSlider.Value = 1.0;
                image.Scale = 1.0;
                image.TranslationX = 0;
                image.TranslationY = 0;
                image.Rotation = 0;
            })
        });

        var toolsRow = new HorizontalStackLayout
        {
            Spacing = 12,
            HorizontalOptions = LayoutOptions.Center,
            Children = { rotateButton, resetButton }
        };

        var hint = Ui.Text("Drag to center baby's face in the circle", "Meta", "Ink2");
        hint.HorizontalOptions = LayoutOptions.Center;

        var body = new VerticalStackLayout
        {
            Spacing = 16,
            Padding = new Thickness(0, 8, 0, 24),
            Children =
            {
                header,
                hint,
                cardWrapper,
                zoomLabel,
                sliderRow,
                toolsRow,
            }
        };

        return new ScrollView { Content = body };
    }

    private async Task OnSaveAsync(Button saveBtn)
    {
        if (_saving)
            return;
        _saving = true;
        saveBtn.IsEnabled = false;
        saveBtn.Text = "Saving…";

        try
        {
            var croppedPath = await CropAndSaveAsync(_sourcePath, _zoom, _panX, _panY, _rotationDegrees, ViewportSize, _model.Time);
            await _model.SetLocalAvatarFileAsync(croppedPath);
            await Navigation.PopAsync();
        }
#pragma warning disable CA1031 // A photo that cannot be saved says so in Coo's words.
        catch (Exception)
#pragma warning restore CA1031
        {
            await Components.CooDialog.AlertAsync(this, Coo.Mobile.Presentation.Localization.Strings.Get("Avatar.Title"), Coo.Mobile.Presentation.Localization.Strings.Get("Error.Generic"));
            saveBtn.IsEnabled = true;
            saveBtn.Text = "Save";
            _saving = false;
        }
    }

    private static Task<string> CropAndSaveAsync(string sourcePath, double zoom, double panX, double panY, int userRotationDegrees, double viewportSize, TimeProvider time)
    {
        return Task.Run(() =>
        {
            // 1. Read EXIF orientation
            var exif = new Android.Media.ExifInterface(sourcePath);
            var exifOrientation = exif.GetAttributeInt(Android.Media.ExifInterface.TagOrientation, (int)Android.Media.Orientation.Normal);
            int exifDegrees = exifOrientation switch
            {
                (int)Android.Media.Orientation.Rotate90 => 90,
                (int)Android.Media.Orientation.Rotate180 => 180,
                (int)Android.Media.Orientation.Rotate270 => 270,
                _ => 0
            };

            int totalRotation = (exifDegrees + userRotationDegrees) % 360;

            // 2. Decode bitmap
            var original = Android.Graphics.BitmapFactory.DecodeFile(sourcePath)
                ?? throw new InvalidOperationException("Could not decode image file.");

            // 3. Rotate bitmap if needed
            Android.Graphics.Bitmap oriented;
            if (totalRotation != 0)
            {
                var matrix = new Android.Graphics.Matrix();
                matrix.PostRotate(totalRotation);
                oriented = Android.Graphics.Bitmap.CreateBitmap(original, 0, 0, original.Width, original.Height, matrix, true)!;
                if (oriented != original) original.Recycle();
            }
            else
            {
                oriented = original;
            }

            int origW = oriented.Width;
            int origH = oriented.Height;

            // 4. Calculate crop rectangle corresponding to AspectFill inside viewportSize x viewportSize
            double baseScale = Math.Max(viewportSize / origW, viewportSize / origH);
            double totalScale = baseScale * zoom;

            double cropSize = viewportSize / totalScale;
            cropSize = Math.Min(cropSize, Math.Min(origW, origH));

            double cropCenterX = (origW / 2.0) - (panX / totalScale);
            double cropCenterY = (origH / 2.0) - (panY / totalScale);

            int cropLeft = (int)Math.Clamp(cropCenterX - (cropSize / 2.0), 0, Math.Max(0, origW - cropSize));
            int cropTop = (int)Math.Clamp(cropCenterY - (cropSize / 2.0), 0, Math.Max(0, origH - cropSize));
            int cropDim = (int)Math.Min(cropSize, Math.Min(origW - cropLeft, origH - cropTop));
            cropDim = Math.Max(10, cropDim);

            var cropped = Android.Graphics.Bitmap.CreateBitmap(oriented, cropLeft, cropTop, cropDim, cropDim)!;
            if (cropped != oriented) oriented.Recycle();

            // 5. Standardize to crisp 512x512 JPEG avatar
            var finalBitmap = Android.Graphics.Bitmap.CreateScaledBitmap(cropped, 512, 512, true)!;
            if (finalBitmap != cropped) cropped.Recycle();

            var id = Guid.CreateVersion7(time.GetUtcNow()).ToString("N");
            var outputPath = System.IO.Path.Combine(FileSystem.AppDataDirectory, $"avatar_{id}.jpg");
            using (var stream = System.IO.File.Create(outputPath))
            {
                finalBitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 90, stream);
            }
            finalBitmap.Recycle();
            return outputPath;
        });
    }
}
