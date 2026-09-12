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
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace FluentFlyout.App.Controls;

/// <summary>
/// Taskbar media widget with the full WPF feature set: dynamic width by measured
/// text, fixed-width mode, controls position (left/right) with follow animation,
/// text-style presets, marquee scrolling (ping-pong and loop-forever with edge
/// fades), song-change animations (crossfade snapshot and slide), atomic song
/// commit with debounces, pause overlay, session cycling and album accent.
/// WinUI lacks WPF animations on framework properties, so all motion is driven
/// with DispatcherQueue timers + Composition/translate transforms.
/// </summary>
public sealed partial class TaskbarWidgetControl : UserControl
{
    private const int BakedTextureSize = 256;
    private const int BlurPasses = 6;

    // Debounce windows, same as the WPF widget.
    private const int NoMediaDebounceMs = 700;
    private const int NoArtDebounceMs = 600;
    private const int SongCommitWaitMs = 350;

    private static readonly TimeSpan SlideDirectionLifetime = TimeSpan.FromSeconds(3);

    // ------------------------------------------------------------------
    // Rotation / background state (unchanged pipeline)
    // ------------------------------------------------------------------
    private bool rotationActive;
    private bool rotationPaused;
    private double pausedRotationAngle;
    private readonly Stopwatch spinStopwatch = Stopwatch.StartNew();
    private TimeSpan spinStartUtc;
    private double spinStartAngle;
    private double spinDurationSeconds = 20;
    private bool spinUp;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? spinTimer;

    private int bgCrossfadeVersion;
    private BitmapSource? bgCrossfadeTarget;

    // ------------------------------------------------------------------
    // Text / width state
    // ------------------------------------------------------------------
    private string actualTitle = string.Empty;
    private string actualArtist = string.Empty;
    private readonly Dictionary<string, double> textWidthCache = [];
    private double cachedLogicalWidth = -1;
    private bool marqueeActiveTitle;
    private bool marqueeActiveArtist;
    private double titleMarqueeOffset;
    private double artistMarqueeOffset;
    private int marqueeDirection = 1; // ping-pong direction
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? marqueeTimer;
    private readonly Stopwatch marqueeStopwatch = Stopwatch.StartNew();
    private TimeSpan marqueePhaseStart;
    private MarqueePhase marqueePhase = MarqueePhase.WaitStart;

    private enum MarqueePhase { WaitStart, Scroll, WaitEnd, ScrollBack, WaitEndBack }

    // ------------------------------------------------------------------
    // Song commit / debounce state
    // ------------------------------------------------------------------
    private MediaSnapshot? lastSnapshot;
    private bool isPaused;
    private bool hasPendingSong;
    private string pendingTitle = string.Empty;
    private string pendingArtist = string.Empty;
    private byte[]? pendingArtwork;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? commitTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? noMediaTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? noArtTimer;
    private DateTime lastInfoChangeUtc = DateTime.MinValue;

    /// <summary>Bumped whenever a commit publishes a new title/artist pair; the host window morphs its width off this.</summary>
    public int SongCommitVersion { get; private set; }

    /// <summary>Committed logical width, consumed by the host window to size the outer HWND.</summary>
    public double LogicalWidth { get; private set; } = 216;

    // Slide state
    private bool slideActive;
    private int slideVersion;
    private bool slideAnimatedTitle;
    private bool slideAnimatedArtist;
    private int slidePendingCompletions;
    private bool slideBackwardsPending;
    private DateTime slideDirectionNotedUtc = DateTime.MinValue;

    // Content crossfade (old content fades out over new)
    private bool contentCrossfadeRunning;
    private int contentCrossfadeVersion;

    private bool hasAlbumCover;
    private bool albumArtHovering;
    private byte[]? lastArtworkBytes;
    private bool disposed;

    public TaskbarWidgetControl()
    {
        InitializeComponent();

        ApplyWindowsTheme();
        ApplySettings();
        ActualThemeChanged += (_, _) => ApplyWindowsTheme();
        Loaded += (_, _) => { UpdateClip(); UpdateRotationPauseState(); };
    }

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
    // Public surface
    // ------------------------------------------------------------------

    public void ApplySettings()
    {
        var settings = App.Current.Runtime.Settings.Current;

        ControlsStackPanel.Visibility = settings.TaskbarWidgetControlsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        SongImageBorder.Visibility = settings.TaskbarWidgetShowAlbumArt
            ? Visibility.Visible : Visibility.Collapsed;

        ReorderControls(settings.TaskbarWidgetControlsPosition);

        ApplyCornerRadius(settings);
        ApplyButtonHoverRadius(settings);
        ApplyTextStyle(settings);

        double intensity = Math.Clamp(settings.TaskbarWidgetBackgroundBlurIntensity, 0, 100) / 100.0;
        if (intensity <= 0.01)
            intensity = 0.65;
        BackgroundImage.Opacity = intensity;
        BackgroundImageNext.Opacity = intensity;

        UpdateBackgroundMode();
        cachedLogicalWidth = -1; // force re-measure; settings affect width
        RecomputeLayout(force: true);
    }

