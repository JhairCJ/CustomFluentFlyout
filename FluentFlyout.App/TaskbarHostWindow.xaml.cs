using System.Diagnostics;
using FluentFlyout.Core;
using FluentFlyout.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FluentFlyout.App;

/// <summary>
/// Hosts the taskbar widget inside Explorer's taskbar. Owns placement (next to the
/// system tray per settings), DPI scaling, a slow reposition timer, delayed auto-hide
/// when playback pauses and recovery if the taskbar is recreated. The window width
/// morphs with the widget's dynamic width (per-song text length) following the
/// widget's SongCommitVersion, animated when TaskbarWidgetResizeAnimated is on.
/// </summary>
public sealed partial class TaskbarHostWindow : Window
{
    private const int WidgetLogicalHeight = 40;
    private const int AutoHideDelayMs = 750;
    private const int ResizeMorphMs = 300;

    private readonly WindowInteropService interop = new();
    private readonly TaskbarHostController hostController = new();
    private readonly DispatcherQueueTimer repositionTimer;
    private readonly DispatcherQueueTimer autoHideTimer;
    private readonly DispatcherQueueTimer resizeTimer;
    private int lastCommitVersion = -1;
    private double currentLogicalWidth = 216;
    private double morphFromWidth;
    private double morphToWidth;
    private readonly Stopwatch morphStopwatch = Stopwatch.StartNew();
    private bool attached;
    private bool hasPublishedMedia;
    private MediaPlaybackState lastPlaybackState = MediaPlaybackState.Unknown;

    public TaskbarHostWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        interop.SetToolWindow(Hwnd);
        interop.SetNoActivate(Hwnd);

        repositionTimer = DispatcherQueue.CreateTimer();
        repositionTimer.Interval = TimeSpan.FromMilliseconds(1500);
        repositionTimer.Tick += (_, _) => UpdatePosition();

        autoHideTimer = DispatcherQueue.CreateTimer();
        autoHideTimer.Interval = TimeSpan.FromMilliseconds(AutoHideDelayMs);
        autoHideTimer.IsRepeating = false;
        autoHideTimer.Tick += (_, _) =>
        {
            // Only hide if playback did not resume during the grace period.
            if (lastPlaybackState != MediaPlaybackState.Playing)
                hostController.SetVisible(false);
        };

        // Resize morph clock: the WinUI equivalent of the WPF outer-width DoubleAnimation.
        resizeTimer = DispatcherQueue.CreateTimer();
        resizeTimer.Interval = TimeSpan.FromMilliseconds(16);
        resizeTimer.Tick += (_, _) => TickResizeMorph();

