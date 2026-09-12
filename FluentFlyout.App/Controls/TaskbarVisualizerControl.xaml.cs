using System.Diagnostics;
using FluentFlyout.Core;
using FluentFlyout.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace FluentFlyout.App.Controls;

/// <summary>
/// Taskbar equalizer with the WPF pipeline: capture thread publishes raw targets,
/// the UI render loop eases bars toward them with frame-rate-independent
/// attack/release, supports centered/baseline modes, auto-hide with an 800 ms
/// grace, album-accent-colored bars and click-to-settings.
/// </summary>
public sealed partial class TaskbarVisualizerControl : UserControl, IDisposable
{
    private readonly VisualizerEngine engine;
    private readonly List<Rectangle> barRects = [];
    private readonly DispatcherQueueTimer renderTimer;

    private int barCount = 10;
    private double barWidth = 4;
    private double barSpacing = 3;
    private double attackSeconds = 0.036;
    private double releaseSeconds = 0.49;
    private int smoothingKey = -1;

    private readonly Stopwatch renderStopwatch = Stopwatch.StartNew();
    private double lastRenderTime;

    private const int AutoHideGraceMs = 800;
    private DateTime lastAudibleUtc = DateTime.MinValue;
    private bool hasContent;
    private bool disposed;

    public TaskbarVisualizerControl()
    {
        InitializeComponent();

        renderTimer = DispatcherQueue.CreateTimer();
        renderTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / 30);
        renderTimer.Tick += (_, _) => RenderFrame();

        engine = new VisualizerEngine(barCount);
        engine.FrameAvailable += OnFrameAvailable;

        // Album accent color changes must re-paint the bars; without this the bars
        // stay frozen on the brush created at construction time.
        AlbumAccentHelper.AccentChanged += OnAccentChanged;

        InitializeBars();
        ApplySettings();

        ActualThemeChanged += (_, _) => ApplySettings();

