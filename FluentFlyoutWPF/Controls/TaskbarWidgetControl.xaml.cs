// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls.TaskbarWidget;
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
    // Disc the running background crossfade (if any) is fading toward. Duplicate events
    // for identical content must leave that crossfade alone instead of restarting or
    // snapping it. Versioned like the slide so rapid skips collapse onto the latest art.
    private BitmapSource? _bgCrossfadeTarget;
    private int _bgCrossfadeVersion;
    // Bake currently running in UpdateBakedBackgroundAsync (icon + quantized side), so
    // burst events (duplicate metadata, resize ticks) don't stack parallel bakes.
    private BitmapImage? _bakingIcon;
    private double _bakingSide;

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

    // Atomic song commit: a song change arrives as a burst (texts first, cover art in
    // later events). A complete song (cover present) publishes synchronously in the same
    // UI block that starts the text entrance, so letters and background crossfade start
    // together; only an incomplete burst waits on the commit timer below. Each burst
    // event re-arms the timer; the last one wins. Playback state (pause/controls/overlay)
    // never waits: it is applied immediately in UpdateUi.
    private const int SongCommitWaitMs = 350;
    private DispatcherTimer? _commitTimer;
    private bool _hasPendingSong;
    private string _pendingTitle = string.Empty;
    private string _pendingArtist = string.Empty;
    private BitmapImage? _pendingIcon;

    // Slide settle tracking: the slide finishes when its slowest enter animation
    // completes (stagger of the artist row included), not on a wall-clock timer that
    // can drift and cause the final "teleport" snap.
    private int _slidePendingCompletions;

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
        ApplyTextStyle();

        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        // Initialize control order
        ReorderControls();

        // Apply the background mode (normal or animated rotation)
        UpdateBackgroundMode();

        // A forever rotation clock burns CPU/GPU even when nobody can see it
        // (autohide, widget collapsed, no media). Freeze it while hidden.
        IsVisibleChanged += (s, e) => UpdateRotationPauseState();

        // Release timers, clocks and ghost visuals when the host window goes away so
        // the control never keeps itself (or its DispatcherTimers) alive after teardown.
        Unloaded += (s, e) => CleanupWidgetResources();
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

    /// <summary>
    /// Widget-only typeface (bundled pack-URI fonts resolve here, anything else is
    /// a system font name), with a safe fallback.
    /// </summary>
    private static FontFamily WidgetFontFamily =>
        WidgetFonts.Resolve(SettingsManager.Current.TaskbarWidgetFontFamily);

    private static int WidgetTitleFontSize =>
        Math.Clamp(SettingsManager.Current.TaskbarWidgetTitleFontSize, 10, 18);

    private static int WidgetArtistFontSize =>
        Math.Clamp(SettingsManager.Current.TaskbarWidgetArtistFontSize, 10, 16);

    /// <summary>
    /// Title weight for the current text style preset (0 Modern: 600, 1 Classic: 400,
    /// 2 Bold: 700, 3 Soft: 500).
    /// </summary>
    private static int WidgetTitleWeight => SettingsManager.Current.TaskbarWidgetTextStyle switch
    {
        1 => 400,
        2 => 700,
        3 => 500,
        _ => 600,
    };

    private static int WidgetArtistWeight =>
        SettingsManager.Current.TaskbarWidgetTextStyle == 2 ? 600 : 400;

    private static double WidgetArtistOpacity => SettingsManager.Current.TaskbarWidgetTextStyle switch
    {
        1 => 0.5,
        2 => 0.85,
        3 => 0.6,
        _ => 0.65,
    };

    private static bool WidgetArtistItalic =>
        SettingsManager.Current.TaskbarWidgetTextStyle == 3;

    private static FontWeight ToWidgetFontWeight(int weight) => weight switch
    {
        >= 700 => FontWeights.Bold,
        >= 600 => FontWeights.SemiBold,
        >= 500 => FontWeights.Medium,
        _ => FontWeights.Normal,
    };

    /// <summary>
    /// Applies the widget-only typography (font family, sizes and the text style
    /// preset) to the song/artist rows. Called on load and live whenever one of the
    /// <c>TaskbarWidgetFont*</c> / <c>TaskbarWidgetText*</c> settings changes.
    /// Width caches are dropped so the next <see cref="CalculateSize"/> remeasures
    /// with the new metrics, and marquees restart under the new typeface.
    /// </summary>
    public void ApplyTextStyle()
    {
        FontFamily family = WidgetFontFamily;

        int titleSize = WidgetTitleFontSize;
        int artistSize = WidgetArtistFontSize;

        SongTitle.FontFamily = family;
        SongArtist.FontFamily = family;
        SongTitle.FontSize = titleSize;
        SongArtist.FontSize = artistSize;
        SongTitle.FontWeight = ToWidgetFontWeight(WidgetTitleWeight);
        SongArtist.FontWeight = ToWidgetFontWeight(WidgetArtistWeight);
        SongArtist.FontStyle = WidgetArtistItalic ? FontStyles.Italic : FontStyles.Normal;
        // While no slide transition owns the rows, the artist opacity belongs to the preset.
        if (!_songChangeSlideActive)
            SongArtist.Opacity = WidgetArtistOpacity;

        SongTitleContainer.Height = Math.Ceiling(titleSize * 1.5);
        SongArtistContainer.Height = Math.Ceiling(artistSize * 1.5);

        // The rows live in top-anchored Canvases (needed for the slide/marquee X
        // transforms), so without an explicit offset the glyphs hug the top edge and
        // the text looks bottom-heavy. Center each line in its container instead.
        CenterTextRow(SongTitle, SongTitleContainer);
        CenterTextRow(SongArtist, SongArtistContainer);

        _cachedTitleText = string.Empty;
        _cachedArtistText = string.Empty;
        _cachedTitleContainerWidth = -1;
        _cachedArtistContainerWidth = -1;

        UpdateMarquees();
    }

    /// <summary>
    /// Vertically centers a text row inside its <see cref="Canvas"/> container by
    /// offsetting <see cref="Canvas.Top"/> with the real line height of the current
    /// typeface, so any font/size/weight combination stays optically centered.
    /// Only the Y offset is touched: slide and marquee own the X transform.
    /// </summary>
    private static void CenterTextRow(System.Windows.Controls.TextBlock live, Canvas container)
    {
        var typeface = new Typeface(live.FontFamily, live.FontStyle, live.FontWeight, live.FontStretch);
        var probe = new FormattedText(
            "Ag",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            live.FontSize,
            Brushes.Black,
            null,
            1);
        double top = (container.Height - probe.Height) / 2;
        Canvas.SetTop(live, Math.Max(0, top));
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
            BackgroundImageNext.CacheMode = null;

            if (_currentIcon != null)
                BackgroundImage.Source = _currentIcon;
        }

        CancelBackgroundCrossfade();

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
        }

        // One shared angle owner for both background layers: the incoming layer fades in
        // on top of the old disc at the exact same angle, so the crossfade never jumps.
        // (A transform instance carries no parent, so sharing it is safe.)
        BackgroundImage.RenderTransform = _backgroundRotateTransform;
        BackgroundImageNext.RenderTransform = _backgroundRotateTransform;

        // blur is baked into the bitmap, so disable the live effect while rotating
        BackgroundImage.Effect = null;
        BackgroundImageNext.Effect = null;

        // Cache the rotating subtree as a bitmap: each frame becomes a cheap texture
        // transform instead of a full resample + clip of the large disc. RenderAtScale
        // 0.5 matches the 256px baked texture, so nothing visible is lost (it's all blur).
        if (BackgroundImage.CacheMode is not BitmapCache)
            BackgroundImage.CacheMode = new BitmapCache(0.5);
        if (BackgroundImageNext.CacheMode is not BitmapCache)
            BackgroundImageNext.CacheMode = new BitmapCache(0.5);

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
        LayoutDiscLayer(BackgroundImageNext, width, height, discSide, offsetX, showLeftSide);

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
    /// True crossfade between background discs: the incoming disc fades 0 -&gt; bound on the
    /// top layer (<see cref="BackgroundImageNext"/>) while the old disc stays at bound
    /// underneath, then the old layer adopts the new image (invisibly, fully covered) and
    /// the top layer parks. The composite never passes through transparent/black, unlike
    /// the old single-layer dip. Runs with the same duration/easing as the text entrance
    /// started in the same commit, so letters and background land together.
    /// Restarting toward a newer target kills the previous clock (removed clocks never
    /// complete), so rapid skips collapse onto the latest art with no stuck states.
    /// </summary>
    private void BeginBackgroundCrossfade(BitmapSource target, int durationMs)
    {
        // Nothing established yet (first paint): set directly, no fade from empty.
        if (BackgroundImage.Source == null)
        {
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
            return;
        }

        // Already showing exactly this with nothing in flight: park the top layer.
        if (ReferenceEquals(BackgroundImage.Source, target) && _bgCrossfadeTarget == null)
        {
            ParkBackgroundNextLayer();
            return;
        }

        // Already fading toward this exact disc: leave the running crossfade alone so it
        // completes with easing instead of restarting, and never snap it mid-flight.
        if (ReferenceEquals(_bgCrossfadeTarget, target))
            return;

        _bgCrossfadeVersion++;
        int version = _bgCrossfadeVersion;

        // Read the live value first (a restart continues from the partial opacity).
        double nextStart = BackgroundImageNext.Opacity;
        bool nextWasVisible = BackgroundImageNext.Visibility == Visibility.Visible;
        BackgroundImageNext.BeginAnimation(OpacityProperty, null);

        if (!AreAnimationsEnabled)
        {
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
            return;
        }

        // Fading back to what is already underneath (rapid A -> B -> A): reveal the
        // front layer instead of starting a new incoming fade.
        if (ReferenceEquals(BackgroundImage.Source, target))
        {
            _bgCrossfadeTarget = target;
            if (!nextWasVisible || nextStart <= 0.01)
            {
                ParkBackgroundNextLayer();
                return;
            }
            BackgroundImageNext.Opacity = nextStart;
            var fadeOut = new DoubleAnimation
            {
                From = nextStart,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = GetEasing(true)
            };
            fadeOut.Completed += (s, e) =>
            {
                if (version != _bgCrossfadeVersion)
                    return;
                ParkBackgroundNextLayer();
            };
            BackgroundImageNext.BeginAnimation(OpacityProperty, fadeOut);
            return;
        }

        _bgCrossfadeTarget = target;
        double bound = BackgroundImage.Opacity; // bound intensity both layers rest at
        BackgroundImageNext.Source = target;
        BackgroundImageNext.Visibility = Visibility.Visible;
        // Fresh start from 0; a restart continues from its partial value (no snap).
        BackgroundImageNext.Opacity = nextWasVisible ? nextStart : 0.0;

        var fadeIn = new DoubleAnimation
        {
            From = BackgroundImageNext.Opacity,
            To = bound,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = GetEasing(true)
        };
        fadeIn.Completed += (s, e) =>
        {
            if (version != _bgCrossfadeVersion)
                return;
            // Fully covered by the top layer: adopt underneath (invisible change).
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
        };
        BackgroundImageNext.BeginAnimation(OpacityProperty, fadeIn);
    }

    /// <summary>
    /// Parks the incoming background layer and forgets any running crossfade target.
    /// The front layer is never touched (it rests at the bound intensity), so this
    /// cannot snap or flash. Used when rotation stops, the widget is torn down, or a
    /// transition settles.
    /// </summary>
    private void ParkBackgroundNextLayer()
    {
        _bgCrossfadeTarget = null;
        BackgroundImageNext.BeginAnimation(OpacityProperty, null);
        BackgroundImageNext.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Kills a running background crossfade (bumping the version so its completion is
    /// ignored) and parks the incoming layer.
    /// </summary>
    private void CancelBackgroundCrossfade()
    {
        _bgCrossfadeVersion++;
        ParkBackgroundNextLayer();
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
        CancelBackgroundCrossfade();
        BackgroundImage.CacheMode = null;
        BackgroundImageNext.CacheMode = null;
        BackgroundImage.Effect = BackgroundImageBlurEffect;
        BackgroundImageNext.Effect = BackgroundImageNextBlurEffect;
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

        LayoutFillLayer(BackgroundImage, width, height, side);
        LayoutFillLayer(BackgroundImageNext, width, height, side);
    }

    /// <summary>
    /// Positions one static-mode background layer over the viewport.
    /// </summary>
    private static void LayoutFillLayer(System.Windows.Controls.Image layer, double width, double height, double side)
    {
        Canvas.SetLeft(layer, (width - side) / 2);
        Canvas.SetTop(layer, (height - side) / 2);
        layer.Width = side;
        layer.Height = side;
        layer.Margin = new Thickness(0);
        layer.Stretch = Stretch.UniformToFill;
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
            ParkBackgroundNextLayer();
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
            // Same song-change transition as the rotating disc (same duration/easing as
            // the text entrance started in this commit), so background and letters move
            // together instead of the background snapping or lagging behind.
            BeginBackgroundCrossfade(icon, TaskbarWidgetAnimationEnvironment.GetDurationMs());
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
        // Quantize the bake side: the baked texture is a fixed 256px blur where tiny
        // side differences are invisible, so small widget resizes (the title changed
        // length) must reuse the cached disc instead of rebaking and re-dipping.
        // Layout still uses the exact discSide; only the bake is quantized.
        double bakeSide = Math.Round(discSide / 16.0) * 16.0;

        if (_bakedBackground != null && ReferenceEquals(_bakedIcon, icon) && Math.Abs(_bakedSideDip - bakeSide) < 0.5)
        {
            // Identical content already baked: never snap or cancel here. A crossfade
            // already heading there is left alone to finish with easing.
            if (_backgroundRotationActive && !ReferenceEquals(BackgroundImage.Source, _bakedBackground))
                BeginBackgroundCrossfade(_bakedBackground, TaskbarWidgetAnimationEnvironment.GetDurationMs());
            return;
        }

        // Same bake already running (event burst / resize ticks): the in-flight task
        // will deliver it, don't stack another one.
        if (ReferenceEquals(_bakingIcon, icon) && Math.Abs(_bakingSide - bakeSide) < 0.5)
            return;

        // First paint ever: show the raw artwork right away; otherwise keep the previous
        // baked background until the new one is ready.
        if (_bakedBackground == null && !ReferenceEquals(_bakedIcon, icon))
            BackgroundImage.Source = icon;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double blurRadiusDips = SettingsManager.Current.TaskbarWidgetBackgroundBlurRadius;

        _bakingIcon = icon;
        _bakingSide = bakeSide;

        BitmapSource? baked;
#if DEBUG
        var bakeStopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif
        try
        {
            baked = await Task.Run(() => BakeBlurredBackground(icon, bakeSide, dpi, blurRadiusDips));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to bake blurred taskbar widget background");
            if (ReferenceEquals(_bakingIcon, icon))
                _bakingIcon = null;
            return;
        }
#if DEBUG
        bakeStopwatch.Stop();
#endif

        if (ReferenceEquals(_bakingIcon, icon) && Math.Abs(_bakingSide - bakeSide) < 0.5)
            _bakingIcon = null;

        // A newer song may have arrived while baking; discard the stale result.
        if (baked == null || !ReferenceEquals(_currentIcon, icon))
            return;

        _bakedIcon = icon;
        _bakedBackground = baked;
        _bakedSideDip = bakeSide;

        // Rotation may have been disabled while baking; the static path owns the layer then.
        if (!_backgroundRotationActive)
            return;

#if DEBUG
        Logger.Debug($"Widget background baked in {bakeStopwatch.Elapsed.TotalMilliseconds} ms, starting synced crossfade");
#endif

        // Start the disc crossfade now, in the same song change whose text entrance just
        // started: same duration/easing, so background and letters land together. (The bake
        // typically takes a few ms, so the offset is imperceptible.)
        BeginBackgroundCrossfade(baked, TaskbarWidgetAnimationEnvironment.GetDurationMs());
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
            _cachedTitleWidth = Math.Round(StringWidth.GetStringWidth(currentTitle, WidgetFontFamily, WidgetTitleWeight, WidgetTitleFontSize), 2);
            _cachedTitleText = currentTitle;
            textChanged = true;
        }
        if (!string.Equals(currentArtist, _cachedArtistText, StringComparison.Ordinal))
        {
            _cachedArtistWidth = Math.Round(StringWidth.GetStringWidth(currentArtist, WidgetFontFamily, WidgetArtistWeight, WidgetArtistFontSize), 2);
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

                double spacerWidth = StringWidth.GetStringWidth(spacer, WidgetFontFamily, isTitle ? WidgetTitleWeight : WidgetArtistWeight, isTitle ? WidgetTitleFontSize : WidgetArtistFontSize);
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
            // Media truly stopped (or the transient gap while switching tracks).
            // A pending atomic commit must never publish after the stop.
            CancelPendingSong();
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

        bool paused = playbackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        // Single UI-thread hop with every change batched: each Dispatcher.Invoke is
        // a queue round-trip plus its own layout pass, so three of them per metadata
        // event is pure overhead.
        Dispatcher.Invoke(() =>
        {
            // One snapshot per update: SettingsManager.Current is live, so re-reading it
            // per property wastes work and can observe an inconsistent mix mid-update.
            var settings = TaskbarWidgetSettingsSnapshot.Capture();

            _isPaused = paused;

            // Playback state never waits for the commit: controls, glyph and rotation
            // pause apply instantly; only identity (title/artist/cover/background)
            // goes through the atomic commit below.
            ApplyPlaybackControlsImmediate(playbackControls, settings);

            _noMediaDebounceTimer?.Stop();
            _noMediaDebounceTimer = null;

            string newTitle = !string.IsNullOrEmpty(title) ? title : "-";
            string newArtist = !string.IsNullOrEmpty(artist) ? artist : "-";

            // NOTE: the navigation direction note is deliberately NOT consumed here.
            // Intermediate same-song events (playback-state flaps caused by the button
            // press itself) arrive before the new title; consuming here would eat the
            // note and the real song change would default to forward. It is consumed
            // once, in CommitPendingSong, when the new identity actually publishes.

            bool infoChanged = _actualTitle != newTitle || _actualArtist != newArtist;
            if (infoChanged)
                _lastInfoChangeUtc = DateTime.UtcNow;
            bool artChanged = !ReferenceEquals(icon, _lastIcon);

            bool pendingChanged = !_hasPendingSong
                || _pendingTitle != newTitle
                || _pendingArtist != newArtist
                || !ReferenceEquals(_pendingIcon, icon);

            if (!infoChanged && !artChanged && !pendingChanged)
            {
                // Same song (e.g. pause toggle): no commit, just refresh the instant UI.
                if (settings.ControlsEnabled)
                    PlayPauseButton.Icon = _isPaused ? _playIcon : _pauseIcon;
                SongImagePlaceholder.Foreground = AlbumAccent.Brush;
                UpdateAlbumArtOverlay();
                ApplyCommitTail(settings, artist);
                UpdateRotationPauseState();
                return;
            }

            _pendingTitle = newTitle;
            _pendingArtist = newArtist;
            _pendingIcon = icon;
            _hasPendingSong = true;

            // Fast path: a complete song (cover present) publishes synchronously in this
            // very block, so the text entrance and the background crossfade start in the
            // same instant with the same duration/easing and land together. Only an
            // incomplete burst (cover still on its way) waits on the timer; when the
            // cover arrives it takes this same fast path and publishes immediately.
            if (icon != null)
            {
                _commitTimer?.Stop();
                CommitPendingSong();
                return;
            }

            ArmCommitTimer();

            if (settings.ControlsEnabled)
            {
                PlayPauseButton.Icon = _isPaused ? _playIcon : _pauseIcon;
            }

            // change color of icon
            SongImagePlaceholder.Foreground = AlbumAccent.Brush;

            UpdateAlbumArtOverlay();
            UpdateRotationPauseState();
        });
    }

    /// <summary>
    /// Applies the playback-control enablement immediately (never waits for the song commit).
    /// </summary>
    private void ApplyPlaybackControlsImmediate(GlobalSystemMediaTransportControlsSessionPlaybackControls? playbackControls, TaskbarWidgetSettingsSnapshot settings)
    {
        // adjust UI based on available controls
        if (settings.ControlsEnabled && playbackControls != null)
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
    }

    /// <summary>
    /// (Re)arms the atomic song-commit timer. Every burst event re-arms it, so the last
    /// event wins and intermediate songs of rapid skips are never published.
    /// </summary>
    private void ArmCommitTimer()
    {
        if (_commitTimer == null)
        {
            _commitTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(SongCommitWaitMs)
            };
            _commitTimer.Tick += (s, e) =>
            {
                _commitTimer.Stop();
                CommitPendingSong();
            };
        }

        _commitTimer.Stop();
        _commitTimer.Start();
    }

    /// <summary>
    /// Drops a buffered song without publishing it (media stopped).
    /// </summary>
    private void CancelPendingSong()
    {
        _hasPendingSong = false;
        _pendingIcon = null;
        _slideBackwardsPending = false;
        _commitTimer?.Stop();
    }

    /// <summary>
    /// Publishes the buffered song as one atomic transition: texts, cover and
    /// background change together. A commit without cover is definitive (placeholder
    /// immediately); a late cover arrives as its own commit with its own entrance.
    /// Must run on the UI thread.
    /// </summary>
    private void CommitPendingSong()
    {
        if (!_hasPendingSong)
            return;

        _hasPendingSong = false;
        string newTitle = _pendingTitle;
        string newArtist = _pendingArtist;
        BitmapImage? icon = _pendingIcon;
        _pendingIcon = null;

        // Consume the pending navigation direction here — and only here — so it applies
        // to the song change that actually publishes. Anything later (e.g. auto-advance
        // long after a button press) defaults to forward. Intermediate same-song events
        // between the press and the new title never touch the note.
        bool slideBackwards = _slideBackwardsPending
            && (DateTime.UtcNow - _slideDirectionNotedUtc) <= SlideDirectionLifetime;
        _slideBackwardsPending = false;

        var settings = TaskbarWidgetSettingsSnapshot.Capture();

        // Title/artist and cover art arrive in separate events on song change:
        // each half gets its own entrance so nothing pops in without a fade.
        bool infoChanged = _actualTitle != newTitle || _actualArtist != newArtist;
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

            bool slid = infoChanged
                && settings.SongChangeAnimation == 1
                && TryAnimateSongChangeSlide(oldTitle, oldArtist, newTitle, newArtist, titleChanged, artistChanged, slideBackwards);

            if (!slid)
            {
                // changed info
                if (settings.Animated)
                {
                    AnimateEntrance();
                }

                SongTitle.Text = _actualTitle;
                SongArtist.Text = _actualArtist;
            }
        }

        // Update tooltip with song info (single allocation, no += chain)
        SongInfoStackPanel.ToolTip = string.IsNullOrEmpty(newArtist) ? newTitle : newTitle + "\n\n" + newArtist;

        if (settings.ControlsEnabled)
        {
            PlayPauseButton.Icon = _isPaused ? _playIcon : _pauseIcon;
        }

        // change color of icon
        SongImagePlaceholder.Foreground = AlbumAccent.Brush;

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
        ApplyCommitTail(settings, newArtist);
        UpdateRotationPauseState();
    }

    /// <summary>
    /// Shared visibility tail for commits and same-song updates: row visibility,
    /// background/controls visibility, and the appear fade when (re)showing.
    /// </summary>
    /// <param name="artist">Raw artist string, used for the empty-artist collapse.</param>
    private void ApplyCommitTail(TaskbarWidgetSettingsSnapshot settings, string artist)
    {
        SongTitle.Visibility = Visibility.Visible;
        // While a slide is in flight the transition owns the artist row visibility
        // (kept visible for the outgoing ghost, collapsed on completion if empty).
        if (!_songChangeSlideActive)
            SongArtist.Visibility = !string.IsNullOrEmpty(artist) ? Visibility.Visible : Visibility.Collapsed; // hide artist if it's not available
        SongInfoStackPanel.Visibility = Visibility.Visible;
        // The canvas owns both background layers (front + incoming crossfade layer).
        BackgroundCanvas.Visibility = settings.BackgroundBlur ? Visibility.Visible : Visibility.Collapsed;

        // on top of XAML visibility binding (XAML binding only hides when disabled in settings)
        ControlsStackPanel.Visibility = settings.ControlsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Fade the widget in when it is appearing (hidden or mid-fade-out), so the
        // appear transition matches the song-change animation settings.
        if (Visibility != Visibility.Visible || _isFadingOut)
            AnimateFadeIn();
        else
            Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Stops timers/clocks and clears transient visuals so the control releases all
    /// UI-thread resources when its host window goes away.
    /// </summary>
    private void CleanupWidgetResources()
    {
        CancelPendingSong();
        _commitTimer = null;
        _noMediaDebounceTimer?.Stop();
        _noMediaDebounceTimer = null;
        _noArtDebounceTimer?.Stop();
        _noArtDebounceTimer = null;

        _songChangeSlideActive = false;
        _slidePendingCompletions = 0;
        CleanupSongChangeSlideGhosts();
        CancelBackgroundCrossfade();
        if (_backgroundRotateTransform != null)
            _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        BeginAnimation(OpacityProperty, null);
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
    private bool AreAnimationsEnabled => TaskbarWidgetAnimationEnvironment.AreAnimationsEnabled;

    /// <summary>
    /// Returns the user's chosen easing function, or <see langword="null"/> for linear
    /// when "linear" is selected, mirroring the main flyout's behaviour.
    /// </summary>
    private EasingFunctionBase? GetEasing(bool easeOut) =>
        TaskbarWidgetAnimationEnvironment.GetEasing(_mainWindow, easeOut);

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

        int msDuration = TaskbarWidgetAnimationEnvironment.GetDurationMs();
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

        int msDuration = TaskbarWidgetAnimationEnvironment.GetDurationMs();
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

            int msDuration = TaskbarWidgetAnimationEnvironment.GetDurationMs();

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

            // Exact settle: the slide ends when its slowest enter animation completes
            // (artist stagger included), never on a wall-clock timer that can drift and
            // cause the final "teleport" snap. Removed animation clocks never complete,
            // so a superseded rapid-skip slide settles nothing; the version check in
            // FinishSongChangeSlide is belt-and-braces.
            // When nothing moves there is nothing to wait for: settle now.
            bool anyMotion = titleHasOutgoing || artistHasOutgoing || titleHasIncoming || artistHasIncoming;
            if (!anyMotion)
            {
                FinishSongChangeSlide(version, animateTitle, animateArtist, artistHasIncoming);
                return true;
            }

            int expectedCompletions = (titleHasIncoming ? 1 : 0) + (artistHasIncoming ? 1 : 0);
            if (expectedCompletions == 0)
            {
                FinishSongChangeSlide(version, animateTitle, animateArtist, artistHasIncoming);
                return true;
            }

            _slidePendingCompletions = expectedCompletions;
            void OnRowEnterCompleted()
            {
                if (version != _songChangeSlideVersion)
                    return;
                if (--_slidePendingCompletions <= 0)
                    FinishSongChangeSlide(version, animateTitle, animateArtist, artistHasIncoming);
            }

            if (animateArtist)
                SlideSingleText(SongArtist, SongArtistContainer, oldArtist, newArtist, artistTravel, exitMs, enterMs, 40, artistHasOutgoing, slideBackwards, artistHasIncoming ? OnRowEnterCompleted : null);
            if (animateTitle)
                SlideSingleText(SongTitle, SongTitleContainer, oldTitle, newTitle, titleTravel, exitMs, enterMs, 0, titleHasOutgoing, slideBackwards, titleHasIncoming ? OnRowEnterCompleted : null);

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during song-change slide animation");
            _songChangeSlideActive = false;
            _slidePendingCompletions = 0;
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
    /// <param name="onEnterCompleted">Fired when this row's enter animation completes; used for exact settle.</param>
    private void SlideSingleText(System.Windows.Controls.TextBlock live, Canvas container, string oldText, string newText, double travel, int exitMs, int enterMs, int staggerMs, bool hasOutgoing, bool slideBackwards, Action? onEnterCompleted = null)
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
        bool ghostIsTitle = live == SongTitle;
        double renderedOldWidth = travel;
        if (hasOutgoing && !string.IsNullOrEmpty(oldText))
            renderedOldWidth = double.IsNaN(live.Width)
                ? StringWidth.GetStringWidth(oldText, WidgetFontFamily, ghostIsTitle ? WidgetTitleWeight : WidgetArtistWeight, ghostIsTitle ? WidgetTitleFontSize : WidgetArtistFontSize) + distanceEpsilon
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
                FontFamily = live.FontFamily,
                FontSize = live.FontSize,
                FontStyle = live.FontStyle,
                FontStretch = live.FontStretch,
                FontWeight = live.FontWeight,
                Width = live.Width,
                TextTrimming = live.TextTrimming,
                RenderTransform = new TranslateTransform()
            };
            // Same baseline as the live row: without this the outgoing text would
            // jump to the container top as soon as the slide starts.
            Canvas.SetTop(ghost, Canvas.GetTop(live));
            container.Children.Add(ghost);
            _songChangeSlideGhosts.Add(ghost);

            var ghostTransform = (TranslateTransform)ghost.RenderTransform;
            var exit = new DoubleAnimation
            {
                From = 0,
                To = exitTo,
                Duration = TimeSpan.FromMilliseconds(exitMs),
                BeginTime = TimeSpan.FromMilliseconds(staggerMs),
                // Ease-out (immediate start, soft landing) on exit: ease-in would leave
                // the old text visibly stuck at the start of its run (AGENTS.md §3).
                EasingFunction = GetEasing(true)
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
        if (onEnterCompleted != null)
            enter.Completed += (s, e) => onEnterCompleted();
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
        _slidePendingCompletions = 0;
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

            int msDuration = TaskbarWidgetAnimationEnvironment.GetDurationMs();

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