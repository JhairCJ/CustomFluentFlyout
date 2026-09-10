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

        InitializeBars();
        ApplySettings();

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

        if (settings.TaskbarVisualizerBarCount != barCount)
        {
            barCount = Math.Clamp(settings.TaskbarVisualizerBarCount, 1, 32);
            InitializeBars();
        }

        engine.AudioSensitivity = settings.TaskbarVisualizerAudioSensitivity;
        engine.AudioPeakLevel = settings.TaskbarVisualizerAudioPeakLevel;
        engine.HighRefreshRate = settings.TaskbarVisualizerHighRefreshRate;
        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

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

    private void MainBorder_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        if (!settings.TaskbarVisualizerClickable || !settings.TaskbarVisualizerHasContent)
            return;
        MainBorder.Opacity = 0.9;
    }

    private void MainBorder_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        MainBorder.Opacity = 1;
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
        engine.FrameAvailable -= OnFrameAvailable;
        engine.Dispose();
    }
}
