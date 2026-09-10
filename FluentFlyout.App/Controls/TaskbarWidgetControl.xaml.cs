using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Numerics;
using Windows.Storage.Streams;
using FluentFlyout.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace FluentFlyout.App.Controls;

/// <summary>
/// Taskbar media widget with the WPF feature set: square album-disc background
/// rotating behind the widget viewport, baked blur (WinUI has no BlurEffect, so
/// the artwork is pre-blurred into a bitmap), true two-layer crossfade on song
/// change, album art, themed text and working playback controls.
/// Rotation and crossfade use the Composition APIs (WinUI has no WPF-style
/// BeginAnimation on framework properties).
/// </summary>
public sealed partial class TaskbarWidgetControl : UserControl
{
    private const int BakedTextureSize = 256;
    private const int BlurPasses = 6;
    private const double DiscMinSide = 480;
    private const double DiscOffsetFactor = 0.28;

    private bool rotationActive;
    private bool rotationPaused;
    private double pausedRotationAngle;

    // Spin bookkeeping (Composition-driven; we advance the angle ourselves).
    private readonly Stopwatch spinStopwatch = Stopwatch.StartNew();
    private TimeSpan spinStartUtc;
    private double spinStartAngle;
    private double spinDurationSeconds = 20;
    private bool spinUp;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? spinTimer;

    // Crossfade state.
    private int bgCrossfadeVersion;
    private BitmapSource? bgCrossfadeTarget;

    private MediaSnapshot? lastSnapshot;
    private bool isPaused;

    public TaskbarWidgetControl()
    {
        InitializeComponent();

        ApplyWindowsTheme();
        ApplySettings();
        ActualThemeChanged += (_, _) => ApplyWindowsTheme();
        Loaded += (_, _) => { UpdateClip(); UpdateRotationPauseState(); };
        MainBorder.SizeChanged += MainBorder_SizeChanged;
    }

