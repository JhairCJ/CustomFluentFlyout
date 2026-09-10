using FluentFlyout.Core;
using FluentFlyout.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FluentFlyout.App;

/// <summary>
/// Hosts the taskbar widget inside Explorer's taskbar. Owns placement (next to the
/// system tray per settings), DPI scaling, a slow reposition timer, auto-hide when
/// playback pauses and recovery if the taskbar is recreated.
/// </summary>
public sealed partial class TaskbarHostWindow : Window
{
    private const int WidgetLogicalWidth = 216;
    private const int WidgetLogicalHeight = 40;

    private readonly WindowInteropService interop = new();
    private readonly TaskbarHostController hostController = new();
    private readonly DispatcherQueueTimer repositionTimer;
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

        TryAttachToTaskbar();
    }

    public nint Hwnd => WindowNative.GetWindowHandle(this);

    public void TryAttachToTaskbar()
    {
        attached = hostController.TryAttach(Hwnd, WidgetLogicalWidth, WidgetLogicalHeight);
        if (attached)
        {
            UpdatePosition();
            repositionTimer.Start();
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

        var settings = App.Current.Runtime.Settings.Current;
        if (!settings.TaskbarWidgetEnabled)
            return;

        var (x, y, taskbarWidth, taskbarHeight, dpiScale) = hostController.ComputePlacement(
            WidgetLogicalWidth,
            WidgetLogicalHeight,
            settings.TaskbarWidgetPosition,
            settings.TaskbarWidgetManualPadding,
            settings.TaskbarVisualizerEnabled);

        int physicalWidth = (int)Math.Round(WidgetLogicalWidth * dpiScale);
        int physicalHeight = (int)Math.Round(WidgetLogicalHeight * dpiScale);

        // Fit inside the taskbar if the widget is wider than it.
        if (taskbarWidth > 0 && physicalWidth > taskbarWidth - 8)
            physicalWidth = Math.Max(40, taskbarWidth - 8);

        bool shouldBeVisible = forceShow
            || !settings.TaskbarWidgetAutoHide
            || lastPlaybackState == MediaPlaybackState.Playing;

        hostController.PlaceAt(x, y, physicalWidth, physicalHeight, show: shouldBeVisible && hasPublishedMedia);
    }

    public void UpdateMedia(MediaSnapshot snapshot)
    {
        lastPlaybackState = snapshot.PlaybackState;

        bool hasRealMedia = !string.IsNullOrWhiteSpace(snapshot.Title);
        if (hasRealMedia)
            hasPublishedMedia = true;

        WidgetControl.UpdateMedia(snapshot);

        var settings = App.Current.Runtime.Settings.Current;
        if (settings.UseAlbumArtAsAccentColor)
            Controls.AlbumAccentHelper.UpdateArtwork(snapshot.ArtworkBytes, useAlbumAccent: true);

        // First real media (or resume): position, then show.
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