        TryAttachToTaskbar();
    }

    public nint Hwnd => WindowNative.GetWindowHandle(this);

    public void TryAttachToTaskbar()
    {
        attached = hostController.TryAttach(Hwnd, (int)currentLogicalWidth, WidgetLogicalHeight);
        if (attached)
        {
            UpdatePosition();
            repositionTimer.Start();
        }
    }

    /// <summary>
    /// Animates the outer window width between the previous committed width and the
    /// widget's new logical width, honoring the resize-animation settings.
    /// </summary>
    private void BeginResizeMorph(double newLogicalWidth)
    {
        var settings = App.Current.Runtime.Settings.Current;
        bool animate = settings.TaskbarWidgetResizeAnimated
            && settings.TaskbarWidgetAnimated
            && settings.FlyoutAnimationSpeed != 0;

        if (!animate || Math.Abs(newLogicalWidth - currentLogicalWidth) < 1)
        {
            currentLogicalWidth = newLogicalWidth;
            UpdatePosition(forceShow: true);
            return;
        }

        morphFromWidth = currentLogicalWidth;
        morphToWidth = newLogicalWidth;
        morphStopwatch.Restart();
        resizeTimer.Start();
    }

    private void TickResizeMorph()
    {
        double t = Math.Min(morphStopwatch.Elapsed.TotalMilliseconds / ResizeMorphMs, 1.0);
        // ease-out cubic, same feel as the WPF width morph
        double eased = 1 - Math.Pow(1 - t, 3);
        currentLogicalWidth = morphFromWidth + (morphToWidth - morphFromWidth) * eased;
        UpdatePosition(forceShow: true);

        if (t >= 1.0)
        {
            resizeTimer.Stop();
            currentLogicalWidth = morphToWidth;
            UpdatePosition(forceShow: true);
        }
    }

    /// <summary>Recomputes placement and applies it. Show only after real media arrived.</summary>
    public void UpdatePosition(bool forceShow = false)
    {
        if (!attached || hostController.ParentTaskbarHandle == 0)
        {
            TryAttachToTaskbar();
            if (!attached)
                return;
        }

        // Explorer restarts re-create Shell_TrayWnd; our window is orphaned then.
        // Re-attach instead of positioning against a dead parent.
        if (!hostController.IsTaskbarParentValid())
        {
            attached = false;
            hostController.Reset();
            TryAttachToTaskbar();
            if (!attached)
                return;
        }

        var settings = App.Current.Runtime.Settings.Current;
        if (!settings.TaskbarWidgetEnabled)
            return;

        // Visualizer sits on the configured side of the widget.
        bool visualizerLeft = settings.TaskbarVisualizerPosition == 0;
        VisualizerControl.SetValue(Microsoft.UI.Xaml.Controls.Grid.ColumnProperty, visualizerLeft ? 0 : 1);
        WidgetControl.SetValue(Microsoft.UI.Xaml.Controls.Grid.ColumnProperty, visualizerLeft ? 1 : 0);
        VisualizerControl.Margin = visualizerLeft
            ? new Thickness(0, 0, 8, 0)
            : new Thickness(8, 0, 0, 0);

        double totalLogicalWidth = currentLogicalWidth;
        if (settings.TaskbarVisualizerEnabled)
            totalLogicalWidth += 128; // visualizer canvas + margins

        var (x, y, taskbarWidth, taskbarHeight, dpiScale) = hostController.ComputePlacement(
            (int)Math.Round(totalLogicalWidth),
            WidgetLogicalHeight,
            settings.TaskbarWidgetPosition,
            settings.TaskbarWidgetManualPadding,
            settings.TaskbarVisualizerEnabled);

        int physicalWidth = (int)Math.Round(totalLogicalWidth * dpiScale);
        int physicalHeight = (int)Math.Round(WidgetLogicalHeight * dpiScale);

        // Fit inside the taskbar if the widget is wider than it.
        if (taskbarWidth > 0 && physicalWidth > taskbarWidth - 8)
            physicalWidth = Math.Max(40, taskbarWidth - 8);

        // Startup gate: never show the idle placeholder. Only real media (a snapshot
        // with a title) unlocks visibility; forceShow alone can't bypass it.
        bool playing = lastPlaybackState == MediaPlaybackState.Playing;
        bool shouldBeVisible = hasPublishedMedia
            && (forceShow || !settings.TaskbarWidgetAutoHide || playing);

        hostController.PlaceAt(x, y, physicalWidth, physicalHeight, show: shouldBeVisible);
    }

    public void UpdateMedia(MediaSnapshot snapshot)
    {
        lastPlaybackState = snapshot.PlaybackState;

        // Only a real track (with a title) counts as published media. Pause/stop
        // snapshots of an empty session must never unlock the placeholder.
        bool hasRealMedia = !string.IsNullOrWhiteSpace(snapshot.Title);
        if (hasRealMedia)
            hasPublishedMedia = true;

        WidgetControl.UpdateMedia(snapshot);

        // Follow song commits: recompute the widget's dynamic width and morph the
        // outer window when the commit version advanced (identity change only).
        if (WidgetControl.SongCommitVersion != lastCommitVersion)
        {
            lastCommitVersion = WidgetControl.SongCommitVersion;
            double logicalWidth = WidgetControl.RecomputeLayout(force: false) * 0.9;
            BeginResizeMorph(logicalWidth);
        }

        var settings = App.Current.Runtime.Settings.Current;
        if (settings.UseAlbumArtAsAccentColor)
            Controls.AlbumAccentHelper.UpdateArtwork(snapshot.ArtworkBytes, useAlbumAccent: true);

        // Auto-hide with a grace period: pausing hides after 750 ms unless playback
        // resumes; playing cancels a pending hide immediately.
        if (hasPublishedMedia && settings.TaskbarWidgetAutoHide)
        {
            if (lastPlaybackState == MediaPlaybackState.Playing)
                autoHideTimer.Stop();
            else
                autoHideTimer.Start();
        }
        else
        {
            autoHideTimer.Stop();
        }

        if (hasPublishedMedia)
            UpdatePosition(forceShow: hasRealMedia);
    }

    public void ApplySettings()
    {
        WidgetControl.ApplySettings();
        VisualizerControl.ApplySettings();
        VisualizerControl.Visibility = App.Current.Runtime.Settings.Current.TaskbarVisualizerEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatePosition(forceShow: true);
    }

    public void SetVisualizerEnabled(bool enabled)
    {
        VisualizerControl.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdatePosition(forceShow: true);
    }
}