    public void UpdateMedia(MediaSnapshot snapshot)
    {
        lastSnapshot = snapshot;

        // Media truly stopped / transient gap: debounce before collapsing.
        if (snapshot.PlaybackState == MediaPlaybackState.Closed ||
            (string.IsNullOrWhiteSpace(snapshot.Title) && string.IsNullOrWhiteSpace(snapshot.Artist)))
        {
            isPaused = true;
            UpdateRotationPauseState();
            StartNoMediaDebounce();
            return;
        }

        noMediaTimer?.Stop();
        noMediaTimer = null;

        bool wasPaused = isPaused;
        isPaused = snapshot.PlaybackState != MediaPlaybackState.Playing;

        string newTitle = string.IsNullOrWhiteSpace(snapshot.Title) ? "-" : snapshot.Title;
        string newArtist = string.IsNullOrWhiteSpace(snapshot.Artist) ? "-" : snapshot.Artist;

        ApplyPlaybackControlsAvailability();

        bool infoChanged = actualTitle != newTitle || actualArtist != newArtist;
        if (infoChanged)
            lastInfoChangeUtc = DateTime.UtcNow;
        bool artChanged = snapshot.ArtworkBytes is { Length: > 0 } && !ReferenceEquals(snapshot.ArtworkBytes, lastArtworkBytes);

        bool pendingChanged = !hasPendingSong
            || pendingTitle != newTitle
            || pendingArtist != newArtist
            || !ReferenceEquals(pendingArtwork, snapshot.ArtworkBytes);

        if (!infoChanged && !artChanged && !pendingChanged)
        {
            // Same song (pause toggle): refresh the instant UI only.
            PlayPauseIcon.Glyph = isPaused ? "\uE768" : "\uE769";
            UpdateAlbumArtOverlay();
            UpdateRotationPauseState();
            return;
        }

        pendingTitle = newTitle;
        pendingArtist = newArtist;
        pendingArtwork = snapshot.ArtworkBytes;
        hasPendingSong = true;

        // Fast path: a complete song (cover present) publishes now; otherwise wait
        // for the commit timer so rapid skips collapse onto the last track.
        if (snapshot.ArtworkBytes is { Length: > 0 })
        {
            commitTimer?.Stop();
            CommitPendingSong();
        }
        else
        {
            ArmCommitTimer();
            PlayPauseIcon.Glyph = isPaused ? "\uE768" : "\uE769";
            UpdateAlbumArtOverlay();
            UpdateRotationPauseState();
        }

        if (wasPaused != isPaused)
            UpdateRotationPauseState();
    }

    /// <summary>Notes an explicit track navigation so the next slide animates mirrored.</summary>
    public void NoteTrackNavigation(bool forward)
    {
        slideBackwardsPending = !forward;
        slideDirectionNotedUtc = DateTime.UtcNow;
    }

    // ------------------------------------------------------------------
    // Commit pipeline
    // ------------------------------------------------------------------

    private void ArmCommitTimer()
    {
        commitTimer ??= CreateTimer(SongCommitWaitMs, () =>
        {
            commitTimer!.Stop();
            CommitPendingSong();
        });
        commitTimer.Stop();
        commitTimer.Start();
    }

    private void CancelPendingSong()
    {
        hasPendingSong = false;
        pendingArtwork = null;
        slideBackwardsPending = false;
        commitTimer?.Stop();
    }

    private void StartNoMediaDebounce()
    {
        noMediaTimer ??= CreateTimer(NoMediaDebounceMs, () =>
        {
            noMediaTimer!.Stop();
            noMediaTimer = null;
            ShowNoMediaPlaceholder();
        });
        noMediaTimer.Stop();
        noMediaTimer.Start();
    }

