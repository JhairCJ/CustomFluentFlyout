// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using Wpf.Ui.Controls;

namespace FluentFlyout.Controls;

/// <summary>
/// Interaction logic for TaskbarWidgetControl.xaml
/// </summary>
public partial class TaskbarWidgetControl : UserControl
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly double _scale = 0.9;
    private readonly int _nativeWidgetsPadding = 216;

    private readonly int _coverImageMargin = 55;

    // Physical left margins that stay on screen even when the album art is hidden
    // (MainStackPanel 4px + SongInfoStackPanel 8px). These must still be reserved in the
    // width calculation, otherwise the media buttons overflow to the right.
    private readonly int _noCoverReservedMargin = 12;

    // Cached width calculations
    private string _cachedTitleText = string.Empty;
    private string _cachedArtistText = string.Empty;
    private double _cachedTitleWidth = 0;
    private double _cachedArtistWidth = 0;
    private double _cachedTitleContainerWidth = -1;
    private double _cachedArtistContainerWidth = -1;
    private readonly int _extraMarginForText = 6; // additional margin to avoid text clipping

    private double _cachedTitleOpacityMaskWidth = -1;
    private double _cachedArtistOpacityMaskWidth = -1;
    private LinearGradientBrush? _cachedTitleOpacityMask;
    private LinearGradientBrush? _cachedArtistOpacityMask;

    private string _actualTitle = string.Empty;
    private string _actualArtist = string.Empty;

    // Last artwork instance handed to UpdateUi. Media sessions deliver a song change
    // in two phases (title first, cover art in a later event), so the art must be
    // tracked separately: its arrival deserves its own entrance transition.
    // Instances come from the thumbnail cache, so reference comparison is exact.
    private BitmapImage? _lastIcon;

    // reference to main window for flyout functions
    private MainWindow? _mainWindow;
    private bool _isPaused;

    // rotating background (baked blur)
    private double _appliedRotationDurationSeconds;
    private int? _appliedDesiredFrameRate;
    private BitmapImage? _currentIcon;
    private BitmapImage? _bakedIcon;
    private BitmapSource? _bakedBackground;
    private double _bakedSideDip;
    private RotateTransform? _backgroundRotateTransform;
    private bool _backgroundRotationActive;
    private bool _backgroundRotationAnimationRunning;
    private bool _backgroundRotationWasUp;
    private bool _backgroundRotationPaused;
    private double _pausedRotationAngle;

    // Debounce before collapsing to the "no media" placeholder so the widget does not
    // blink during the transient gap while switching tracks.
    private const int NoMediaDebounceMs = 700;
    private DispatcherTimer? _noMediaDebounceTimer;

    // A new song's cover art routinely arrives in a later event than its title.
    // While the title is fresh, a null cover means "not here yet", not "has none":
    // keep the old cover + background instead of flashing placeholder-on-black.
    private const int NoArtDebounceMs = 600;
    private DispatcherTimer? _noArtDebounceTimer;
    private DateTime _lastInfoChangeUtc = DateTime.MinValue;

    // True while the widget is fading out; used to cancel the hide if media resumes
    // before the fade completes.
    private bool _isFadingOut;

    // True while the mouse is over the album art; used to reveal the switch-session chevron.
    private bool _albumArtHovering;

    // Play/pause glyphs shared across updates: allocating a new SymbolIcon per
    // metadata event is pure GC pressure for two constant visuals.
    private static readonly SymbolIcon _playIcon = new(SymbolRegular.Play24, filled: true);
    private static readonly SymbolIcon _pauseIcon = new(SymbolRegular.Pause24, filled: true);

    // True while a cover thumbnail is currently displayed; the switch-session chevron and the
    // pause overlay are only drawn over real cover art, not over the music-note placeholder.
    private bool _hasAlbumCover;

    // Slide song-change animation state (style 1): while active, the outgoing ghost texts
    // slide out to the left and the new texts slide in from the right. Marquee updates are
    // suspended meanwhile so the scrolling clocks never steal the slide transforms.
    private bool _songChangeSlideActive;
    private int _songChangeSlideVersion;
    private readonly List<System.Windows.Controls.TextBlock> _songChangeSlideGhosts = new();

    // Pending slide direction for the next song change: set when the user explicitly
    // navigates (previous/next buttons) so a backward step animates mirrored (old text
    // exits right, new text enters from the left). Expires quickly so a stale note never
    // leaks into an unrelated later change (e.g. auto-advance at the end of a song).
    private bool _slideBackwardsPending;
    private DateTime _slideDirectionNotedUtc = DateTime.MinValue;
    private static readonly TimeSpan SlideDirectionLifetime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Notes an explicit track navigation so the next song-change slide animates in the
    /// matching direction (forward: exit left / enter from right; backward: mirrored).
    /// </summary>
    /// <param name="forward">True for next-track, false for previous-track.</param>
    public void NoteTrackNavigation(bool forward)
    {
        _slideBackwardsPending = !forward;
        _slideDirectionNotedUtc = DateTime.UtcNow;
    }

    public TaskbarWidgetControl()
    {
        InitializeComponent();

        // Apply Windows theme colors (independent of the app theme setting)
        ApplyWindowsTheme();

        // Set DataContext for bindings
        DataContext = SettingsManager.Current;

        MainBorder.SizeChanged += (s, e) =>
        {
            ApplyCornerRadius();

            if (_backgroundRotationActive)
                ApplyBackgroundRotation();
            else
                LayoutBackgroundToFillWidget();
        };
        ApplyCornerRadius();
        ApplyButtonHoverRadius();

        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        // Initialize control order
        ReorderControls();

        // Apply the background mode (normal or animated rotation)
        UpdateBackgroundMode();

        // A forever rotation clock burns CPU/GPU even when nobody can see it
        // (autohide, widget collapsed, no media). Freeze it while hidden.
        IsVisibleChanged += (s, e) => UpdateRotationPauseState();
    }

    public void ApplyCornerRadius()
    {
        double radius = SettingsManager.Current.TaskbarWidgetBorderRadius;
        MainBorder.CornerRadius = new CornerRadius(radius);
        TopBorder.CornerRadius = new CornerRadius(Math.Max(0, radius - 1));
        SongImageBorder.CornerRadius = new CornerRadius(SettingsManager.Current.TaskbarWidgetAlbumArtRadius);
        CrossfadeOverlay.CornerRadius = new CornerRadius(radius);
        MainBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, MainBorder.ActualWidth, MainBorder.ActualHeight), radius, radius);
    }

    public void ApplyButtonHoverRadius()
    {
        var radius = new CornerRadius(SettingsManager.Current.TaskbarWidgetButtonHoverRadius);
        PreviousButton.CornerRadius = radius;
        PlayPauseButton.CornerRadius = radius;
        NextButton.CornerRadius = radius;
    }

    public void ReorderControls()
    {
        // Remove ControlsStackPanel from MainStackPanel
        MainStackPanel.Children.Remove(ControlsStackPanel);

        // Reorder based on position setting
        if (SettingsManager.Current.TaskbarWidgetControlsPosition == 0)
        {
            // Left: Controls, Image, Info
            MainStackPanel.Children.Insert(0, ControlsStackPanel);
            ControlsStackPanel.Margin = new Thickness(2, 0, 6, 0); // for some reason margins are weird on left side
        }
        else
        {
            // Right: Image, Info, Controls
            MainStackPanel.Children.Add(ControlsStackPanel);
            ControlsStackPanel.Margin = new Thickness(8, 0, 0, 0);
        }
    }

    public void SetVerticalMode(bool isVertical)
    {
        var counterRotate = isVertical ? new RotateTransform(-90) : null;

        SongImageBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        SongImageBorder.RenderTransform = (Transform?)counterRotate ?? Transform.Identity;

        foreach (var button in new Wpf.Ui.Controls.Button[] { PreviousButton, PlayPauseButton, NextButton })
        {
            button.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            button.RenderTransform = (Transform?)counterRotate ?? Transform.Identity;
        }
    }

    public void SetMainWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
    }

    public void ApplyWindowsTheme()
    {
        WindowsThemeDetector.GetWindowsTheme(out _, out var systemTheme);
        bool isDark = systemTheme == WindowsThemeDetector.ThemeMode.Dark;

        var foreground = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE4, 0x1C, 0x1C, 0x1C));

        SongTitle.Foreground = foreground;
        SongArtist.Foreground = foreground;
        PreviousButton.Foreground = foreground;
        PlayPauseButton.Foreground = foreground;
        NextButton.Foreground = foreground;
    }

    /// <summary>
    /// Applies the background mode based on settings: either the normal live-blurred image
    /// or the complete square album disc rotating behind the widget (the widget only acts
    /// as the viewport that reveals a band of the rotating square).
    /// </summary>
    public void UpdateBackgroundMode()
    {
        bool shouldRotate = SettingsManager.Current.TaskbarWidgetBackgroundRotate &&
                            SettingsManager.Current.TaskbarWidgetBackgroundBlur;

        if (shouldRotate)
        {
            ApplyBackgroundRotation();
        }
        else
        {
            StopBackgroundRotation();

            LayoutBackgroundToFillWidget();

            // Static image needs no per-frame cache; drop it to save memory.
            BackgroundImage.CacheMode = null;

            if (_currentIcon != null)
                BackgroundImage.Source = _currentIcon;
        }

        CancelBackgroundDip();

        UpdateRotationPauseState();
    }

    private void ApplyBackgroundRotation(double startAngle = 0)
    {
        double width = MainBorder.ActualWidth > 0 ? MainBorder.ActualWidth : 240;
        double height = MainBorder.ActualHeight > 0 ? MainBorder.ActualHeight : 40;

        _backgroundRotationActive = true;

        if (_backgroundRotateTransform == null)
        {
            _backgroundRotateTransform = new RotateTransform(0);
            BackgroundImage.RenderTransform = _backgroundRotateTransform;
        }

        // blur is baked into the bitmap, so disable the live effect while rotating
        BackgroundImage.Effect = null;

        // Cache the rotating subtree as a bitmap: each frame becomes a cheap texture
        // transform instead of a full resample + clip of the large disc. RenderAtScale
        // 0.5 matches the 256px baked texture, so nothing visible is lost (it's all blur).
        if (BackgroundImage.CacheMode is not BitmapCache)
            BackgroundImage.CacheMode = new BitmapCache(0.5);

        // The rotating square is about three times the widget's width - large enough that a
        // full viewport-width band always stays inside the rotating disc at every angle,
        // while still keeping the artwork recognisable. It is sized in DIPs from the
        // widget itself, so it scales with any screen/DPI (1080p, 4K, ...).
        // The floor is kept small on purpose: the baked texture is only 256px and heavily
        // blurred, so a larger disc just burns fill-rate resampling it every frame.
        double sizeMultiplier = Math.Max(SettingsManager.Current.TaskbarWidgetBackgroundRotateSize, 100) / 100.0;
        double discSide = Math.Max(Math.Max(width * sizeMultiplier, height * sizeMultiplier), 480);
        double offsetX = discSide * 0.28; // distance of the viewport band from the disc centre

        // The square is centred vertically, but shifted horizontally away from the disc
        // centre so the centre of the album is never visible through the viewport.
        bool showLeftSide = SettingsManager.Current.TaskbarWidgetBackgroundRotateSide == 0;
        LayoutDiscLayer(BackgroundImage, width, height, discSide, offsetX, showLeftSide);

        if (_currentIcon != null)
            UpdateBakedBackgroundAsync(_currentIcon, discSide);

        // 0 = spins down (clockwise), 1 = spins up (counter - clockwise)
        bool spinUp = SettingsManager.Current.TaskbarWidgetBackgroundRotateDirection == 1;

        // Restart the endless rotation when a different direction, duration or start angle
        // (e.g. resuming after a pause) is requested
        double durationSeconds = Math.Max(SettingsManager.Current.TaskbarWidgetBackgroundRotateDuration, 1);
        if (_backgroundRotationPaused)
        {
            // keep the disc frozen at the angle it was paused at, even if the layout
            // changed (e.g. widget resize or settings change) while media is paused
            _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            _backgroundRotateTransform.Angle = _pausedRotationAngle;
            return;
        }

        if (!_backgroundRotationAnimationRunning ||
            _backgroundRotationWasUp != spinUp ||
            Math.Abs(_appliedRotationDurationSeconds - durationSeconds) > 0.01 ||
            AppliedDesiredFrameRateChanged() ||
            Math.Abs(startAngle - 0) > 0.01)
        {
            _backgroundRotationWasUp = spinUp;
            _backgroundRotationAnimationRunning = true;
            _appliedRotationDurationSeconds = durationSeconds;
            var animation = new DoubleAnimation
            {
                From = startAngle,
                To = spinUp ? startAngle - 360 : startAngle + 360,
                Duration = TimeSpan.FromSeconds(durationSeconds),
                RepeatBehavior = RepeatBehavior.Forever
            };

            // Cap the animation clock at 30 FPS when high refresh rate mode is off to reduce
            // per-frame rasterization cost. When on, the animation runs at the monitor's
            // refresh rate (no cap).
            int? desiredFrameRate = SettingsManager.Current.TaskbarWidgetBackgroundRotateHighRefreshRate
                ? null
                : 30;
            Timeline.SetDesiredFrameRate(animation, desiredFrameRate);
            _appliedDesiredFrameRate = desiredFrameRate;

            _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
        }
    }

    /// <summary>
    /// Positions a rotating-disc background layer over the viewport.
    /// </summary>
    private static void LayoutDiscLayer(System.Windows.Controls.Image layer, double width, double height, double discSide, double offsetX, bool showLeftSide)
    {
        layer.Width = discSide;
        layer.Height = discSide;
        layer.Stretch = Stretch.Fill;
        layer.Margin = new Thickness(0);
        Canvas.SetLeft(layer, (width - discSide) / 2 + (showLeftSide ? offsetX : -offsetX));
        Canvas.SetTop(layer, (height - discSide) / 2);
    }

    /// <summary>
    /// Fades the single background layer out, swaps to <paramref name="target"/>, and
    /// fades back in. One layer, one clock: the rotation transform is never touched,
    /// so the disc cannot jump position mid-transition. Restarting toward a newer
    /// target kills the previous clock (removed clocks never complete), so rapid
    /// skips collapse onto the latest art with no stuck states.
    /// </summary>
    private void BeginBackgroundDip(BitmapSource target)
    {
        // Restart toward the newest target if one is already running.
        BackgroundImage.BeginAnimation(OpacityProperty, null);

        if (!_backgroundRotationActive || ReferenceEquals(BackgroundImage.Source, target))
            return;

        if (!AreAnimationsEnabled)
        {
            BackgroundImage.Source = target;
            return;
        }

        double bound = BackgroundImage.Opacity; // bound intensity value, restored afterwards

        var dipOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = GetEasing(false)
        };
        dipOut.Completed += (s, e) =>
        {
            BackgroundImage.Source = target;

            var dipIn = new DoubleAnimation
            {
                To = bound,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = GetEasing(true)
            };
            dipIn.Completed += (s2, e2) =>
            {
                BackgroundImage.BeginAnimation(OpacityProperty, null); // binding resumes, value already matches
            };
            BackgroundImage.BeginAnimation(OpacityProperty, dipIn);
        };
        BackgroundImage.BeginAnimation(OpacityProperty, dipOut);
    }

    /// <summary>
    /// Kills a running background dip, restoring the bound opacity.
    /// Used when rotation stops or the widget is torn down.
    /// </summary>
    private void CancelBackgroundDip()
    {
        BackgroundImage.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>
    /// True when the desired animation frame rate differs from the last applied one.
    /// </summary>
    private bool AppliedDesiredFrameRateChanged()
    {
        int? desiredFrameRate = SettingsManager.Current.TaskbarWidgetBackgroundRotateHighRefreshRate
            ? null
            : 30;
        return (_appliedDesiredFrameRate ?? 0) != (desiredFrameRate ?? 0);
    }

    /// <summary>
    /// Restarts the rotation with the current setting's frame rate, keeping the disc
    /// at the exact angle it had before the toggle, so there is no visual jump.
    /// </summary>
    public void RefreshBackgroundRotationFrameRate()
    {
        if (!SettingsManager.Current.TaskbarWidgetBackgroundRotate ||
            !SettingsManager.Current.TaskbarWidgetBackgroundBlur)
            return;

        if (!_backgroundRotationActive || _backgroundRotateTransform == null)
            return;

        double currentAngle = _backgroundRotateTransform.Angle;
        ApplyBackgroundRotation(currentAngle);
    }

    /// <summary>
    /// Freezes the rotating background at its current angle. The animation clock is
    /// stopped but the disc stays visible at the exact angle it had when paused.
    /// </summary>
    private void PauseBackgroundRotation()
    {
        if (!_backgroundRotationAnimationRunning || _backgroundRotationPaused || _backgroundRotateTransform == null)
            return;

        _pausedRotationAngle = _backgroundRotateTransform.Angle;
        _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        _backgroundRotateTransform.Angle = _pausedRotationAngle;
        _backgroundRotationAnimationRunning = false;
        _backgroundRotationPaused = true;
    }

    /// <summary>
    /// Resumes the rotating background animation from the angle it was paused at.
    /// </summary>
    private void ResumeBackgroundRotation()
    {
        if (!_backgroundRotationPaused)
            return;

        _backgroundRotationPaused = false;
        ApplyBackgroundRotation(_pausedRotationAngle);
    }

    /// <summary>
    /// Pauses or resumes the rotating background to match the current media playback
    /// state so the spinning disc freezes while media is paused. Also freezes while
    /// the widget itself is hidden, so the forever clock never rasterizes off-screen.
    /// </summary>
    private void UpdateRotationPauseState()
    {
        if (!SettingsManager.Current.TaskbarWidgetBackgroundRotate ||
            !SettingsManager.Current.TaskbarWidgetBackgroundBlur)
            return;

        if (_isPaused || !IsVisible)
            PauseBackgroundRotation();
        else
            ResumeBackgroundRotation();
    }

    private void StopBackgroundRotation()
    {
        _backgroundRotationActive = false;
        _backgroundRotationAnimationRunning = false;
        _backgroundRotationPaused = false;
        if (_backgroundRotateTransform != null)
        {
            _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            _backgroundRotateTransform.Angle = 0;
        }
        CancelBackgroundDip();
        BackgroundImage.CacheMode = null;
        BackgroundImage.Effect = BackgroundImageBlurEffect;
    }

    /// <summary>
    /// Lays out the background for the normal (non-rotating) blurred mode. A square
    /// element the size of the widget's larger dimension is centred over the viewport,
    /// so the visible band is cropped from the middle of the artwork, not its top.
    /// </summary>
    private void LayoutBackgroundToFillWidget()
    {
        double width = BackgroundCanvas.ActualWidth > 0 ? BackgroundCanvas.ActualWidth : (MainBorder.ActualWidth > 0 ? MainBorder.ActualWidth : 240);
        double height = BackgroundCanvas.ActualHeight > 0 ? BackgroundCanvas.ActualHeight : (MainBorder.ActualHeight > 0 ? MainBorder.ActualHeight : 40);

        double side = Math.Max(Math.Max(width, height), 1);

        Canvas.SetLeft(BackgroundImage, (width - side) / 2);
        Canvas.SetTop(BackgroundImage, (height - side) / 2);
        BackgroundImage.Width = side;
        BackgroundImage.Height = side;
        BackgroundImage.Margin = new Thickness(0);
        BackgroundImage.Stretch = Stretch.UniformToFill;
    }

    private void SetBackground(BitmapImage? icon)
    {
        _currentIcon = icon;

        if (icon == null)
        {
            StopBackgroundRotation();
            Canvas.SetLeft(BackgroundImage, 0);
            Canvas.SetTop(BackgroundImage, 0);
            BackgroundImage.Source = null;
            return;
        }

        if (SettingsManager.Current.TaskbarWidgetBackgroundRotate &&
            SettingsManager.Current.TaskbarWidgetBackgroundBlur)
        {
            ApplyBackgroundRotation();
        }
        else
        {
            LayoutBackgroundToFillWidget();
            BackgroundImage.Source = icon;
        }
    }

    /// <summary>
    /// Bakes the complete album cover once into a large blurred square that covers the
    /// widget while rotating, avoiding per-frame software re-rasterization of a live effect.
    /// Runs on a worker thread so it never blocks the UI thread during a song change.
    /// </summary>
    private static BitmapSource? BakeBlurredBackground(BitmapImage icon, double discSide, double dpi, double blurRadiusDips)
    {
        // Bake at a tiny fixed resolution regardless of disc size or monitor DPI. The blur
        // radius is scaled proportionally (blurRadiusDips * res / discSide), so the on-screen
        // look is identical at any resolution while the CPU rasterization drops to well under
        // a millisecond at 128px.
        int res = 256;
        int pixelSide = res;

        // scale the user's blur radius (DIPs on screen) into the baked texture space
        double blurRadius = blurRadiusDips * res / Math.Max(discSide, 1);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // the complete, unedited album cover, stretched to fill the square disc
            dc.DrawImage(icon, new Rect(0, 0, res, res));
        }

        visual.Effect = new BlurEffect
        {
            Radius = blurRadius,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };

        var rtb = new RenderTargetBitmap(pixelSide, pixelSide, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Asynchronously bakes the blurred rotating background for the current album. The
    /// previous baked disc keeps showing until the new one is ready (no raw-art flash).
    /// The raw artwork is only shown immediately on the very first paint, so the disc
    /// is never empty.
    /// </summary>
    private async void UpdateBakedBackgroundAsync(BitmapImage icon, double discSide)
    {
        if (_bakedBackground != null && ReferenceEquals(_bakedIcon, icon) && Math.Abs(_bakedSideDip - discSide) < 0.5)
        {
            CancelBackgroundDip();
            BackgroundImage.Source = _bakedBackground;
            return;
        }

        // First paint ever: show the raw artwork right away; otherwise keep the previous
        // baked background until the new one is ready.
        if (_bakedBackground == null && !ReferenceEquals(_bakedIcon, icon))
            BackgroundImage.Source = icon;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double blurRadiusDips = SettingsManager.Current.TaskbarWidgetBackgroundBlurRadius;

        BitmapSource? baked;
#if DEBUG
        var bakeStopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif
        try
        {
            baked = await Task.Run(() => BakeBlurredBackground(icon, discSide, dpi, blurRadiusDips));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to bake blurred taskbar widget background");
            return;
        }
#if DEBUG
        bakeStopwatch.Stop();
#endif

        // A newer song may have arrived while baking; discard the stale result.
        if (baked == null || !ReferenceEquals(_currentIcon, icon))
            return;

        _bakedIcon = icon;
        _bakedBackground = baked;
        _bakedSideDip = discSide;

        // Rotation may have been disabled while baking; the static path owns the layer then.
        if (!_backgroundRotationActive)
            return;

#if DEBUG
        Logger.Debug($"Widget background baked in {bakeStopwatch.Elapsed.TotalMilliseconds} ms, overlay visible: {CrossfadeOverlay.Visibility == Visibility.Visible}");
#endif

        if (CrossfadeOverlay.Visibility == Visibility.Visible)
        {
            // Covered by the song-change overlay: swap instantly underneath it, so the
            // overlay's single fade reveals everything new together (texts, art, disc).
            BackgroundImage.BeginAnimation(OpacityProperty, null);
            BackgroundImage.Source = baked;
            return;
        }

        // Overlay already lifted (slow bake): dip-fade instead of snapping.
        BeginBackgroundDip(baked);
    }

    /// <summary>
    /// Recomputes the cached text widths from <see cref="_actualTitle"/> /
    /// <see cref="_actualArtist"/> and returns the logical widget width, mirroring the
    /// sizing rules used by <see cref="CalculateSize"/>.
    /// </summary>
    /// <returns>
    /// The logical width and whether the underlying text changed since the last call.
    /// </returns>
    private (double logicalWidth, bool textChanged) ComputeTextLogicalWidth()
    {
        // calculate widget width - use cached values if text hasn't changed
        string currentTitle = _actualTitle;
        string currentArtist = _actualArtist;

        bool textChanged = false;

        if (!string.Equals(currentTitle, _cachedTitleText, StringComparison.Ordinal))
        {
            _cachedTitleWidth = Math.Round(StringWidth.GetStringWidth(currentTitle, 400), 2);
            _cachedTitleText = currentTitle;
            textChanged = true;
        }
        if (!string.Equals(currentArtist, _cachedArtistText, StringComparison.Ordinal))
        {
            _cachedArtistWidth = Math.Round(StringWidth.GetStringWidth(currentArtist, 400), 2);
            _cachedArtistText = currentArtist;
            textChanged = true;
        }

        // maximum width limit, same as Windows native widget
        double maxLogicalWidth = _nativeWidgetsPadding / _scale;
        double logicalWidth;

        double coverImageReserved = SettingsManager.Current.TaskbarWidgetShowAlbumArt ? _coverImageMargin : _noCoverReservedMargin;

        if (SettingsManager.Current.TaskbarWidgetFixedWidth)
        {
            // pin to the user-configured width so right-aligned controls don't shift between songs
            logicalWidth = SettingsManager.Current.TaskbarWidgetFixedWidthPx / _scale;
            logicalWidth = Math.Min(logicalWidth, maxLogicalWidth);
        }
        else
        {
            logicalWidth = Math.Max(_cachedTitleWidth, _cachedArtistWidth) + coverImageReserved + _extraMarginForText; // add margin for cover image
            logicalWidth = Math.Min(logicalWidth, maxLogicalWidth);
        }

        return (logicalWidth, textChanged);
    }

    /// <summary>
    /// Applies the text container widths derived from <paramref name="logicalWidth"/>.
    /// </summary>
    /// <returns>True when any container width changed.</returns>
    private bool SyncTextContainerWidths(double logicalWidth)
    {
        double coverImageReserved = SettingsManager.Current.TaskbarWidgetShowAlbumArt ? _coverImageMargin : _noCoverReservedMargin;

        double newTitleContainerWidth = Math.Max(logicalWidth - coverImageReserved, 0);
        double newArtistContainerWidth = Math.Max(logicalWidth - coverImageReserved, 0);
        bool widthChanged = false;

        if (_cachedTitleContainerWidth != newTitleContainerWidth)
        {
            SongTitleContainer.Width = newTitleContainerWidth;
            _cachedTitleContainerWidth = newTitleContainerWidth;
            widthChanged = true;
        }

        if (_cachedArtistContainerWidth != newArtistContainerWidth)
        {
            SongArtistContainer.Width = newArtistContainerWidth;
            _cachedArtistContainerWidth = newArtistContainerWidth;
            widthChanged = true;
        }

        return widthChanged;
    }

    public (double logicalWidth, double logicalHeight) CalculateSize(double dpiScale)
    {
        var (logicalWidth, textChanged) = ComputeTextLogicalWidth();
        bool widthChanged = SyncTextContainerWidths(logicalWidth);

        // Refresh animations if layout bounds or text contents change
        if (textChanged || widthChanged)
        {
            UpdateMarquees();
        }

        // add space for playback controls if enabled and visible
        if (SettingsManager.Current.TaskbarWidgetControlsEnabled && ControlsStackPanel.Visibility == Visibility.Visible)
        {
            logicalWidth += 104;
        }

        double logicalHeight = 40; // default height

        return (logicalWidth, logicalHeight);
    }

    public void UpdateMarquees(bool updateTitle = true, bool updateArtist = true)
    {
        // A slide transition owns the text transforms while it runs; restarting the
        // marquees here would snap the incoming text and strand the outgoing ghosts.
        if (_songChangeSlideActive)
            return;

        double titleAvailableWidth = double.IsNaN(SongTitleContainer.Width) ? 0 : SongTitleContainer.Width;
        double artistAvailableWidth = double.IsNaN(SongArtistContainer.Width) ? 0 : SongArtistContainer.Width;

        bool isScrollingEnabled = SettingsManager.Current.TaskbarWidgetScrollingEnabled;

        if (updateTitle)
            UpdateMarquee(SongTitle, SongTitleContainer, _cachedTitleWidth, titleAvailableWidth, isScrollingEnabled);
        if (updateArtist)
            UpdateMarquee(SongArtist, SongArtistContainer, _cachedArtistWidth, artistAvailableWidth, isScrollingEnabled);
    }

    private void UpdateMarquee(System.Windows.Controls.TextBlock textBlock, Canvas container, double textWidth, double availableWidth, bool isEnabled)
    {
        if (textBlock.RenderTransform as TranslateTransform is not { } transform) return;

        int speed = SettingsManager.Current.TaskbarWidgetScrollingTextSpeed;
        bool loopForever = SettingsManager.Current.TaskbarWidgetScrollingTextLoopForever;
        bool isTitle = textBlock == SongTitle;
        double containerWidth = container.Width;

        // references moved outside so they may be called in the else block later
        ref double cachedMaskWidth = ref (isTitle ? ref _cachedTitleOpacityMaskWidth : ref _cachedArtistOpacityMaskWidth);
        ref LinearGradientBrush? cachedMask = ref (isTitle ? ref _cachedTitleOpacityMask : ref _cachedArtistOpacityMask);

        if (isEnabled && textWidth > availableWidth && containerWidth > 0 && !double.IsNaN(containerWidth))
        {
            textBlock.Width = double.NaN;
            textBlock.TextTrimming = TextTrimming.None;

            string origText = isTitle ? _actualTitle : _actualArtist;

            if (cachedMask == null || Math.Abs(containerWidth - cachedMaskWidth) > 0.5)
            {
                // 12.0 is the width in pixels of the gradient fade on the left and right hand edges of the 
                // text container.
                double fadeFraction = 12.0 / containerWidth;
                if (fadeFraction > 0.5) fadeFraction = 0.5;

                cachedMask = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(containerWidth, 0),
                    MappingMode = BrushMappingMode.Absolute
                };

                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0));
                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), fadeFraction));
                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 1.0 - fadeFraction));
                cachedMask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
                cachedMaskWidth = containerWidth;
            }

            container.OpacityMask = cachedMask;

            if (loopForever)
            {
                // continuous looping should have the fades constantly active (as its infinite)
                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[0].Color = Color.FromArgb(0, 255, 255, 255);
                cachedMask.GradientStops[3].Color = Color.FromArgb(0, 255, 255, 255);

                // \u00A0 are non-breaking spaces, which prevents WPF from collapsing and/or trimming
                // them
                string spacer = "\u00A0\u00A0\u00A0\u00A0\u00A0";
                textBlock.Text = origText + spacer + origText;

                double spacerWidth = StringWidth.GetStringWidth(spacer, 400);
                double scrollDistance = textWidth + spacerWidth;

                double durationToScroll = scrollDistance / speed;
                var animation = new DoubleAnimation
                {
                    From = 0,
                    To = -scrollDistance,
                    Duration = TimeSpan.FromSeconds(durationToScroll),
                    RepeatBehavior = RepeatBehavior.Forever
                };

                transform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
            else
            {
                // Adding 10 pixels gives extra padding so the text scrolls past the container's edge before
                // resetting or reversing; this prevents abrupt cutoffs
                double scrollDistance = textWidth - containerWidth + 10;
                textBlock.Text = origText;

                double durationSeconds = scrollDistance / speed;
                double pauseDuration = 2.0; // wait 2 seconds at the start and end of the scroll
                double tWaitStart = pauseDuration;
                double tScrollEnd = tWaitStart + durationSeconds;
                double tWaitEnd = tScrollEnd + pauseDuration;
                double tScrollBackEnd = tWaitEnd + durationSeconds;
                double tTotalCycle = tScrollBackEnd + pauseDuration;

                var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitStart))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollBackEnd))));
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotalCycle))));

                // sync fades with the "ping pong" movement
                Color transparentWhite = Color.FromArgb(0, 255, 255, 255);
                Color solidWhite = Color.FromArgb(255, 255, 255, 255);

                // 300 ms is the capped duration for the fade transition; we clamp it so that the fade animation
                // doesn't overlap with the scroll animation on certain shorter texts
                TimeSpan fadeTime = TimeSpan.FromMilliseconds(Math.Min(300, durationSeconds * 1000 / 2.0));

                var leftColorAnim = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                leftColorAnim.KeyFrames.Add(new DiscreteColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitStart))));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitStart) + fadeTime)));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd) - fadeTime)));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
                leftColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotalCycle))));

                var rightColorAnim = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
                rightColorAnim.KeyFrames.Add(new DiscreteColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd) - fadeTime)));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tScrollEnd))));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(solidWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd))));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tWaitEnd) + fadeTime)));
                rightColorAnim.KeyFrames.Add(new LinearColorKeyFrame(transparentWhite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tTotalCycle))));

                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, leftColorAnim);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, rightColorAnim);

                transform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
        }
        else
        {
            if (cachedMask != null)
            {
                // Prevent memory leaks and/or unwanted behavior by clearing the color animations when the mask is hidden
                cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null);
                cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, null);
            }

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            textBlock.Text = isTitle ? _actualTitle : _actualArtist;
            textBlock.Width = containerWidth;
            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            container.OpacityMask = null;
        }
    }

    public void UpdateUi(string title, string artist, BitmapImage? icon, GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus, GlobalSystemMediaTransportControlsSessionPlaybackControls? playbackControls = null)
    {
        if (title == "-" && artist == "-")
        {
            // No media playing right now. This is often a transient gap while switching
            // tracks, so keep the last song visible instead of blinking to the music-note
            // placeholder; only collapse after a short debounce if media truly stopped.
            Dispatcher.Invoke(() =>
            {
                _isPaused = true;
                _lastIcon = null;
                _noArtDebounceTimer?.Stop();
                _noArtDebounceTimer = null;
                UpdateRotationPauseState();

                if (_noMediaDebounceTimer == null)
                {
                    _noMediaDebounceTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(NoMediaDebounceMs)
                    };
                    _noMediaDebounceTimer.Tick += (s, e) =>
                    {
                        _noMediaDebounceTimer.Stop();
                        ShowNoMediaPlaceholder();
                    };
                }

                _noMediaDebounceTimer.Stop();
                _noMediaDebounceTimer.Start();
            });
            return;
        }

        _isPaused = false;
        if (playbackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            _isPaused = true;
        }

        // Single UI-thread hop with every change batched: each Dispatcher.Invoke is
        // a queue round-trip plus its own layout pass, so three of them per metadata
        // event is pure overhead.
        Dispatcher.Invoke(() =>
        {
            // adjust UI based on available controls
            if (SettingsManager.Current.TaskbarWidgetControlsEnabled && playbackControls != null)
            {
                PreviousButton.IsHitTestVisible = playbackControls.IsPreviousEnabled;
                PlayPauseButton.IsHitTestVisible = playbackControls.IsPauseEnabled || playbackControls.IsPlayEnabled;
                NextButton.IsHitTestVisible = playbackControls.IsNextEnabled;

                PreviousButton.Opacity = playbackControls.IsPreviousEnabled ? 1 : 0.5;
                PlayPauseButton.Opacity = (playbackControls.IsPauseEnabled || playbackControls.IsPlayEnabled) ? 1 : 0.5;
                NextButton.Opacity = playbackControls.IsNextEnabled ? 1 : 0.5;
            }
            else
            {
                PreviousButton.IsHitTestVisible = false;
                PlayPauseButton.IsHitTestVisible = false;
                NextButton.IsHitTestVisible = false;

                PreviousButton.Opacity = 0.5;
                NextButton.Opacity = 0.5;
                PlayPauseButton.Opacity = 0.5;
            }

            _noMediaDebounceTimer?.Stop();
            _noMediaDebounceTimer = null;

            string newTitle = !string.IsNullOrEmpty(title) ? title : "-";
            string newArtist = !string.IsNullOrEmpty(artist) ? artist : "-";

            // Title/artist and cover art arrive in separate events on song change:
            // each half gets its own entrance so nothing pops in without a fade.
            bool infoChanged = _actualTitle != newTitle || _actualArtist != newArtist;
            if (infoChanged)
                _lastInfoChangeUtc = DateTime.UtcNow;
            bool artChanged = !ReferenceEquals(icon, _lastIcon);

            if (infoChanged || artChanged)
            {
                // Ghost texts must use the clean backing strings: the live TextBlocks may
                // hold marquee-duplicated text (title + spacer + title) while looping.
                string oldTitle = _actualTitle;
                string oldArtist = _actualArtist;

                _actualTitle = newTitle;
                _actualArtist = newArtist;

                // Slide and crossfade are mutually exclusive entrance styles: the slide owns
                // the text swap (and the marquee restart), so the snapshot crossfade is skipped.
                // Rows whose text did not change stay put (typically the artist when only
                // the title changes between songs).
                bool titleChanged = !string.Equals(oldTitle, newTitle, StringComparison.Ordinal);
                bool artistChanged = !string.Equals(oldArtist, newArtist, StringComparison.Ordinal);

                // Consume the pending navigation direction (if fresh) so it applies to this
                // change only; anything later (e.g. auto-advance) defaults to forward.
                bool slideBackwards = _slideBackwardsPending
                    && (DateTime.UtcNow - _slideDirectionNotedUtc) <= SlideDirectionLifetime;
                _slideBackwardsPending = false;

                bool slid = infoChanged
                    && SettingsManager.Current.TaskbarWidgetSongChangeAnimation == 1
                    && TryAnimateSongChangeSlide(oldTitle, oldArtist, newTitle, newArtist, titleChanged, artistChanged, slideBackwards);

                if (!slid)
                {
                    // changed info
                    if (SettingsManager.Current.TaskbarWidgetAnimated)
                    {
                        AnimateEntrance();
                    }

                    SongTitle.Text = _actualTitle;
                    SongArtist.Text = _actualArtist;
                }
            }

            // Update tooltip with song info (single allocation, no += chain)
            SongInfoStackPanel.ToolTip = string.IsNullOrEmpty(artist) ? title : title + "\n\n" + artist;

            if (SettingsManager.Current.TaskbarWidgetControlsEnabled)
            {
                PlayPauseButton.Icon = _isPaused ? _playIcon : _pauseIcon;
            }

            // change color of icon
            SolidColorBrush brush = AlbumAccent.Brush;
            SongImagePlaceholder.Foreground = brush;

            bool freshTitle = (DateTime.UtcNow - _lastInfoChangeUtc).TotalMilliseconds < NoArtDebounceMs;
            if (icon != null)
            {
                _noArtDebounceTimer?.Stop();
                _noArtDebounceTimer = null;
                _lastIcon = icon;
                _hasAlbumCover = true;
                SongImage.ImageSource = icon;
                SetBackground(icon);
                SongImageBorder.Margin = new Thickness(0, 0, 0, -2); // align image better when cover is present
            }
            else if (_hasAlbumCover && freshTitle)
            {
                // Cover not here yet for this new song: keep displaying the old one.
                // _lastIcon intentionally stays at the old art so the arrival diffs
                // and gets its own entrance; the timer falls back to the placeholder
                // if no art ever comes.
                if (_noArtDebounceTimer == null)
                {
                    _noArtDebounceTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(NoArtDebounceMs)
                    };
                    _noArtDebounceTimer.Tick += (s, e) => ShowArtPlaceholder();
                }

                _noArtDebounceTimer.Stop();
                _noArtDebounceTimer.Start();
            }
            else
            {
                _noArtDebounceTimer?.Stop();
                _noArtDebounceTimer = null;
                _lastIcon = null;
                _hasAlbumCover = false;
                SongImage.ImageSource = null;
                SetBackground(null);
            }

            UpdateAlbumArtOverlay();

            SongTitle.Visibility = Visibility.Visible;
            // While a slide is in flight the transition owns the artist row visibility
            // (kept visible for the outgoing ghost, collapsed on completion if empty).
            if (!_songChangeSlideActive)
                SongArtist.Visibility = !string.IsNullOrEmpty(artist) ? Visibility.Visible : Visibility.Collapsed; // hide artist if it's not available
            SongInfoStackPanel.Visibility = Visibility.Visible;
            BackgroundImage.Visibility = SettingsManager.Current.TaskbarWidgetBackgroundBlur ? Visibility.Visible : Visibility.Collapsed;

            // on top of XAML visibility binding (XAML binding only hides when disabled in settings)
            ControlsStackPanel.Visibility = SettingsManager.Current.TaskbarWidgetControlsEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;

            UpdateRotationPauseState();

            // Fade the widget in when it is appearing (hidden or mid-fade-out), so the
            // appear transition matches the song-change animation settings.
            if (Visibility != Visibility.Visible || _isFadingOut)
                AnimateFadeIn();
            else
                Visibility = Visibility.Visible;
        });
    }

    /// <summary>
    /// Falls back to the music-note placeholder when a new song's cover never arrives
    /// (the art deferral in <see cref="UpdateUi"/> kept the old cover meanwhile).
    /// </summary>
    private void ShowArtPlaceholder()
    {
        _noArtDebounceTimer?.Stop();
        _noArtDebounceTimer = null;

        if (!_hasAlbumCover)
            return;

        if (SettingsManager.Current.TaskbarWidgetAnimated)
            AnimateEntrance(); // snapshots the stale cover, fades to the placeholder

        _lastIcon = null;
        _hasAlbumCover = false;
        SongImage.ImageSource = null;
        SetBackground(null);
        SongImageBorder.Margin = new Thickness(0, 0, 0, -3); // align music note better when no cover
        UpdateAlbumArtOverlay();
    }

    /// <summary>
    /// Collapses the widget to the bare music-note placeholder. Only called once media
    /// has genuinely stopped (after the no-media debounce has elapsed).
    /// </summary>
    private void ShowNoMediaPlaceholder()
    {
        _noArtDebounceTimer?.Stop();
        _noArtDebounceTimer = null;
        _lastIcon = null;
        _actualTitle = string.Empty;
        _actualArtist = string.Empty;

        if (SettingsManager.Current.TaskbarWidgetHideCompletely)
        {
            AnimateFadeOut(() => Visibility = Visibility.Collapsed);
            return;
        }

        ControlsStackPanel.Visibility = Visibility.Collapsed;
        SongTitle.Text = string.Empty;
        SongArtist.Text = string.Empty;
        SongInfoStackPanel.Visibility = Visibility.Collapsed;
        SongInfoStackPanel.ToolTip = string.Empty;
        SongImagePlaceholder.Symbol = SymbolRegular.MusicNote220;
        SongImagePlaceholder.Visibility = Visibility.Visible;
        SongImage.ImageSource = null;
        SetBackground(null);
        _hasAlbumCover = false;
        SongImageBorder.Margin = new Thickness(0, 0, 0, -3); // align music note better when no cover

        MainBorder.Background = new SolidColorBrush(Colors.Transparent);
        MainBorder.Background.Opacity = 0;
        TopBorder.BorderBrush = Brushes.Transparent;

        Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Whether widget animations are active: both the widget animation toggle and the
    /// global flyout animation speed must be enabled, matching the main flyout behaviour.
    /// </summary>
    private bool AreAnimationsEnabled =>
        SettingsManager.Current.TaskbarWidgetAnimated && SettingsManager.Current.FlyoutAnimationSpeed != 0;

    /// <summary>
    /// Returns the user's chosen easing function, or <see langword="null"/> for linear
    /// when "linear" is selected, mirroring the main flyout's behaviour.
    /// </summary>
    private EasingFunctionBase? GetEasing(bool easeOut)
    {
        if (_mainWindow != null)
            return _mainWindow.getEasingStyle(easeOut); // null means linear, as in the main flyout
        return new CubicEase { EasingMode = easeOut ? EasingMode.EaseOut : EasingMode.EaseIn };
    }

    /// <summary>
    /// Fades the widget in when it appears (e.g. media starts playing again after being
    /// hidden, or the widget is re-enabled). Uses the global animation duration/easing.
    /// </summary>
    private void AnimateFadeIn()
    {
        _isFadingOut = false;
        BeginAnimation(OpacityProperty, null);

        if (!AreAnimationsEnabled)
        {
            Opacity = 1;
            Visibility = Visibility.Visible;
            return;
        }

        Visibility = Visibility.Visible;
        Opacity = 0;

        int msDuration = Math.Max(MainWindow.getDuration(), 1);
        DoubleAnimation fadeInAnimation = new()
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(msDuration),
            EasingFunction = GetEasing(true)
        };
        BeginAnimation(OpacityProperty, fadeInAnimation);
    }

    /// <summary>
    /// Fades the widget out, then invokes <paramref name="onComplete"/> (used to collapse
    /// the widget once it has fully disappeared).
    /// </summary>
    private void AnimateFadeOut(Action onComplete)
    {
        if (!AreAnimationsEnabled || Visibility != Visibility.Visible || Opacity <= 0)
        {
            onComplete();
            return;
        }

        _isFadingOut = true;

        int msDuration = Math.Max(MainWindow.getDuration(), 1);
        DoubleAnimation fadeOutAnimation = new()
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(msDuration),
            EasingFunction = GetEasing(false)
        };
        fadeOutAnimation.Completed += (s, e) =>
        {
            _isFadingOut = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            onComplete();
        };
        BeginAnimation(OpacityProperty, fadeOutAnimation);
    }

    /// <summary>
    /// Slides the song texts out to the left and the new ones in from the right when the
    /// track changes (song-change style 1). Only rows whose text actually changed slide;
    /// an unchanged row (typically the artist) stays put with its marquee untouched.
    /// </summary>
    /// <param name="oldTitle">Text currently displayed (slides out left).</param>
    /// <param name="oldArtist">Artist currently displayed (slides out left).</param>
    /// <param name="newTitle">Incoming title (slides in from the right).</param>
    /// <param name="newArtist">Incoming artist (slides in from the right).</param>
    /// <param name="animateTitle">Whether the title row changed and should slide.</param>
    /// <param name="animateArtist">Whether the artist row changed and should slide.</param>
    /// <param name="slideBackwards">True to mirror the direction (exit right, enter from left).</param>
    /// <returns>True when the slide was started and owns the text swap.</returns>
    private bool TryAnimateSongChangeSlide(string oldTitle, string oldArtist, string newTitle, string newArtist, bool animateTitle, bool animateArtist, bool slideBackwards)
    {
        try
        {
            if (!AreAnimationsEnabled || Visibility != Visibility.Visible || _isFadingOut)
                return false;

            // No laid-out containers yet (first paint): bail out before touching the width
            // caches so the next CalculateSize tick still sees the pending change.
            double currentTravel = double.IsNaN(SongTitleContainer.Width) ? 0 : SongTitleContainer.Width;
            if (currentTravel <= 0)
                return false;

            // Invalidate any in-flight slide (rapid skips): the newest song wins, older
            // ghosts are removed instantly instead of stacking up in the containers.
            _songChangeSlideVersion++;
            int version = _songChangeSlideVersion;
            CleanupSongChangeSlideGhosts();

            // Resize the text containers synchronously for the new strings (the caches are
            // already synced via _actualTitle/_actualArtist) so the slide travels the final
            // distance and the next CalculateSize tick sees no pending change to animate.
            var (logicalWidth, _) = ComputeTextLogicalWidth();
            SyncTextContainerWidths(logicalWidth);

            double titleTravel = double.IsNaN(SongTitleContainer.Width) ? 0 : SongTitleContainer.Width;
            double artistTravel = double.IsNaN(SongArtistContainer.Width) ? 0 : SongArtistContainer.Width;
            if (titleTravel <= 0)
                return false;

            int msDuration = Math.Max(MainWindow.getDuration(), 1);

            _songChangeSlideActive = true;

            // Rows whose text did not change stay put: no ghost, no entrance, and a
            // running marquee on that row is left untouched.
            bool titleHasOutgoing = animateTitle && !string.IsNullOrEmpty(oldTitle);
            bool artistHasOutgoing = animateArtist && !string.IsNullOrEmpty(oldArtist) && SongArtist.Visibility == Visibility.Visible;
            bool titleHasIncoming = animateTitle && !string.IsNullOrEmpty(newTitle);
            bool artistHasIncoming = animateArtist && !string.IsNullOrEmpty(newArtist);

            if (animateTitle)
                SongTitle.Visibility = Visibility.Visible;
            if (animateArtist)
            {
                // The artist row must stay visible while its ghost slides out, and appear
                // for the incoming text; it collapses again on completion when empty.
                if (artistHasOutgoing || artistHasIncoming)
                    SongArtist.Visibility = Visibility.Visible;
            }

            // Sequential phases within the same total time: the old text fully exits
            // first, then the new one enters, so they never share the screen and cannot
            // overlap no matter how long the texts are.
            int exitMs = Math.Max(msDuration / 2, 1);
            int enterMs = Math.Max(msDuration - exitMs, 1);

            if (animateArtist)
                SlideSingleText(SongArtist, SongArtistContainer, oldArtist, newArtist, artistTravel, exitMs, enterMs, 40, artistHasOutgoing, slideBackwards);
            if (animateTitle)
                SlideSingleText(SongTitle, SongTitleContainer, oldTitle, newTitle, titleTravel, exitMs, enterMs, 0, titleHasOutgoing, slideBackwards);

            // Settle once the longest row finishes: exit + entrance plus the artist
            // stagger when the artist row moves. The version check inside
            // FinishSongChangeSlide drops stale timers from rapid skips.
            // When nothing moves there is nothing to wait for: settle now.
            bool anyMotion = titleHasOutgoing || artistHasOutgoing || titleHasIncoming || artistHasIncoming;
            if (!anyMotion)
            {
                FinishSongChangeSlide(version, animateTitle, animateArtist, artistHasIncoming);
                return true;
            }

            bool artistMoves = animateArtist && (artistHasOutgoing || artistHasIncoming);
            int settleMs = exitMs + enterMs + (artistMoves ? 40 : 0);
            var settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(settleMs) };
            settleTimer.Tick += (s, e) =>
            {
                settleTimer.Stop();
                FinishSongChangeSlide(version, animateTitle, animateArtist, artistHasIncoming);
            };
            settleTimer.Start();

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during song-change slide animation");
            _songChangeSlideActive = false;
            CleanupSongChangeSlideGhosts();
            // Reattach marquee masks/scrolling in case they were suspended mid-flight.
            UpdateMarquees();
            return false;
        }
    }

    /// <summary>
    /// Slides one text row in two sequential phases: the outgoing ghost (old text) fully
    /// exits first, then the live <see cref="TextBlock"/> carrying the new text enters.
    /// The phases never overlap in time, so old and new text cannot share the screen no
    /// matter how long they are. Forward slides exit left / enter from the right;
    /// backward slides are mirrored. The marquee's edge-fade mask is suspended for the
    /// flight and restored by <see cref="UpdateMarquees(bool, bool)"/> on completion.
    /// </summary>
    private void SlideSingleText(System.Windows.Controls.TextBlock live, Canvas container, string oldText, string newText, double travel, int exitMs, int enterMs, int staggerMs, bool hasOutgoing, bool slideBackwards)
    {
        if (live.RenderTransform is not TranslateTransform incomingTransform)
            return;

        incomingTransform.BeginAnimation(TranslateTransform.XProperty, null);

        // Detach the stale marquee fade: its edge mask and forever-running gradient
        // animations belong to the old scroll timeline and would fade the sliding texts.
        bool isTitle = live == SongTitle;
        ref LinearGradientBrush? cachedMask = ref (isTitle ? ref _cachedTitleOpacityMask : ref _cachedArtistOpacityMask);
        if (cachedMask != null)
        {
            cachedMask.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, null);
            cachedMask.GradientStops[3].BeginAnimation(GradientStop.ColorProperty, null);
        }
        container.OpacityMask = null;

        // The ghost renders exactly what was displayed: a scrolling text at full width,
        // a static one at its laid-out width. Either way it must travel its whole
        // rendered width (plus a few pixels against measuring/rounding differences) so
        // no tail is left behind; overshooting is harmless (the containers clip).
        const double distanceEpsilon = 8.0;
        bool hasIncoming = !string.IsNullOrEmpty(newText);
        double renderedOldWidth = travel;
        if (hasOutgoing && !string.IsNullOrEmpty(oldText))
            renderedOldWidth = double.IsNaN(live.Width)
                ? StringWidth.GetStringWidth(oldText, 400) + distanceEpsilon
                : Math.Max(live.Width, 0) + distanceEpsilon;

        double exitTo = slideBackwards ? renderedOldWidth : -renderedOldWidth;
        // The old text is fully out when the entrance starts, so the new text can begin
        // with its head right at the container edge: it enters immediately, with no dead
        // off-screen travel and no empty-container gap.
        const double edgeEpsilon = 4.0;
        double enterFrom = slideBackwards ? -(travel + edgeEpsilon) : travel + edgeEpsilon;

        if (hasOutgoing && !string.IsNullOrEmpty(oldText))
        {
            var ghost = new System.Windows.Controls.TextBlock
            {
                Text = oldText,
                Foreground = live.Foreground,
                Opacity = live.Opacity,
                FontWeight = live.FontWeight,
                Width = live.Width,
                TextTrimming = live.TextTrimming,
                RenderTransform = new TranslateTransform()
            };
            container.Children.Add(ghost);
            _songChangeSlideGhosts.Add(ghost);

            var ghostTransform = (TranslateTransform)ghost.RenderTransform;
            var exit = new DoubleAnimation
            {
                From = 0,
                To = exitTo,
                Duration = TimeSpan.FromMilliseconds(exitMs),
                BeginTime = TimeSpan.FromMilliseconds(staggerMs),
                EasingFunction = GetEasing(false)
            };
            ghostTransform.BeginAnimation(TranslateTransform.XProperty, exit);
        }

        if (!hasIncoming)
        {
            live.Text = string.Empty;
            incomingTransform.X = 0;
            return;
        }

        // Full text during the flight: trimming/ellipsis only applies once settled
        // (UpdateMarquees restores it on completion).
        live.Width = double.NaN;
        live.TextTrimming = TextTrimming.None;
        live.Text = newText;

        // Park the new text off-screen at its entry point BEFORE the clock starts: while
        // the entrance waits out its BeginTime (the exit phase), the property holds this
        // base value instead of 0, so the incoming text never sits visible in place.
        // When the clock kicks in, From matches the base value and there is no jump.
        incomingTransform.X = enterFrom;

        var enter = new DoubleAnimation
        {
            From = enterFrom,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(enterMs),
            // The entrance waits for the exit (plus the row stagger), so both texts are
            // never visible at once; with no outgoing text it starts right away.
            BeginTime = TimeSpan.FromMilliseconds(staggerMs + (hasOutgoing ? exitMs : 0)),
            EasingFunction = GetEasing(true)
        };
        incomingTransform.BeginAnimation(TranslateTransform.XProperty, enter);
    }

    /// <summary>
    /// Settles a finished slide: removes ghosts, parks the animated rows at rest and
    /// resumes their marquee scrolling. Rows that did not change are left untouched.
    /// Stale completions from superseded rapid-skip slides are ignored.
    /// </summary>
    private void FinishSongChangeSlide(int version, bool animateTitle, bool animateArtist, bool artistHasIncoming)
    {
        if (version != _songChangeSlideVersion)
            return;

        _songChangeSlideActive = false;
        CleanupSongChangeSlideGhosts();

        if (animateTitle && SongTitle.RenderTransform is TranslateTransform titleTransform)
        {
            titleTransform.BeginAnimation(TranslateTransform.XProperty, null);
            titleTransform.X = 0;
        }
        if (animateArtist && SongArtist.RenderTransform is TranslateTransform artistTransform)
        {
            artistTransform.BeginAnimation(TranslateTransform.XProperty, null);
            artistTransform.X = 0;
        }

        if (animateArtist && !artistHasIncoming)
            SongArtist.Visibility = Visibility.Collapsed;

        UpdateMarquees(animateTitle, animateArtist);
    }

    /// <summary>
    /// Removes any outgoing ghost texts left by a previous (possibly superseded) slide.
    /// </summary>
    private void CleanupSongChangeSlideGhosts()
    {
        foreach (var ghost in _songChangeSlideGhosts)
        {
            if (ghost.RenderTransform is TranslateTransform transform)
                transform.BeginAnimation(TranslateTransform.XProperty, null);

            if (ghost.Parent is Canvas parent)
                parent.Children.Remove(ghost);
        }
        _songChangeSlideGhosts.Clear();
    }

    private void AnimateEntrance()
    {
        try
        {
            // When the widget is appearing from a collapsed state (or already fading out
            // because media stopped), the whole-control fade handles the transition; a
            // snapshot crossfade is only useful while the widget is fully visible.
            if (!AreAnimationsEnabled || Visibility != Visibility.Visible || _isFadingOut)
                return;

            int msDuration = Math.Max(MainWindow.getDuration(), 1);

            // Snapshot the current widget (old album) into the overlay and fade it out on
            // top of the new content underneath, so the artwork colours crossfade instead
            // of blanking out to the transparent background.
            // A transition is already in flight (rapid skips, or cover art arriving in
            // a later event than the title): swap instantly underneath it. The fading
            // old frame on top turns that swap into a crossfade on its own; re-arming
            // here would either snap half-faded texts or ghost the old overlay into
            // the new snapshot.
            if (CrossfadeOverlay.Visibility == Visibility.Visible)
                return;

            bool snapshotOk = RenderCrossfadeSnapshot();
#if DEBUG
            Logger.Debug($"Widget entrance: crossfade snapshot {(snapshotOk ? "ok" : "failed")}");
#endif
            if (!snapshotOk)
            {
                // Snapshot unavailable: fall back to fading the widget itself in place.
                DoubleAnimation opacityAnimation = new()
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(msDuration),
                    EasingFunction = GetEasing(true)
                };
                RootGrid.BeginAnimation(OpacityProperty, opacityAnimation);
                return;
            }

            DoubleAnimation fadeOutAnimation = new()
            {
                From = 1.0,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(msDuration),
                EasingFunction = GetEasing(true)
            };
            fadeOutAnimation.Completed += (s, e) =>
            {
                CrossfadeOverlay.BeginAnimation(OpacityProperty, null);
                CrossfadeOverlay.Visibility = Visibility.Collapsed;
                CrossfadeOverlay.Background = null;
            };

            CrossfadeOverlay.BeginAnimation(OpacityProperty, fadeOutAnimation);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during entrance animation");
        }
    }

    /// <summary>
    /// Renders the current widget (old album) into <see cref="CrossfadeOverlay"/> so it
    /// can be faded out on top of the freshly updated content underneath (true crossfade).
    /// </summary>
    /// <returns><see langword="true"/> when the snapshot was taken.</returns>
    private bool RenderCrossfadeSnapshot()
    {
        try
        {
            if (CrossfadeOverlay == null || RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
                return false;

            // Cap the snapshot resolution: the widget is tiny and the crossfade brief, so
            // rendering at full DPI is unnecessary and adds UI-thread work per song change.
            double dpi = Math.Min(VisualTreeHelper.GetDpi(this).PixelsPerDip, 1.5);
            int pixelWidth = Math.Max(1, (int)Math.Round(RootGrid.ActualWidth * dpi));
            int pixelHeight = Math.Max(1, (int)Math.Round(RootGrid.ActualHeight * dpi));

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            rtb.Render(RootGrid);
            rtb.Freeze();

            CrossfadeOverlay.Width = RootGrid.ActualWidth;
            CrossfadeOverlay.Height = RootGrid.ActualHeight;
            CrossfadeOverlay.Background = new ImageBrush(rtb) { Stretch = Stretch.Fill };
            CrossfadeOverlay.Visibility = Visibility.Visible;
            CrossfadeOverlay.Opacity = 1.0;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to render taskbar widget crossfade snapshot");
            return false;
        }
    }

    // event handlers for media control buttons
    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var session = _mainWindow.GetTaskbarSession();
        if (session == null) return;

        NoteTrackNavigation(forward: false);
        await session.ControlSession.TrySkipPreviousAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var session = _mainWindow.GetTaskbarSession();
        if (session == null) return;

        await session.ControlSession.TryTogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow == null) return;

        var session = _mainWindow.GetTaskbarSession();
        if (session == null) return;

        NoteTrackNavigation(forward: true);
        await session.ControlSession.TrySkipNextAsync();
    }

    // clicking the album art cycles through the available media sessions (circular list)
    private void SongImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mainWindow == null) return;

        _mainWindow.CycleTaskbarSession();
        UpdateAlbumArtOverlay();
    }

    private void SongImageBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        _albumArtHovering = true;
        UpdateAlbumArtOverlay();
    }

    private void SongImageBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        _albumArtHovering = false;
        UpdateAlbumArtOverlay();
    }

    // The switch-session chevron and the pause overlay share the same centered glyph element
    // (SongImagePlaceholder), so they are mutually exclusive by construction: while hovering
    // over the album art with several sessions available the chevron replaces the pause icon.
    private void UpdateAlbumArtOverlay()
    {
        if (!_hasAlbumCover)
        {
            // no cover: the music-note placeholder stands alone, nothing to overlay
            SongImagePlaceholder.Symbol = SymbolRegular.MusicNote220;
            SongImagePlaceholder.Visibility = Visibility.Visible;
            return;
        }

        bool showChevron = _albumArtHovering
            && _mainWindow != null
            && _mainWindow.GetTaskbarSessionCount() > 1;

        if (showChevron)
        { // same look as the pause overlay: dimmed art + centered dominant-color glyph
            SongImagePlaceholder.Symbol = SymbolRegular.ChevronRight20;
            SongImagePlaceholder.Visibility = Visibility.Visible;
            SongImage.Opacity = 0.4;
            return;
        }

        if (_isPaused && SettingsManager.Current.TaskbarWidgetShowPauseOverlay)
        { // show pause icon overlay
            SongImagePlaceholder.Symbol = SymbolRegular.Pause24;
            SongImagePlaceholder.Visibility = Visibility.Visible;
            SongImage.Opacity = 0.4;
            return;
        }

        SongImagePlaceholder.Visibility = Visibility.Collapsed;
        SongImage.Opacity = 1;
    }
}