        Loaded += (_, _) =>
        {
            var settings = App.Current.Runtime.Settings.Current;
            if (settings.TaskbarVisualizerEnabled)
            {
                engine.Start();
                renderTimer.Start();
            }
        };
        Unloaded += (_, _) =>
        {
            renderTimer.Stop();
            engine.Stop();
        };
    }

    private void OnAccentChanged(object? sender, Color color)
    {
        // Called from a background thread: marshal before touching the tree.
        DispatcherQueue.TryEnqueue(() =>
        {
            var brush = new SolidColorBrush(color);
            foreach (var rect in barRects)
                rect.Fill = brush;
        });
    }

    public void ApplySettings()
    {
        var settings = App.Current.Runtime.Settings.Current;

        bool enabled = settings.TaskbarVisualizerEnabled;
        if (enabled && !engine.IsRunning)
        {
            engine.Start();
            renderTimer.Start();
        }
        else if (!enabled && engine.IsRunning)
        {
            renderTimer.Stop();
            engine.Stop();
        }
        else if (enabled)
        {
            // Engine alive but the render timer may have been stopped by an earlier
            // Unloaded (host window recreation): keep both sides in sync.
            renderTimer.Start();
        }

        // High refresh rate is a hop-size change inside the engine: restart so the
        // setting takes effect live, like the WPF OnTaskbarVisualizerHighRefreshRateChanged.
        if (enabled && engine.IsRunning)
            engine.HighRefreshRate = settings.TaskbarVisualizerHighRefreshRate;

        if (settings.TaskbarVisualizerBarCount != barCount)
        {
            barCount = Math.Clamp(settings.TaskbarVisualizerBarCount, 1, 32);
            InitializeBars();
        }

        engine.AudioSensitivity = settings.TaskbarVisualizerAudioSensitivity;
        engine.AudioPeakLevel = settings.TaskbarVisualizerAudioPeakLevel;
        engine.HighRefreshRate = settings.TaskbarVisualizerHighRefreshRate;

        // Blend into the taskbar like the widget card does; the background must be a
        // SolidColorBrush so the hover animation can target its Color/Opacity.
        bool isDark = ActualTheme == ElementTheme.Dark;
        MainBorder.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));

        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Hover effect (WPF parity): animated background color/opacity fade and a
    // top highlight border, only when the visualizer is clickable with content.
    // ------------------------------------------------------------------

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? hoverTimer;
    private Stopwatch hoverStopwatch = Stopwatch.StartNew();
    private bool hoverAnimating;
    private double hoverFromOpacity;
    private const double HoverTargetOpacityDark = 0.075;
    private const double HoverTargetOpacityLight = 0.6;
    private const int HoverDurationMs = 200;

    private void MainBorder_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        if (!settings.TaskbarVisualizerClickable || !settings.TaskbarVisualizerHasContent)
            return;

        bool isDark = ActualTheme == ElementTheme.Dark;
        TopBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(93, 255, 255, 255));
        TopBorder.Opacity = isDark ? 0.25 : 1;

        StartHoverAnimation(isDark ? HoverTargetOpacityDark : HoverTargetOpacityLight);
    }

    private void MainBorder_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (hoverTimer is null && MainBorder.Opacity == 1)
        {
            TopBorder.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            return;
        }

        TopBorder.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        StartHoverAnimation(0);
    }

    /// <summary>
    /// 200 ms eased fade of the card background opacity toward the target (in on
    /// hover, out on leave) — the WinUI equivalent of the WPF ColorAnimation pair.
    /// </summary>
    private void StartHoverAnimation(double targetOverlayOpacity)
    {
        // The overlay fade is emulated by animating an overlay rect's opacity; we
        // reuse TopBorder.Opacity for the highlight and MainBorder background alpha
        // via a simple timer-driven interpolation on a dedicated hover brush.
        hoverFromOpacity = hoverOverlayCurrent;
        hoverOverlayTarget = targetOverlayOpacity;
        hoverStopwatch.Restart();

        hoverTimer ??= DispatcherQueue.CreateTimer();
        hoverTimer.Interval = TimeSpan.FromMilliseconds(16);
        hoverTimer.Tick += (_, _) =>
        {
            double t = Math.Min(hoverStopwatch.Elapsed.TotalMilliseconds / HoverDurationMs, 1.0);
            double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // ease-in-out cubic
            hoverOverlayCurrent = hoverFromOpacity + (hoverOverlayTarget - hoverFromOpacity) * eased;
            HoverOverlay.Opacity = hoverOverlayCurrent;
            if (t >= 1.0)
            {
                hoverTimer.Stop();
                hoverAnimating = false;
            }
        };
        hoverTimer.Start();
        hoverAnimating = true;
    }

    private double hoverOverlayCurrent;
    private double hoverOverlayTarget;

    private void InitializeBars()
    {
        BarsCanvas.Children.Clear();
        barRects.Clear();

        var brush = new SolidColorBrush(AlbumAccentHelper.GetAccentColor());

        for (int i = 0; i < barCount; i++)
        {
            var rect = new Rectangle
            {
                Width = barWidth,
                Height = 2,
                RadiusX = 2,
                RadiusY = 2,
                Fill = brush,
            };
            BarsCanvas.Children.Add(rect);
            barRects.Add(rect);
        }

        double totalSpacing = (barCount - 1) * barSpacing;
        double availableWidth = BarsCanvas.Width - totalSpacing;
        barWidth = Math.Max(1, availableWidth / barCount);

        for (int i = 0; i < barRects.Count; i++)
        {
            barRects[i].Width = barWidth;
            Canvas.SetLeft(barRects[i], i * (barWidth + barSpacing));
            Canvas.SetTop(barRects[i], BarsCanvas.Height);
        }
    }

    private void OnFrameAvailable(object? sender, VisualizerFrame frame)
    {
        // Capture thread: stamp audibility; the render loop does everything else.
        if (frame.HasSignal)
            lastAudibleUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Resolves smoothing constants once per setting change. Same feel as WPF:
    /// attack 12ms (snappy) .. 60ms, release 80ms (lively) .. 900ms (slow melt).
    /// </summary>
    private void EnsureSmoothing()
    {
        int s = Math.Clamp(App.Current.Runtime.Settings.Current.TaskbarVisualizerSmoothing, 0, 100);
        if (s == smoothingKey)
            return;
        smoothingKey = s;

        float t = s / 100f;
        attackSeconds = 0.012 + t * 0.048;
        releaseSeconds = 0.08 + t * 0.82;
    }

    private void RenderFrame()
    {
        if (disposed || !engine.IsRunning)
            return;

        double now = renderStopwatch.Elapsed.TotalSeconds;
        double dt = now - lastRenderTime;
        lastRenderTime = now;
        if (dt <= 0 || dt > 0.25)
            dt = 1.0 / 60.0;

        EnsureSmoothing();

        var targets = engine.Targets;
        int count = Math.Min(barCount, targets.Length);

        float attackFactor = 1f - (float)Math.Exp(-dt / attackSeconds);
        float releaseFactor = 1f - (float)Math.Exp(-dt / releaseSeconds);

        var settings = App.Current.Runtime.Settings.Current;
        bool centeredBars = settings.TaskbarVisualizerCenteredBars;
        int barBaseline = settings.TaskbarVisualizerBaseline ? 4 : 0;
        double canvasHeight = BarsCanvas.Height;

        bool resting = true;
        for (int i = 0; i < count; i++)
        {
            float target = targets[i];
            var rect = barRects[i];
            float current = (float)((canvasHeight - Canvas.GetTop(rect)) / canvasHeight);
            current = Math.Clamp(current, 0f, 1f);

            float next = target > current
                ? current + (target - current) * attackFactor
                : current + (target - current) * releaseFactor;

            if (next is < 0.0005f and > -0.0005f)
                next = 0f;

            double height = Math.Max(next * canvasHeight, barBaseline);
            if (settings.TaskbarVisualizerCenteredBars)
            {
                Canvas.SetTop(rect, (canvasHeight - height) / 2);
            }
            else
            {
                Canvas.SetTop(rect, canvasHeight - height);
            }
            rect.Height = height;

            if (next > 0.01f)
                resting = false;
        }

        // Auto-hide with grace: hides once bars settle and the grace elapsed.
        bool autoHides = settings.TaskbarVisualizerBaseline && settings.TaskbarVisualizerBaselineAutoHide;
        bool audibleRecently = (DateTime.UtcNow - lastAudibleUtc).TotalMilliseconds < AutoHideGraceMs;
        bool shouldShow = !autoHides || audibleRecently || !resting;
        SetHasContent(shouldShow);
    }

    private void SetHasContent(bool value)
    {
        if (hasContent == value)
            return;
        hasContent = value;
        App.Current.Runtime.Settings.Current.TaskbarVisualizerHasContent = value;
        BarsCanvas.Opacity = value ? 1 : 0;
    }

    private void MainBorder_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        if (!settings.TaskbarVisualizerClickable || !settings.TaskbarVisualizerHasContent)
            return;
        App.Current.ShowSettings("Visualizer");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        renderTimer.Stop();
        AlbumAccentHelper.AccentChanged -= OnAccentChanged;
        engine.FrameAvailable -= OnFrameAvailable;
        engine.Dispose();
    }
}