    private void ShowNoMediaPlaceholder()
    {
        noArtTimer?.Stop();
        noArtTimer = null;
        CancelPendingSong();
        lastArtworkBytes = null;
        actualTitle = string.Empty;
        actualArtist = string.Empty;

        var settings = App.Current.Runtime.Settings.Current;
        if (settings.TaskbarWidgetHideCompletely)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        ControlsStackPanel.Visibility = Visibility.Collapsed;
        SongTitle.Text = string.Empty;
        SongArtist.Text = string.Empty;
        SongInfoStackPanel.Visibility = Visibility.Collapsed;
        SongImageBorderInner.Visibility = Visibility.Collapsed;
        SongImagePlaceholder.Visibility = Visibility.Visible;
        SetBackgroundLayers(null);
        hasAlbumCover = false;
        MainBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void CommitPendingSong()
    {
        if (!hasPendingSong)
            return;

        hasPendingSong = false;
        string newTitle = pendingTitle;
        string newArtist = pendingArtist;
        byte[]? artwork = pendingArtwork;
        pendingArtwork = null;

        bool slideBackwards = slideBackwardsPending
            && (DateTime.UtcNow - slideDirectionNotedUtc) <= SlideDirectionLifetime;
        slideBackwardsPending = false;

        var settings = App.Current.Runtime.Settings.Current;

        bool infoChanged = actualTitle != newTitle || actualArtist != newArtist;
        bool titleChanged = !string.Equals(actualTitle, newTitle, StringComparison.Ordinal);
        bool artistChanged = !string.Equals(actualArtist, newArtist, StringComparison.Ordinal);

        if (infoChanged)
        {
            string oldTitle = actualTitle;
            string oldArtist = actualArtist;
            actualTitle = newTitle;
            actualArtist = newArtist;

            SongCommitVersion++;
            RecomputeLayout(force: false);

            bool slid = infoChanged
                && settings.TaskbarWidgetSongChangeAnimation == 1
                && TryAnimateSongChangeSlide(oldTitle, oldArtist, newTitle, newArtist, titleChanged, artistChanged, slideBackwards);

            if (!slid)
            {
                if (settings.TaskbarWidgetAnimated && App.Current.Runtime.Settings.Current.FlyoutAnimationSpeed != 0)
                    StartContentCrossfade();
                SongTitle.Text = actualTitle;
                SongArtist.Text = actualArtist;
                UpdateMarquees();
            }
        }

        SongInfoStackPanel.SetValue(ToolTipService.ToolTipProperty, string.IsNullOrEmpty(newArtist) ? newTitle : newTitle + "\n\n" + newArtist);
        PlayPauseIcon.Glyph = isPaused ? "\uE768" : "\uE769";

        bool freshTitle = (DateTime.UtcNow - lastInfoChangeUtc).TotalMilliseconds < NoArtDebounceMs;
        if (artwork is { Length: > 0 })
        {
            noArtTimer?.Stop();
            noArtTimer = null;
            lastArtworkBytes = artwork;
            hasAlbumCover = true;
            _ = LoadArtworkAsync(artwork);
        }
        else if (hasAlbumCover && freshTitle)
        {
            // Cover not here yet: keep the old one, fall back to placeholder later.
            noArtTimer ??= CreateTimer(NoArtDebounceMs, () =>
            {
                noArtTimer!.Stop();
                noArtTimer = null;
                ShowArtPlaceholder();
            });
            noArtTimer.Stop();
            noArtTimer.Start();
        }
        else
        {
            noArtTimer?.Stop();
            noArtTimer = null;
            lastArtworkBytes = null;
            hasAlbumCover = false;
            SongImageBorderInner.Visibility = Visibility.Collapsed;
            SongImagePlaceholder.Visibility = Visibility.Visible;
            SetBackgroundLayers(null);
        }

        UpdateAlbumArtOverlay();
        UpdateRotationPauseState();
    }

    private void ShowArtPlaceholder()
    {
        if (!hasAlbumCover)
            return;

        hasAlbumCover = false;
        lastArtworkBytes = null;
        SongImageBorderInner.Visibility = Visibility.Collapsed;
        SongImagePlaceholder.Visibility = Visibility.Visible;
        SetBackgroundLayers(null);
        UpdateAlbumArtOverlay();
    }

    // ------------------------------------------------------------------
    // Text style presets
    // ------------------------------------------------------------------

    private void ApplyTextStyle(UserSettings settings)
    {
        string family = string.IsNullOrWhiteSpace(settings.TaskbarWidgetFontFamily)
            ? "Segoe UI Variable" : settings.TaskbarWidgetFontFamily;

        int titleSize = Math.Clamp(settings.TaskbarWidgetTitleFontSize, 10, 18);
        int artistSize = Math.Clamp(settings.TaskbarWidgetArtistFontSize, 10, 16);

        SongTitle.FontFamily = new FontFamily(family);
        SongArtist.FontFamily = new FontFamily(family);
        SongTitle.FontSize = titleSize;
        SongArtist.FontSize = artistSize;

        SongTitle.FontWeight = settings.TaskbarWidgetTextStyle switch
        {
            1 => Microsoft.UI.Text.FontWeights.Normal,
            2 => Microsoft.UI.Text.FontWeights.Bold,
            3 => Microsoft.UI.Text.FontWeights.Medium,
            _ => Microsoft.UI.Text.FontWeights.SemiBold,
        };
        SongArtist.FontWeight = settings.TaskbarWidgetTextStyle == 2
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
        SongArtist.FontStyle = settings.TaskbarWidgetTextStyle == 3
            ? Windows.UI.Text.FontStyle.Italic
            : Windows.UI.Text.FontStyle.Normal;
        SongArtist.Opacity = settings.TaskbarWidgetTextStyle switch
        {
            1 => 0.5,
            2 => 0.85,
            3 => 0.6,
            _ => 0.65,
        };

        SongTitleContainer.Height = Math.Ceiling(titleSize * 1.5);
        SongArtistContainer.Height = Math.Ceiling(artistSize * 1.5);
        Canvas.SetTop(SongTitle, Math.Max(0, (SongTitleContainer.Height - titleSize * 1.33) / 2));
        Canvas.SetTop(SongArtist, Math.Max(0, (SongArtistContainer.Height - artistSize * 1.33) / 2));

        textWidthCache.Clear();
    }

    private void ApplyCornerRadius(UserSettings settings)
    {
        double radius = settings.TaskbarWidgetBorderRadius;
        MainBorder.CornerRadius = new CornerRadius(radius);
        TaskbarBackground.CornerRadius = new CornerRadius(radius);
        SongImageBorder.CornerRadius = new CornerRadius(settings.TaskbarWidgetAlbumArtRadius);
        SongImageBorderInner.CornerRadius = new CornerRadius(settings.TaskbarWidgetAlbumArtRadius);
    }

    private void ApplyButtonHoverRadius(UserSettings settings)
    {
        var radius = new CornerRadius(settings.TaskbarWidgetButtonHoverRadius);
        PreviousButton.CornerRadius = radius;
        PlayPauseButton.CornerRadius = radius;
        NextButton.CornerRadius = radius;
    }

    private void ReorderControls(int position)
    {
        MainStackPanel.Children.Remove(ControlsStackPanel);
        if (position == 0)
        {
            MainStackPanel.Children.Insert(0, ControlsStackPanel);
            ControlsStackPanel.Margin = new Thickness(2, 0, 6, 0);
        }
        else
        {
            MainStackPanel.Children.Add(ControlsStackPanel);
            ControlsStackPanel.Margin = new Thickness(8, 0, 0, 0);
        }
    }

    // ------------------------------------------------------------------
    // Width computation (Core math)
    // ------------------------------------------------------------------

    private double MeasureText(string text, int fontWeight, int fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        string key = fontWeight + "|" + fontSize + "|" + text;
        if (textWidthCache.TryGetValue(key, out double cached))
            return cached;

        // Approximate the width with a hidden TextBlock probe (WinUI has no
        // FormattedText): measure at the current font settings of the rows.
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = fontSize >= 13 ? SongTitle.FontFamily : SongArtist.FontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight >= 700 ? Microsoft.UI.Text.FontWeights.Bold
                : fontWeight >= 600 ? Microsoft.UI.Text.FontWeights.SemiBold
                : fontWeight >= 500 ? Microsoft.UI.Text.FontWeights.Medium
                : Microsoft.UI.Text.FontWeights.Normal,
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = probe.DesiredSize.Width;
        if (textWidthCache.Count >= 256)
            textWidthCache.Clear();
        textWidthCache[key] = width;
        return width;
    }

    private (double logicalWidth, bool textChanged) ComputeLogicalWidth()
    {
        var settings = App.Current.Runtime.Settings.Current;
        bool titleChanged = SongTitle.Text != actualTitle;
        bool artistChanged = SongArtist.Text != actualArtist;

        double titleWidth = MeasureText(actualTitle, TitleWeight(settings), Math.Clamp(settings.TaskbarWidgetTitleFontSize, 10, 18));
        double artistWidth = MeasureText(actualArtist, ArtistWeight(settings), Math.Clamp(settings.TaskbarWidgetArtistFontSize, 10, 16));

        double logicalWidth = TaskbarWidgetMath.ComputeLogicalWidth(
            titleWidth,
            artistWidth,
            settings.TaskbarWidgetShowAlbumArt,
            settings.TaskbarWidgetControlsEnabled,
            settings.TaskbarWidgetFixedWidth,
            settings.TaskbarWidgetFixedWidthPx);

        return (logicalWidth, titleChanged || artistChanged);
    }

    private static int TitleWeight(UserSettings settings) => settings.TaskbarWidgetTextStyle switch
    {
        1 => 400, 2 => 700, 3 => 500, _ => 600,
    };

    private static int ArtistWeight(UserSettings settings) => settings.TaskbarWidgetTextStyle == 2 ? 600 : 400;

    /// <summary>
    /// Recomputes the logical width, applies the text container widths and restarts
    /// the marquees. Returns the new logical width for the host window.
    /// </summary>
    public double RecomputeLayout(bool force)
    {
        var (logicalWidth, textChanged) = ComputeLogicalWidth();

        if (!force && Math.Abs(logicalWidth - cachedLogicalWidth) < 0.5 && !textChanged)
            return logicalWidth;

        cachedLogicalWidth = logicalWidth;
        LogicalWidth = logicalWidth * TaskbarWidgetMath.Scale;

        var settings = App.Current.Runtime.Settings.Current;
        double rowWidth = TaskbarWidgetMath.TextRowWidth(logicalWidth, settings.TaskbarWidgetShowAlbumArt);
        SongTitleContainer.Width = rowWidth;
        SongArtistContainer.Width = rowWidth;

        SongTitle.Width = rowWidth;
        SongArtist.Width = rowWidth;

        if (textChanged || force)
            UpdateMarquees();

        return logicalWidth;
    }

    // ------------------------------------------------------------------
    // Marquee scrolling
    // ------------------------------------------------------------------

    private void UpdateMarquees()
    {
        var settings = App.Current.Runtime.Settings.Current;
        bool enabled = settings.TaskbarWidgetScrollingEnabled;

        UpdateMarqueeRow(
            SongTitle, SongTitleContainer,
            actualTitle, Math.Clamp(settings.TaskbarWidgetTitleFontSize, 10, 18),
            enabled, ref marqueeActiveTitle);
        UpdateMarqueeRow(
            SongArtist, SongArtistContainer,
            actualArtist, Math.Clamp(settings.TaskbarWidgetArtistFontSize, 10, 16),
            enabled, ref marqueeActiveArtist);

        bool anyActive = marqueeActiveTitle || marqueeActiveArtist;
        if (anyActive && marqueeTimer is null)
        {
            marqueeTimer = CreateTimer(33, TickMarquee);
            marqueePhaseStart = marqueeStopwatch.Elapsed;
            marqueePhase = MarqueePhase.WaitStart;
            marqueeDirection = 1;
        }
        else if (!anyActive && marqueeTimer is not null)
        {
            marqueeTimer.Stop();
            marqueeTimer = null;
            SetTranslateX(SongTitle, 0);
            SetTranslateX(SongArtist, 0);
        }
    }

    private void UpdateMarqueeRow(
        TextBlock textBlock, Canvas container,
        string text, int fontSize,
        bool enabled, ref bool active)
    {
        var settings = App.Current.Runtime.Settings.Current;
        double available = container.Width;
        double textWidth = MeasureText(text, TitleWeight(settings), fontSize);
        bool overflows = enabled && textWidth > available && available > 0;

        if (overflows)
        {
            active = true;
            textBlock.Text = settings.TaskbarWidgetScrollingTextLoopForever
                ? text + "\u00A0\u00A0\u00A0\u00A0\u00A0" + text
                : text;
            textBlock.Width = double.NaN;
            textBlock.TextTrimming = TextTrimming.None;
            // WinUI has no OpacityMask; the container clip provides the hard edge
            // (Composition mask-brush fades are a possible future refinement).
        }
        else
        {
            active = false;
            textBlock.Text = text;
            textBlock.Width = available;
            textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            SetTranslateX(textBlock, 0);
        }
    }

    private void TickMarquee()
    {
        var settings = App.Current.Runtime.Settings.Current;
        double speed = Math.Max(settings.TaskbarWidgetScrollingTextSpeed, 1);
        double dt = marqueeStopwatch.Elapsed.TotalSeconds - marqueePhaseStart.TotalSeconds;

        AdvanceRow(SongTitle, SongTitleContainer, actualTitle,
            Math.Clamp(settings.TaskbarWidgetTitleFontSize, 10, 18), TitleWeight(settings),
            ref titleMarqueeOffset, settings.TaskbarWidgetScrollingTextLoopForever, speed, dt);
        AdvanceRow(SongArtist, SongArtistContainer, actualArtist,
            Math.Clamp(settings.TaskbarWidgetArtistFontSize, 10, 16), ArtistWeight(settings),
            ref artistMarqueeOffset, settings.TaskbarWidgetScrollingTextLoopForever, speed, dt);
    }

    private void AdvanceRow(TextBlock textBlock, Canvas container, string text,
        int fontSize, int weight, ref double offset, bool loopForever, double speed, double dt)
    {
        if (!marqueeActiveTitle && textBlock == SongTitle && !marqueeActiveArtist && textBlock == SongArtist)
            return;
        bool isActive = textBlock == SongTitle ? marqueeActiveTitle : marqueeActiveArtist;
        if (!isActive || slideActive)
            return;

        double textWidth = MeasureText(text, weight, fontSize);
        double available = container.Width;
        if (loopForever)
        {
            double spacerWidth = MeasureText("\u00A0\u00A0\u00A0\u00A0\u00A0", weight, fontSize);
            double scrollDistance = TaskbarWidgetMath.ComputeLoopScrollDistance(textWidth, spacerWidth);
            offset = (offset + speed * dt) % scrollDistance;
            SetTranslateX(textBlock, -offset);
        }
        else
        {
            double scrollDistance = textWidth - available + 10;
            if (scrollDistance <= 0)
                return;
            offset += marqueeDirection * speed * dt;
            if (offset >= scrollDistance)
            {
                offset = scrollDistance;
                marqueeDirection = -1;
            }
            else if (offset <= 0)
            {
                offset = 0;
                marqueeDirection = 1;
            }
            SetTranslateX(textBlock, -offset);
        }
    }

    private static void SetTranslateX(FrameworkElement element, double x)
    {
        if (element.RenderTransform is TranslateTransform t)
        {
            t.X = x;
            return;
        }
        element.RenderTransform = new TranslateTransform { X = x };
    }

    // ------------------------------------------------------------------
    // Song-change animations
    // ------------------------------------------------------------------

    /// <summary>
    /// Crossfade entrance: the old content fades out over the new. WinUI cannot
    /// snapshot the live tree, so instead the *incoming* rows start transparent and
    /// fade in (composition opacity), which is visually equivalent over the
    /// taskbar-colored card.
    /// </summary>
    private void StartContentCrossfade()
    {
        if (!IsLoaded)
            return;

        contentCrossfadeVersion++;
        int version = contentCrossfadeVersion;
        contentCrossfadeRunning = true;

        int durationMs = Math.Max(GetAnimationDurationMs(), 1);
        double durationSeconds = durationMs / 1000.0;

        FadeInFrom(SongTitle, durationSeconds);
        FadeInFrom(SongArtist, durationSeconds);
        if (SongImageBorderInner.Visibility == Visibility.Visible)
            FadeInFrom(SongImageBorderInner, durationSeconds);

        // End the fade ownership after the duration; marquees restart on commit anyway.
        _ = Task.Delay(durationMs).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (version != contentCrossfadeVersion)
                    return;
                contentCrossfadeRunning = false;
            });
        });
    }

    private static void FadeInFrom(UIElement element, double seconds)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visual.StopAnimation("Opacity");
        visual.Opacity = 0f;

        var anim = compositor.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, 0f);
        anim.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        anim.Duration = TimeSpan.FromSeconds(seconds);
        visual.StartAnimation("Opacity", anim);
    }

    private static int GetAnimationDurationMs()
    {
        // Same 300ms default as the WPF global animation speed baseline.
        return 300;
    }

    private bool TryAnimateSongChangeSlide(string oldTitle, string oldArtist, string newTitle, string newArtist,
        bool animateTitle, bool animateArtist, bool slideBackwards)
    {
        try
        {
            if (!IsLoaded || Visibility != Visibility.Visible)
                return false;

            double travel = SongTitleContainer.Width;
            if (travel <= 0)
                return false;

            slideVersion++;
            int version = slideVersion;
            slideActive = true;
            slideAnimatedTitle = animateTitle;
            slideAnimatedArtist = animateArtist;
            slidePendingCompletions = (animateTitle ? 1 : 0) + (animateArtist ? 1 : 0);

            // Suspend marquees while the slide owns the transforms.
            marqueeTimer?.Stop();

            int durationMs = GetAnimationDurationMs();
            const int staggerMs = 40;

            if (animateArtist)
                SlideSingleText(SongArtist, SongArtistContainer, oldArtist, newArtist, durationMs, staggerMs, slideBackwards, version, OnRowCompleted);
            if (animateTitle)
                SlideSingleText(SongTitle, SongTitleContainer, oldTitle, newTitle, durationMs, 0, slideBackwards, version, OnRowCompleted);

            if (!animateTitle && !animateArtist)
            {
                slideActive = false;
                UpdateMarquees();
            }

            return animateTitle || animateArtist;
        }
        catch
        {
            slideActive = false;
            slideAnimatedTitle = false;
            slideAnimatedArtist = false;
            slidePendingCompletions = 0;
            UpdateMarquees();
            return false;
        }
    }

    private void OnRowCompleted()
    {
        if (--slidePendingCompletions > 0)
            return;
        slideActive = false;
        slideAnimatedTitle = false;
        slideAnimatedArtist = false;
        // Restore marquee scrolling after the slide settles.
        titleMarqueeOffset = 0;
        artistMarqueeOffset = 0;
        marqueeDirection = 1;
        marqueePhaseStart = marqueeStopwatch.Elapsed;
        marqueeTimer?.Start();
        UpdateMarquees();
    }

    /// <summary>
    /// Sequential per-row slide: old text exits fully, then new text enters.
    /// Uses a DispatcherQueueTimer-driven timeline because WinUI cannot animate
    /// TranslateTransform properties with easing timelines.
    /// </summary>
    private void SlideSingleText(TextBlock textBlock, Canvas container, string oldText, string newText,
        int durationMs, int staggerMs, bool slideBackwards, int version, Action onCompleted)
    {
        bool hasOld = !string.IsNullOrEmpty(oldText);
        bool hasNew = !string.IsNullOrEmpty(newText);

        if (!hasOld && !hasNew)
        {
            textBlock.Text = string.Empty;
            SetTranslateX(textBlock, 0);
            onCompleted();
            return;
        }

        int stagger = staggerMs >= durationMs ? 0 : staggerMs;
        bool twoPhase = hasOld && hasNew;
        int phaseMs = twoPhase ? Math.Max((durationMs - stagger) / 2, 1) : Math.Max(durationMs - stagger, 1);

        const double exitMargin = 8.0;
        const double enterEpsilon = 4.0;
        double containerWidth = container.Width;
        double newTextWidth = MeasureText(newText, textBlock == SongTitle ? TitleWeight(App.Current.Runtime.Settings.Current) : ArtistWeight(App.Current.Runtime.Settings.Current), (int)textBlock.FontSize);

        double exitTo = slideBackwards
            ? containerWidth + exitMargin
            : -(containerWidth + exitMargin);
        double enterFrom = slideBackwards
            ? -(newTextWidth + enterEpsilon)
            : containerWidth + enterEpsilon;

        textBlock.TextTrimming = TextTrimming.None;

        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        var stopwatch = Stopwatch.StartNew();
        double delaySeconds = stagger / 1000.0;
        double phaseSeconds = phaseMs / 1000.0;
        bool inEnter = false;
        double startX = 0, endX = 0;

        timer.Tick += (_, _) =>
        {
            if (version != slideVersion)
            {
                timer.Stop();
                return;
            }

            double t = stopwatch.Elapsed.TotalSeconds;
            if (t < delaySeconds)
                return;

            double phaseT = Math.Min((t - delaySeconds) / phaseSeconds, 1.0);
            // ease-out cubic
            double eased = 1 - Math.Pow(1 - phaseT, 3);

            if (!inEnter)
            {
                // Exit phase: old text travels from rest to exitTo.
                textBlock.Text = hasOld ? oldText : string.Empty;
                double x = startX + (endX - startX) * eased;
                SetTranslateX(textBlock, x);

                if (phaseT >= 1.0)
                {
                    if (!hasNew)
                    {
                        timer.Stop();
                        textBlock.Text = string.Empty;
                        SetTranslateX(textBlock, 0);
                        onCompleted();
                        return;
                    }
                    // Handoff: new text enters from the mirrored side.
                    inEnter = true;
                    stopwatch.Restart();
                    startX = enterFrom;
                    endX = 0;
                    textBlock.Text = newText;
                }
            }
            else
            {
                double x = startX + (endX - startX) * eased;
                SetTranslateX(textBlock, x);
                if (phaseT >= 1.0)
                {
                    timer.Stop();
                    SetTranslateX(textBlock, 0);
                    textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
                    onCompleted();
                }
            }
        };

        // Kick off with the exit parameters.
        textBlock.Text = oldText;
        startX = 0;
        endX = exitTo;
        SetTranslateX(textBlock, 0);
        timer.Start();
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

            var settings = App.Current.Runtime.Settings.Current;
            BitmapSource? baked = await BakeBlurredTextureAsync(artworkBytes, settings.TaskbarWidgetBackgroundBlurRadius);
            if (baked is not null)
                SetBackgroundLayers(baked);

            if (settings.UseAlbumArtAsAccentColor)
                AlbumAccentHelper.UpdateArtwork(artworkBytes, useAlbumAccent: true);
        }
        catch
        {
            SongImageBorderInner.Visibility = Visibility.Collapsed;
            SongImagePlaceholder.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Decodes the artwork, scales it to a square 256px texture and bakes the blur.
    /// The user's blur radius (DIPs on screen) is scaled into the bake space, matching
    /// the WPF bake where the on-screen look is identical at any resolution.
    /// </summary>
    private static async Task<BitmapSource?> BakeBlurredTextureAsync(byte[] artworkBytes, double blurRadiusDips)
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

            // Scale the user's blur radius (DIPs at ~216px widget) into the 256px bake.
            double discSide = 216 * TaskbarWidgetMath.Scale * 3; // rotating disc side estimate
            double blurRadius = blurRadiusDips * BakedTextureSize / Math.Max(discSide, 1);
            int passes = Math.Clamp((int)Math.Ceiling(blurRadius / 2), 2, 10);

            BoxBlur(buffer, BakedTextureSize, BakedTextureSize, passes);

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
        const int radius = 6;
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
    // Background rotation — unchanged from the previous WinUI implementation
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
        double discSide = Math.Max(Math.Max(width * sizeMultiplier, height * sizeMultiplier), 480);
        double offsetX = discSide * 0.28;

        bool showLeftSide = settings.TaskbarWidgetBackgroundRotateSide == 0;
        LayoutDiscLayer(BackgroundImage, width, height, discSide, offsetX, showLeftSide);
        LayoutDiscLayer(BackgroundImageNext, width, height, discSide, offsetX, showLeftSide);

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
            return;

        spinTimer = DispatcherQueue.CreateTimer();
        spinTimer.Interval = TimeSpan.FromMilliseconds(33);
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
    // Two-layer background crossfade
    // ------------------------------------------------------------------

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
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);

        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        batch.Completed += (_, _) =>
        {
            if (version != bgCrossfadeVersion)
                return;
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

    private void LayoutBackgroundToFillWidget()
    {
        double width = MainBorder.ActualWidth > 0 ? MainBorder.ActualWidth : 240;
        double height = MainBorder.ActualHeight > 0 ? MainBorder.ActualHeight : 40;
        double side = Math.Max(Math.Max(width, height), 1);

        LayoutFillLayer(BackgroundImage, width, height, side);
        LayoutFillLayer(BackgroundImageNext, width, height, side);

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
    // Theme + accent
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
        PauseOverlayIcon.Foreground = brush;
        PreviousButton.Foreground = brush;
        PlayPauseButton.Foreground = brush;
        NextButton.Foreground = brush;

        TaskbarBackground.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
    }

    // ------------------------------------------------------------------
    // Album art overlay (pause / session chevron)
    // ------------------------------------------------------------------

    private void UpdateAlbumArtOverlay()
    {
        if (!hasAlbumCover)
        {
            SongImagePlaceholder.Visibility = Visibility.Visible;
            PauseOverlayIcon.Visibility = Visibility.Collapsed;
            SongImage.Opacity = 1;
            return;
        }

        int sessionCount = 0;
        try { sessionCount = App.Current.Runtime.Media.GetSessions().Count; } catch { }

        bool showChevron = albumArtHovering && sessionCount > 1;
        if (showChevron)
        {
            SongImagePlaceholder.Glyph = "\uE76C"; // ChevronRight
            SongImagePlaceholder.Visibility = Visibility.Visible;
            SongImage.Opacity = 0.4;
            PauseOverlayIcon.Visibility = Visibility.Collapsed;
            return;
        }

        SongImagePlaceholder.Visibility = Visibility.Collapsed;
        if (isPaused && App.Current.Runtime.Settings.Current.TaskbarWidgetShowPauseOverlay)
        {
            PauseOverlayIcon.Visibility = Visibility.Visible;
            SongImage.Opacity = 0.4;
        }
        else
        {
            PauseOverlayIcon.Visibility = Visibility.Collapsed;
            SongImage.Opacity = 1;
        }
    }

    private void SongImageBorder_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        albumArtHovering = true;
        UpdateAlbumArtOverlay();
    }

    private void SongImageBorder_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        albumArtHovering = false;
        UpdateAlbumArtOverlay();
    }

    private void ApplyPlaybackControlsAvailability()
    {
        // The snapshot does not carry SMTC control flags; the media service exposes
        // sessions, so we keep all buttons enabled (the WPF dimming relies on
        // per-session control info that the Core surface does not model yet).
        PreviousButton.IsEnabled = true;
        PlayPauseButton.IsEnabled = true;
        NextButton.IsEnabled = true;
        PreviousButton.Opacity = 1;
        PlayPauseButton.Opacity = 1;
        NextButton.Opacity = 1;
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

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        NoteTrackNavigation(forward: false);
        await App.Current.Runtime.Media.PreviousAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        NoteTrackNavigation(forward: true);
        await App.Current.Runtime.Media.NextAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await PlayPauseAsync();

    /// <summary>
    /// Clicking the album art: cycles through available media sessions when several
    /// exist, otherwise opens the media flyout per settings.
    /// </summary>
    private async void SongImage_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        var sessions = App.Current.Runtime.Media.GetSessions();
        if (sessions.Count > 1)
        {
            // Cycle: activate the next session after the currently shown one by
            // toggling play/pause on it (SMTC has no "focus session" API; the WPF
            // app achieves switching by sending a play/pause to pin the session).
            int index = sessions.ToList().FindIndex(s => s.SessionId == lastSnapshot?.SessionId);
            var next = sessions[(index + 1) % sessions.Count];
            lastSnapshot = next;
            UpdateMedia(next);
            return;
        }

        if (settings.TaskbarWidgetClickOpensFlyout)
            App.Current.WindowManager.ShowMediaFlyout();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer CreateTimer(int intervalMs, Action tick)
    {
        var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