    /// <summary>WinUI has no ClipToBounds: clip the viewport with a geometry.</summary>
    private void UpdateClip()
    {
        double w = MainBorder.ActualWidth, h = MainBorder.ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        ClipGrid.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, w, h),
        };
    }

    // ------------------------------------------------------------------
    // Public surface (used by TaskbarHostWindow)
    // ------------------------------------------------------------------

    public void ApplySettings()
    {
        var settings = App.Current.Runtime.Settings.Current;

        ControlsStackPanel.Visibility = settings.TaskbarWidgetControlsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        SongImageBorder.Visibility = settings.TaskbarWidgetShowAlbumArt
            ? Visibility.Visible : Visibility.Collapsed;

        SongTitle.FontSize = settings.TaskbarWidgetTitleFontSize;
        SongArtist.FontSize = settings.TaskbarWidgetArtistFontSize;
        SongTitleContainer.Height = Math.Ceiling(settings.TaskbarWidgetTitleFontSize * 1.5);
        SongArtistContainer.Height = Math.Ceiling(settings.TaskbarWidgetArtistFontSize * 1.5);

        double intensity = Math.Clamp(settings.TaskbarWidgetBackgroundBlurIntensity, 0, 100) / 100.0;
        if (intensity <= 0.01)
            intensity = 0.65;
        BackgroundImage.Opacity = intensity;

        UpdateBackgroundMode();
    }

    public void UpdateMedia(MediaSnapshot snapshot)
    {
        lastSnapshot = snapshot;
        bool wasPaused = isPaused;
        isPaused = snapshot.PlaybackState != MediaPlaybackState.Playing;

        string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown" : snapshot.Title;
        string artist = string.IsNullOrWhiteSpace(snapshot.Artist) ? "No artist" : snapshot.Artist;

        SongTitle.Text = title;
        SongArtist.Text = artist;
        PlayPauseIcon.Glyph = snapshot.PlaybackState == MediaPlaybackState.Playing ? "\uE769" : "\uE768";

        if (snapshot.ArtworkBytes is { Length: > 0 })
        {
            _ = LoadArtworkAsync(snapshot.ArtworkBytes);
        }
        else
        {
            SongImageBorderInner.Visibility = Visibility.Collapsed;
            SongImagePlaceholder.Visibility = Visibility.Visible;
            SetBackgroundLayers(null);
        }

        if (wasPaused != isPaused)
            UpdateRotationPauseState();
    }

    // ------------------------------------------------------------------
    // Artwork + baked blur
    // ------------------------------------------------------------------

    private async Task LoadArtworkAsync(byte[] artworkBytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(artworkBytes.AsBuffer());
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            SongImage.Source = bitmap;
            SongImageBorderInner.Visibility = Visibility.Visible;
            SongImagePlaceholder.Visibility = Visibility.Collapsed;

            // Pre-blur the artwork into a square texture: WinUI has no BlurEffect,
            // so the WPF live blur becomes a downscale + box-blur bake.
            BitmapSource? baked = await BakeBlurredTextureAsync(artworkBytes);
            if (baked is not null)
                SetBackgroundLayers(baked);
        }
        catch
        {
            SongImageBorderInner.Visibility = Visibility.Collapsed;
            SongImagePlaceholder.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Decodes the artwork, scales it to a square 256px texture and runs a small
    /// separable box blur repeatedly — enough passes that the result reads like the
    /// WPF BlurEffect at the configured radius.
    /// </summary>
    private static async Task<BitmapSource?> BakeBlurredTextureAsync(byte[] artworkBytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(artworkBytes.AsBuffer());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform
            {
                ScaledWidth = BakedTextureSize,
                ScaledHeight = BakedTextureSize,
                InterpolationMode = BitmapInterpolationMode.Linear,
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            byte[] buffer = pixels.DetachPixelData();
            BoxBlur(buffer, BakedTextureSize, BakedTextureSize, BlurPasses);

            var bitmap = new WriteableBitmap(BakedTextureSize, BakedTextureSize);
            using var writer = bitmap.PixelBuffer.AsStream();
            await writer.WriteAsync(buffer);
            bitmap.Invalidate();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static void BoxBlur(byte[] buffer, int width, int height, int passes)
    {
        byte[] temp = new byte[buffer.Length];
        for (int p = 0; p < passes; p++)
        {
            BoxBlurPass(buffer, temp, width, height, horizontal: true);
            BoxBlurPass(temp, buffer, width, height, horizontal: false);
        }
    }

    private static void BoxBlurPass(byte[] src, byte[] dst, int width, int height, bool horizontal)
    {
        const int radius = 12; // matches the WPF blur radius feel at 256px bake size
        int outer = horizontal ? height : width;
        int inner = horizontal ? width : height;

        for (int o = 0; o < outer; o++)
        {
            for (int i = 0; i < inner; i++)
            {
                int sr = 0, sg = 0, sb = 0, count = 0;
                for (int d = -radius; d <= radius; d++)
                {
                    int j = i + d;
                    if (j < 0 || j >= inner) continue;
                    int index = horizontal
                        ? (o * width + j) * 4
                        : (j * width + o) * 4;
                    sr += src[index];
                    sg += src[index + 1];
                    sb += src[index + 2];
                    count++;
                }

                int outIndex = horizontal
                    ? (o * width + i) * 4
                    : (i * width + o) * 4;
                dst[outIndex] = (byte)(sr / count);
                dst[outIndex + 1] = (byte)(sg / count);
                dst[outIndex + 2] = (byte)(sb / count);
                dst[outIndex + 3] = src[outIndex + 3];
            }
        }
    }

    // ------------------------------------------------------------------
    // Background rotation (spinning disc) — Composition driven
    // ------------------------------------------------------------------

    private void MainBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateClip();
        if (rotationActive)
            ApplyBackgroundRotation(currentAngleDeg);
        else
            LayoutBackgroundToFillWidget();
    }

    public void UpdateBackgroundMode()
    {
        var settings = App.Current.Runtime.Settings.Current;
        bool shouldRotate = settings.TaskbarWidgetBackgroundRotate
                            && settings.TaskbarWidgetBackgroundBlur;

        if (shouldRotate)
            ApplyBackgroundRotation(rotationPaused ? pausedRotationAngle : 0);
        else
        {
            StopSpin();
            rotationActive = false;
            LayoutBackgroundToFillWidget();
        }

        UpdateRotationPauseState();
    }

    private double currentAngleDeg
    {
        get
        {
            if (!rotationActive || rotationPaused)
                return pausedRotationAngle;
            double elapsed = (spinStopwatch.Elapsed - spinStartUtc).TotalSeconds;
            double direction = spinUp ? -1 : 1;
            double angle = spinStartAngle + direction * (elapsed / Math.Max(spinDurationSeconds, 0.1)) * 360.0;
            return angle % 360.0;
        }
    }

    private void ApplyBackgroundRotation(double startAngle)
    {
        double width = MainBorder.ActualWidth > 0 ? MainBorder.ActualWidth : 240;
        double height = MainBorder.ActualHeight > 0 ? MainBorder.ActualHeight : 40;

        rotationActive = true;

        var settings = App.Current.Runtime.Settings.Current;
        double sizeMultiplier = Math.Max(settings.TaskbarWidgetBackgroundRotateSize, 100) / 100.0;
        double discSide = Math.Max(Math.Max(width * sizeMultiplier, height * sizeMultiplier), DiscMinSide);
        double offsetX = discSide * DiscOffsetFactor;

        bool showLeftSide = settings.TaskbarWidgetBackgroundRotateSide == 0;
        LayoutDiscLayer(BackgroundImage, width, height, discSide, offsetX, showLeftSide);
        LayoutDiscLayer(BackgroundImageNext, width, height, discSide, offsetX, showLeftSide);

        // Rotate at composition level: pivot at the disc centre.
        var visual = ElementCompositionPreview.GetElementVisual(BackgroundImage);
        var visualNext = ElementCompositionPreview.GetElementVisual(BackgroundImageNext);
        visual.CenterPoint = new Vector3((float)discSide / 2f, (float)discSide / 2f, 0f);
        visualNext.CenterPoint = visual.CenterPoint;

        spinDurationSeconds = Math.Max(settings.TaskbarWidgetBackgroundRotateDuration, 1);
        spinUp = settings.TaskbarWidgetBackgroundRotateDirection == 1;
        spinStartAngle = startAngle;
        spinStartUtc = spinStopwatch.Elapsed;

        StartSpin();
    }

    private void StartSpin()
    {
        if (spinTimer is not null)
            return; // already spinning

        spinTimer = DispatcherQueue.CreateTimer();
        spinTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 FPS, matches the WPF cap
        spinTimer.Tick += (_, _) => TickSpin();
        spinTimer.Start();
    }

    private void TickSpin()
    {
        double angle = currentAngleDeg;
        float radians = (float)(angle * Math.PI / 180.0);
        ElementCompositionPreview.GetElementVisual(BackgroundImage).RotationAngle = radians;
        ElementCompositionPreview.GetElementVisual(BackgroundImageNext).RotationAngle = radians;
    }

    private void StopSpin()
    {
        spinTimer?.Stop();
        spinTimer = null;
    }

    private void PauseBackgroundRotation()
    {
        if (rotationPaused || !rotationActive)
            return;

        pausedRotationAngle = currentAngleDeg;
        StopSpin();
        rotationPaused = true;
    }

    private void ResumeBackgroundRotation()
    {
        if (!rotationPaused)
            return;

        rotationPaused = false;
        ApplyBackgroundRotation(pausedRotationAngle);
    }

    private void UpdateRotationPauseState()
    {
        var settings = App.Current.Runtime.Settings.Current;
        if (!settings.TaskbarWidgetBackgroundRotate || !settings.TaskbarWidgetBackgroundBlur)
            return;

        if (isPaused || !IsLoaded)
            PauseBackgroundRotation();
        else
            ResumeBackgroundRotation();
    }

    private static void LayoutDiscLayer(Image layer, double width, double height, double discSide, double offsetX, bool showLeftSide)
    {
        layer.Width = discSide;
        layer.Height = discSide;
        layer.Stretch = Stretch.Fill;
        Canvas.SetLeft(layer, (width - discSide) / 2 + (showLeftSide ? offsetX : -offsetX));
        Canvas.SetTop(layer, (height - discSide) / 2);
    }

    // ------------------------------------------------------------------
    // Two-layer crossfade — Composition opacity animations
    // ------------------------------------------------------------------

    /// <summary>
    /// True crossfade between background discs: the incoming disc fades 0 -> bound on
    /// the top layer while the old disc stays at bound underneath, then the old layer
    /// adopts the new image invisibly and the top layer parks. Versioned so rapid
    /// skips collapse onto the latest art with no stuck states.
    /// </summary>
    private void BeginBackgroundCrossfade(BitmapSource target)
    {
        int durationMs = 250;

        double bound = BackgroundImage.Opacity;

        if (BackgroundImage.Source is null)
        {
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
            return;
        }

        if (ReferenceEquals(BackgroundImage.Source, target) && bgCrossfadeTarget is null)
        {
            ParkBackgroundNextLayer();
            return;
        }

        if (ReferenceEquals(bgCrossfadeTarget, target))
            return;

        bgCrossfadeVersion++;
        int version = bgCrossfadeVersion;

        if (ReferenceEquals(BackgroundImage.Source, target))
        {
            // Fading back to what is already underneath (rapid A -> B -> A): park.
            bgCrossfadeTarget = target;
            ParkBackgroundNextLayer();
            return;
        }

        bgCrossfadeTarget = target;
        BackgroundImageNext.Source = target;
        BackgroundImageNext.Visibility = Visibility.Visible;

        var visual = ElementCompositionPreview.GetElementVisual(BackgroundImageNext);
        visual.StopAnimation("Opacity");
        visual.Opacity = 0f;

        var compositor = visual.Compositor;
        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, 0f);
        anim.InsertKeyFrame(1f, (float)bound, compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f)));
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);

        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        batch.Completed += (_, _) =>
        {
            if (version != bgCrossfadeVersion)
                return;
            // Fully covered by the top layer: adopt underneath (invisible change).
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
        };
        batch.End();

        visual.StartAnimation("Opacity", anim);
    }

    private void ParkBackgroundNextLayer()
    {
        bgCrossfadeTarget = null;
        var visual = ElementCompositionPreview.GetElementVisual(BackgroundImageNext);
        visual.StopAnimation("Opacity");
        visual.Opacity = 0f;
        BackgroundImageNext.Visibility = Visibility.Collapsed;
    }

    private void CancelBackgroundCrossfade()
    {
        bgCrossfadeVersion++;
        ParkBackgroundNextLayer();
    }

    private void SetBackgroundLayers(BitmapSource? target)
    {
        if (target is null)
        {
            BackgroundImage.Source = null;
            CancelBackgroundCrossfade();
            BackgroundImage.Visibility = Visibility.Collapsed;
            return;
        }

        BackgroundImage.Visibility = Visibility.Visible;
        BeginBackgroundCrossfade(target);
    }

    // ------------------------------------------------------------------
    // Static (non-rotating) fill layout
    // ------------------------------------------------------------------

    private void LayoutBackgroundToFillWidget()
    {
        double width = MainBorder.ActualWidth > 0 ? MainBorder.ActualWidth : 240;
        double height = MainBorder.ActualHeight > 0 ? MainBorder.ActualHeight : 40;
        double side = Math.Max(Math.Max(width, height), 1);

        LayoutFillLayer(BackgroundImage, width, height, side);
        LayoutFillLayer(BackgroundImageNext, width, height, side);

        // No rotation in this mode: zero the composition rotation.
        var visual = ElementCompositionPreview.GetElementVisual(BackgroundImage);
        visual.CenterPoint = new Vector3((float)side / 2f, (float)side / 2f, 0f);
        visual.RotationAngle = 0f;
        var visualNext = ElementCompositionPreview.GetElementVisual(BackgroundImageNext);
        visualNext.CenterPoint = visual.CenterPoint;
        visualNext.RotationAngle = 0f;
    }

    private static void LayoutFillLayer(Image layer, double width, double height, double side)
    {
        layer.Width = side;
        layer.Height = side;
        layer.Stretch = Stretch.Fill;
        Canvas.SetLeft(layer, (width - side) / 2);
        Canvas.SetTop(layer, (height - side) / 2);
    }

    // ------------------------------------------------------------------
    // Theme
    // ------------------------------------------------------------------

    public void ApplyWindowsTheme()
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        Color foreground = isDark
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE4, 0x1C, 0x1C, 0x1C);

        var brush = new SolidColorBrush(foreground);
        SongTitle.Foreground = brush;
        SongArtist.Foreground = brush;
        SongImagePlaceholder.Foreground = brush;
        PreviousButton.Foreground = brush;
        PlayPauseButton.Foreground = brush;
        NextButton.Foreground = brush;
    }

    // ------------------------------------------------------------------
    // Interactions
    // ------------------------------------------------------------------

    private async Task PlayPauseAsync()
    {
        await App.Current.Runtime.Media.PlayPauseAsync();
        if (lastSnapshot is not null)
        {
            var next = lastSnapshot with
            {
                PlaybackState = lastSnapshot.PlaybackState == MediaPlaybackState.Playing
                    ? MediaPlaybackState.Paused
                    : MediaPlaybackState.Playing,
            };
            UpdateMedia(next);
        }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await App.Current.Runtime.Media.PreviousAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await App.Current.Runtime.Media.NextAsync();
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await PlayPauseAsync();

    private void SongImage_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        if (settings.TaskbarWidgetClickOpensFlyout)
            App.Current.WindowManager.ShowMediaFlyout();
    }
}
