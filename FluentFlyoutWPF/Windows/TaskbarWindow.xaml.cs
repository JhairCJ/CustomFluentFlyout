// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyout.Windows;

/// <summary>
/// Interaction logic for TaskbarWindow.xaml
/// </summary>
public partial class TaskbarWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly DispatcherTimer _timer;
    private readonly int _nativeWidgetsPadding = 216;
    private readonly double _scale = 0.9;

    private IntPtr _trayHandle;
    private AutomationElement? _widgetElement;
    private AutomationElement? _trayElement;
    private AutomationElement? _taskbarFrameElement;
    // Bounds cache: every forced reposition (each metadata event in a song-change
    // burst) used to round-trip COM on the UI thread with blocking Waits. Bounds
    // move with the taskbar itself, so a short TTL is visually identical.
    private readonly Dictionary<string, (Rect rect, DateTime utc)> _automationBoundsCache = [];
    private static readonly TimeSpan AutomationBoundsTtl = TimeSpan.FromSeconds(3);
    // reference to main window for flyout functions
    private MainWindow? _mainWindow;
    private int _lastSelectedMonitor = -1;
    private bool _positionUpdateInProgress;
    private readonly Dictionary<string, Task> _pendingAutomationTasks = [];

    // Last known taskbar window rect, used to skip redundant position updates.
    private RECT _lastTaskbarRect;
    private bool _hasTaskbarRect;

    // Last applied orientation transforms. LayoutTransform triggers a full
    // measure/arrange pass, so only reassign when the orientation actually flips.
    private bool? _lastWidgetIsVertical;
    private bool? _lastVisualizerIsVertical;

    // Animated song-change resize: the widget width follows the song text, so a new
    // song means a new width. Instead of snapping, the outer Width (+ anchored
    // Left/Top, so the position setting stays the anchor) morphs with the same
    // duration/easing as the text/background entrance inside the widget.
    // Versioned like the background crossfade: rapid songs collapse onto the latest
    // target (removed clocks never complete). Only song-identity commits animate;
    // timer ticks, settings changes and the first paint apply instantly.
    private int _widgetResizeVersion;
    private bool _widgetResizeRunning;
    private int _lastSeenSongCommitVersion;
    private double _widgetResizeTargetWidth = double.NaN;
    private double _widgetResizeTargetLeft = double.NaN;
    private double _widgetResizeTargetTop = double.NaN;
    // Latest widget targets in DIPs, published by PositionWidget for PositionVisualizer
    // (which runs next, while the widget may still be mid-flight on stale base values).
    private double _widgetTargetLeftDips;
    private double _widgetTargetTopDips;
    private double _widgetTargetWidthDips;
    private double _vizResizeTargetLeft = double.NaN;
    private double _vizResizeTargetTop = double.NaN;
    private Rect _widgetResizeUnionRect;
    private Rect _vizResizeUnionRect;
    private Rect _widgetResizeFinalRect;
    private Rect _vizResizeFinalRect;
    private IntPtr _widgetResizeWindowHandle;

    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastPlaybackStatus;
    private DispatcherTimer? _autoHideTimer;

    // One top-level transparent HWND throughout; reparenting a WPF layered window
    // can leave its surface invisible even though the native hit region survives.
    private bool _widgetExpanded;
    private bool _expansionOwnsLayout;
    private bool _expansionAnimating;
    private int _expansionVersion;
    private MouseClickOutsideHook? _expansionOutsideHook;
    private Rect _compactWidgetRect = Rect.Empty; // taskbar-local physical pixels
    private Rect _visualizerRect = Rect.Empty;
    private Rect _taskbarScreenRect = Rect.Empty;
    private Rect _monitorWorkArea = Rect.Empty;
    private Rect _monitorArea = Rect.Empty;
    private DateTime _monitorGeometryCheckedUtc;
    private Rect _expansionFlightRect = Rect.Empty;
    private Rect _expansionTargetRect = Rect.Empty;
    private Rect _expandedAnchorRect = Rect.Empty;
    private Vector _canvasOffsetPhysical;
    private double _positionDpiScale = 1;
    private IntPtr _taskbarHandle;
    private Rect _nativeHostScreenRect = Rect.Empty;
    private Rect[] _nativeRegions = [];
    private Vector _nativeRegionOffset;


    // Startup gating: the window must never flash the idle placeholder (music note +
    // controls) in a corner while Explorer is still settling. It stays hidden until a
    // real song has been published AND a good position has been computed at least once.
    private bool _hasPublishedMedia;
    private bool _hasEverBeenPositioned;
    private int _visibilityVersion;
    private bool _windowFadingOut;
    private WinEventProc? _shellZOrderProc;
    private IntPtr _shellZOrderHook, _shellForegroundHook;
    private bool _topmostRefreshPending, _closed;

    public TaskbarWindow()
    {
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        WindowHelper.SetTopmost(this);

        // Set DataContext for bindings
        DataContext = SettingsManager.Current;

        _timer = new DispatcherTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250); // lightweight native geometry/visibility check
        _timer.Tick += (s, e) => UpdatePosition();
        _timer.Start();

        // Show once so Loaded fires (SetupWindow parenting + MainWindow wiring), then hide
        // again in the same synchronous tick: nothing reaches the screen before Hide, so
        // startup never flashes the music-note placeholder in a corner. The window only
        // reappears via EnsureWindowVisible once real media arrives (see UpdateUi).
        Show();
        Visibility = Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource source = (HwndSource)PresentationSource.FromDependencyObject(this);
        source.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0084) // WM_NCHITTEST
        {
            bool transparent = !IsHitTestVisible || Opacity <= 0.01;
            if (!transparent && (!Widget.IsHitTestVisible || Widget.Visibility != Visibility.Visible || Widget.Opacity <= 0.01))
            {
                long packed = lParam.ToInt64();
                var point = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
                var widgetRect = _compactWidgetRect;
                if (!widgetRect.IsEmpty)
                {
                    widgetRect.Offset(_taskbarScreenRect.Left, _taskbarScreenRect.Top);
                    transparent = widgetRect.Contains(point);
                }
            }
            if (transparent)
            {
                handled = true;
                return new IntPtr(-1); // HTTRANSPARENT: invisible content cannot receive clicks.
            }
        }
        // Some interface mods may collect information from all windows associated with the taskbar,
        // causing the widget and the entire taskbar to freeze.
        // For example, Nilesoft Shell and "Click on empty taskbar space" from Windhawk.
        // Therefore, we are preventing the propagation of this message.
        // Also prevents the widget from blocking taskbar's message processing, which is another source of freezes.
        switch (msg)
        {
            case 0x003D: // WM_GETOBJECT (Sent by Microsoft UI Automation to obtain information about an accessible object contained in a server application)
            case 0x0018: // WM_SHOWWINDOW
            case 0x0046: // WM_WINDOWPOSCHANGING - Triggers during alt-tabs, window changes
            case 0x0083: // WM_NCCALCSIZE - Can trigger layout storms
            case 0x0281: // WM_IME_SETCONTEXT - IME conflicts
            case 0x0282: // WM_IME_NOTIFY
                handled = true;
                return IntPtr.Zero;

                // Handle other known harmless messages that are sent when FluentFlyout starts, Windows locks, etc.
                // Needs testing
                //case 0x0047:
                //case 0x02B1:
                //case 0x001E:
                //case 0x0164:
                //case 0xC25F:
                //    handled = true;
                //    return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetupWindow();
        if (Application.Current.MainWindow is MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            Widget.SetMainWindow(mainWindow);
        }
    }

    private IntPtr GetSelectedTaskbarHandle(out bool isMainTaskbarSelected)
    {
        var monitors = MonitorUtil.GetMonitors();
        var selectedMonitor = monitors[Math.Clamp(SettingsManager.Current.TaskbarWidgetSelectedMonitor, 0, monitors.Count - 1)];
        isMainTaskbarSelected = true;

        // Get the main taskbar and check if it is on the selected monitor.
        var mainHwnd = FindWindow("Shell_TrayWnd", null);
        if (MonitorUtil.GetMonitor(mainHwnd).deviceId == selectedMonitor.deviceId)
            return mainHwnd;

        if (monitors.Count == 1)
            return mainHwnd;

        isMainTaskbarSelected = false;
        if (monitors.Count == 2)
        {
            var hwnd = FindWindow("Shell_SecondaryTrayWnd", null);
            if (MonitorUtil.GetMonitor(hwnd).deviceId == selectedMonitor.deviceId)
            {
                return hwnd;
            }
            else
            {
                isMainTaskbarSelected = true;
                return mainHwnd;
            }
        }

        // If there are more than two monitors, we will need to enumerate all existing windows
        // to find all Shell_SecondaryTrayWnd among them.

        IntPtr secondHwnd = IntPtr.Zero;
        StringBuilder className = new(256); // 256 is the maximum class name length
        IntPtr checkWindowClass(IntPtr wnd)
        {
            var len = GetClassName(wnd, className, className.Capacity);
            if (className.Equals("Shell_SecondaryTrayWnd"))
            {
                if (MonitorUtil.GetMonitor(wnd).deviceId == selectedMonitor.deviceId)
                {
                    return wnd;
                }
            }
            return IntPtr.Zero;
        }

        // Get the threadId of the main taskbar and check all windows created in the same thread.
        // This is very fast, but in some cases Shell_TrayWnd and other Shell_SecondaryTrayWnd's may be created in different threads.
        // Actually, I couldn't achieve that kind of behavior.
        if (mainHwnd != IntPtr.Zero)
        {
            uint threadId = GetWindowThreadProcessId(mainHwnd, IntPtr.Zero);
            EnumThreadWindows(threadId, (wnd, param) =>
            {
                secondHwnd = checkWindowClass(wnd);
                if (secondHwnd != IntPtr.Zero)
                    return false; // stop

                return true;
            }, IntPtr.Zero);

            if (secondHwnd != IntPtr.Zero)
                return secondHwnd;
        }

        // If for some reason the taskbars were created in different threads or simply could not be found,
        // we try to find them among all existing windows.
        EnumWindows((wnd, param) =>
        {
            secondHwnd = checkWindowClass(wnd);
            if (secondHwnd != IntPtr.Zero)
                return false; // stop

            return true;
        }, IntPtr.Zero);

        if (secondHwnd != IntPtr.Zero)
            return secondHwnd;

        // Logger.Debug($"No taskbar found on the selected monitor. Using the main taskbar.");
        isMainTaskbarSelected = true;
        return mainHwnd;
    }

    private void SetupWindow()
    {
        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr taskbarWindowHandle = interop.Handle;

            //Background = _hitTestTransparent; // ensures that non-content areas also trigger MouseEnter event

            IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);

            int style = GetWindowLong(taskbarWindowHandle, GWL_STYLE);
            style = (style & ~WS_CHILD) | WS_POPUP;
            SetWindowLong(taskbarWindowHandle, GWL_STYLE, style);

            CalculateAndSetPosition(taskbarHandle, taskbarWindowHandle, isMainTaskbarSelected);
            InstallShellZOrderHooks();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during setup");
        }
    }

    private void UpdateWindowRegion(IntPtr windowHandle, params Rect[] rects)
    {
        if (_nativeRegionOffset == _canvasOffsetPhysical && _nativeRegions.SequenceEqual(rects)) return;
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        foreach (var r in rects)
        {
            // make sure rect is not empty - happens when setting elements to collapsed
            if (r == Rect.Empty)
                continue;

            IntPtr newRgn = CreateRectRgn(
                (int)Math.Floor(r.Left + _canvasOffsetPhysical.X),
                (int)Math.Floor(r.Top + _canvasOffsetPhysical.Y),
                (int)Math.Ceiling(r.Right + _canvasOffsetPhysical.X),
                (int)Math.Ceiling(r.Bottom + _canvasOffsetPhysical.Y));
            if (newRgn == IntPtr.Zero)
            {
                Logger.Error($"Taskbar Widget error during CreateRectRgn({(int)r.Left}, {(int)r.Top}, {(int)r.Right}, {(int)r.Bottom}).");
                goto on_error;
            }

            if (CombineRgn(rgn, rgn, newRgn, 2 /*RGN_OR*/) == 0)
            {
                Logger.Error($"Taskbar Widget error during CombineRgn. Combined regions: {string.Join(", ", rects.Select(i => $"RECT({(int)i.Left}, {(int)i.Top}, {(int)i.Right}, {(int)i.Bottom})"))}");
                DeleteObject(newRgn);
                goto on_error;
            }

            DeleteObject(newRgn);
        }

        if (SetWindowRgn(windowHandle, rgn, true) == 0)
        {
            Logger.Error($"Taskbar Widget error during SetWindowRgn.");
            goto on_error;
        }

        _nativeRegions = (Rect[])rects.Clone();
        _nativeRegionOffset = _canvasOffsetPhysical;

        // Simple debugging to display the window region:
#if false
        var whiteRect = WidgetCanvas.Children.Cast<FrameworkElement>().FirstOrDefault(e => e.Name == "test_border");
        if (whiteRect == null)
        {
            whiteRect = new System.Windows.Shapes.Rectangle() { Name = "test_border", Width = 20000, Height = 20000, Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black) };
            WidgetCanvas.Children.Add(whiteRect);
            Canvas.SetLeft(whiteRect, -10000);
            Canvas.SetTop(whiteRect, -10000);
        }
#endif

        return;

on_error:

// All regions that were not sent without errors to SetWindowRgn must be destroyed manually
        DeleteObject(rgn);
        if (SetWindowRgn(windowHandle, IntPtr.Zero, true) == 0)
            Logger.Error("Taskbar Widget error during window region reset.");
    }

    private void UpdatePosition(bool force = false)
    {
        if (MainWindow.ExplorerRestarting)
        {
            // Explorer is restarting -- do NOTHING
            return;
        }

        // Widget is only displayed when enabled
        if (!SettingsManager.Current.TaskbarWidgetEnabled)
            return;

        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);
            RaiseWidgetAboveTaskbar();

            if (interop.Handle == IntPtr.Zero)
            {
                if (MainWindow.ExplorerRestarting)
                {
                    Logger.Info("Skipping TaskbarWindow recovery during Explorer restart");
                    return;
                }

                _timer.Stop();

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _mainWindow?.RecreateTaskbarWindow();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to signal MainWindow to recover Taskbar Widget window");
                    }
                }, DispatcherPriority.Background);

                return;
            }

            if (taskbarHandle != IntPtr.Zero && interop.Handle != IntPtr.Zero)
            {
                // Check shell geometry cheaply; expensive UI Automation and WPF
                // layout are only needed when geometry or visibility actually changes.
                if (!force)
                {
                    GetWindowRect(taskbarHandle, out RECT currentRect);
                    bool taskbarMoved = !_hasTaskbarRect
                        || Math.Abs(currentRect.Left - _lastTaskbarRect.Left) > 1
                        || Math.Abs(currentRect.Top - _lastTaskbarRect.Top) > 1
                        || Math.Abs(currentRect.Right - _lastTaskbarRect.Right) > 1
                        || Math.Abs(currentRect.Bottom - _lastTaskbarRect.Bottom) > 1;

                    _lastTaskbarRect = currentRect;
                    _hasTaskbarRect = true;

                    if (!_widgetExpanded && !_monitorArea.IsEmpty
                        && (!IsWindowVisible(taskbarHandle) || !_monitorArea.Contains(
                            new Point((currentRect.Left + currentRect.Right) / 2.0,
                                (currentRect.Top + currentRect.Bottom) / 2.0))))
                    {
                        if (Visibility == Visibility.Visible) CollapseWindowWithFade();
                        return;
                    }

                    if (taskbarMoved)
                    {
                        // Taskbar geometry changed - full reposition
                        force = true;
                    }
                    else
                    {
                        // The timer used to always re-run CalculateAndSetPosition, which re-showed
                        // the widget if something had hidden it. Now that redundant updates are
                        // skipped to avoid jittering, self-heal here: if the widget should be
                        // visible but its window is actually hidden, force a full reposition+show.
                        // The taskbar visibility check avoids poking auto-hide taskbars while they
                        // are retracted/hidden.
                        bool shouldBeVisible = _widgetExpanded || !SettingsManager.Current.TaskbarWidgetAutoHide
                            || _lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                        if (!shouldBeVisible || (!_widgetExpanded && !IsWindowVisible(taskbarHandle)) || IsWindowVisible(interop.Handle))
                            return;

                        force = true;
                    }
                }

                Dispatcher.BeginInvoke(() =>
                {
                    CalculateAndSetPosition(taskbarHandle, interop.Handle, isMainTaskbarSelected);
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during position update");
        }
    }

    private void CalculateAndSetPosition(IntPtr taskbarHandle, IntPtr taskbarWindowHandle, bool isMainTaskbarSelected)
    {
        // Prevent overlapping updates - if a previous update is still running
        // (e.g. waiting for an automation query timeout), skip this tick.
        if (_positionUpdateInProgress)
            return;
        _positionUpdateInProgress = true;

        try
        {
            // get DPI scaling
            double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;

            // Guard against invalid DPI (e.g. during explorer restart when handle is stale)
            if (dpiScale <= 0)
                return;

            // Get Taskbar dimensions
            RECT taskbarRect;

            if (!SettingsManager.Current.LegacyTaskbarWidthEnabled)
            {
                // first, try to find the Taskbar.TaskbarFrame element in the XAML
                // this should give us the actual bounds of the taskbar, excluding invisible margins on some Windows configurations
                (bool success, Rect result) = GetTaskbarFrameRect(taskbarHandle);
                if (success)
                {
                    taskbarRect = new RECT
                    {
                        Left = (int)result.Left,
                        Top = (int)result.Top,
                        Right = (int)result.Right,
                        Bottom = (int)result.Bottom
                    };
                }
                else
                {
                    // fallback to GetWindowRect if we fail to get the frame bounds for some reason
                    GetWindowRect(taskbarHandle, out taskbarRect);
                }
            }
            else
            {
                // legacy method - GetWindowRect on the entire taskbar, which includes invisible margins on some Windows configurations
                GetWindowRect(taskbarHandle, out taskbarRect);
            }

            // Once the user expands, Explorer auto-hiding its bar must not drag or
            // dismiss the detached surface. Keep the visible anchor until explicit
            // collapse, monitor selection, orientation or display-size changes.
            if (_widgetExpanded && !_taskbarScreenRect.IsEmpty
                && _lastSelectedMonitor == SettingsManager.Current.TaskbarWidgetSelectedMonitor
                && Math.Abs(dpiScale - _positionDpiScale) < 0.001
                && Math.Abs(taskbarRect.Right - taskbarRect.Left - _taskbarScreenRect.Width) < 2
                && Math.Abs(taskbarRect.Bottom - taskbarRect.Top - _taskbarScreenRect.Height) < 2)
            {
                taskbarRect = new RECT { Left = (int)_taskbarScreenRect.Left, Top = (int)_taskbarScreenRect.Top,
                    Right = (int)_taskbarScreenRect.Right, Bottom = (int)_taskbarScreenRect.Bottom };
            }

            int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
            int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

            // Vertical taskbar support: rotate and reposition widget when taskbar is taller than wide
            bool isVertical = taskbarHeight > taskbarWidth;
            _taskbarHandle = taskbarHandle;
            _positionDpiScale = dpiScale;
            var barScreenRect = new Rect(taskbarRect.Left, taskbarRect.Top, taskbarWidth, taskbarHeight);
            if (_monitorWorkArea.IsEmpty || barScreenRect != _taskbarScreenRect
                || DateTime.UtcNow - _monitorGeometryCheckedUtc > AutomationBoundsTtl)
            {
                var monitor = MonitorUtil.GetMonitor(taskbarHandle);
                _monitorWorkArea = monitor.workArea;
                _monitorArea = monitor.monitorArea;
                _monitorGeometryCheckedUtc = DateTime.UtcNow;
            }
            _taskbarScreenRect = barScreenRect;
            _widgetResizeWindowHandle = taskbarWindowHandle;
            // A new song-identity commit means a new width: morph to it in sync with the
            // text/background entrance. Every other path (timer ticks, setup, settings)
            // sees an unchanged version and applies instantly.
            int songVersion = Widget.SongCommitVersion;
            bool songChanged = songVersion != _lastSeenSongCommitVersion;
            _lastSeenSongCommitVersion = songVersion;

            var wRect = PositionWidget(taskbarHandle, taskbarRect, dpiScale, isMainTaskbarSelected, isVertical, songChanged);
            var vRect = _expansionOwnsLayout ? _visualizerRect
                : PositionVisualizer(taskbarHandle, taskbarRect, dpiScale, isMainTaskbarSelected, isVertical, songChanged);

            _compactWidgetRect = new Rect(_widgetTargetLeftDips * dpiScale, _widgetTargetTopDips * dpiScale,
                isVertical ? 40 * dpiScale : _widgetTargetWidthDips * dpiScale,
                isVertical ? _widgetTargetWidthDips * dpiScale : 40 * dpiScale);
            _visualizerRect = vRect;
            if (_expansionOwnsLayout)
            {
                // Reconcile monitor moves, taskbar auto-hide and settings while expanded.
                // Metadata alone must not restart the expansion clock.
                Rect target = _widgetExpanded ? ExpandedWidgetRect() : _compactWidgetRect;
                if (target != _expansionTargetRect)
                    MorphWidgetExpansion(target);
                ApplyWidgetHostBounds(_expansionAnimating ? _expansionFlightRect : LiveWidgetRect());
            }
            else
            {
                ApplyWidgetHostBounds(wRect);
                UpdateWindowRegion(taskbarWindowHandle, wRect, vRect);
            }

            _lastSelectedMonitor = SettingsManager.Current.TaskbarWidgetSelectedMonitor;
            _hasEverBeenPositioned = true;
            if (_hasPublishedMedia && Widget.Visibility == Visibility.Visible
                && (_widgetExpanded || !SettingsManager.Current.TaskbarWidgetAutoHide
                    || _lastPlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                && Visibility != Visibility.Visible)
                EnsureWindowVisible();
        }
        finally
        {
            _positionUpdateInProgress = false;
        }
    }

    private Rect PositionWidget(IntPtr taskbarHandle, RECT taskbarRect, double dpiScale, bool isMainTaskbarSelected, bool isVertical, bool animateResize)
    {
        if (!SettingsManager.Current.TaskbarWidgetEnabled)
            return Rect.Empty;

        // Calculate widget size
        var (logicalWidth, logicalHeight) = Widget.CalculateSize(dpiScale);

        int physicalWidth = (int)(logicalWidth * dpiScale * _scale);
        int physicalHeight = (int)(logicalHeight * dpiScale);

        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

        // Apply orientation transform only when it flips: LayoutTransform forces a
        // full measure/arrange of the whole subtree, and this runs per reposition tick.
        // A flip also forces the instant path below (no morph across a rotation).
        bool orientationJustFlipped = _lastWidgetIsVertical != isVertical;
        if (!_expansionOwnsLayout && _lastWidgetIsVertical != isVertical)
        {
            Widget.LayoutTransform = isVertical ? new System.Windows.Media.RotateTransform(90) : null;
            Widget.RenderTransform = System.Windows.Media.Transform.Identity;
            Widget.SetVerticalMode(isVertical);
            _lastWidgetIsVertical = isVertical;
        }

        // On a vertical taskbar the widget is rotated 90°, so the axes flip:
        //   primarySize = taskbarHeight, positioning runs along Y
        //   crossSize   = taskbarWidth,  widget is centered along X
        //   physicalWidth  = visual extent along primary axis (logical width = visual height after rotation)
        //   physicalHeight = visual extent along cross axis   (logical height = visual width after rotation)
        int primarySize = isVertical ? taskbarHeight : taskbarWidth;
        int crossSize = isVertical ? taskbarWidth : taskbarHeight;

        // Center on the cross axis; both orientations use physicalHeight for the cross dimension
        int crossPos = (crossSize - physicalHeight) / 2;

        // Primary axis position (calculated per-case below)
        int primaryPos = 0;

        switch (SettingsManager.Current.TaskbarWidgetPosition)
        {
            case 0: // near start (left for horizontal, top for vertical)
                primaryPos = 20;

                if (SettingsManager.Current.TaskbarVisualizerEnabled && SettingsManager.Current.TaskbarVisualizerPosition == 0)
                    primaryPos += (int)(TaskbarVisualizer.Width * dpiScale) + 4;

                if (!SettingsManager.Current.TaskbarWidgetPadding)
                    break;

                // automatic widget padding to the start
                try
                {
                    // find widget button in XAML
                    (bool found, Rect nativeWidgetRect) = GetTaskbarWidgetRect(taskbarHandle);

                    // Accept only if the native Widgets button is in the start half of the taskbar
                    bool inStartHalf = isVertical
                        ? nativeWidgetRect.Bottom < (taskbarRect.Top + taskbarRect.Bottom) / 2.0
                        : nativeWidgetRect.Right < (taskbarRect.Left + taskbarRect.Right) / 2.0;

                    if (found && inStartHalf)
                    {
                        // Convert absolute screen position to relative position within taskbar
                        primaryPos = isVertical
                            ? (int)(nativeWidgetRect.Bottom - taskbarRect.Top) + 2
                            : (int)(nativeWidgetRect.Right - taskbarRect.Left) + 2;
                    }
                }
                catch (Exception ex)
                {
                    // fallback to default padding
                    Logger.Warn(ex, "Failed to get Widgets button position.");
                    primaryPos += _nativeWidgetsPadding + 2;
                }
                break;

            case 1: // center of the taskbar
                primaryPos = (primarySize - physicalWidth) / 2;

                if (SettingsManager.Current.TaskbarVisualizerEnabled)
                    if (SettingsManager.Current.TaskbarVisualizerPosition == 0)
                        primaryPos += (int)(TaskbarVisualizer.Width * dpiScale) / 2 + 4;
                    else
                        primaryPos -= (int)(TaskbarVisualizer.Width * dpiScale) / 2 - 4;
                break;

            case 2: // near end (right for horizontal, bottom for vertical)
                try
                {
                    if (SettingsManager.Current.TaskbarVisualizerEnabled && SettingsManager.Current.TaskbarVisualizerPosition == 1)
                        primaryPos -= (int)(TaskbarVisualizer.Width * dpiScale) - 4;

                    // Horizontal only: try to position next to native Widgets button on the end side
                    if (!isVertical && SettingsManager.Current.TaskbarWidgetPadding)
                    {
                        try
                        {
                            // find widget button in XAML
                            (bool found, Rect nativeWidgetRect) = GetTaskbarWidgetRect(taskbarHandle);

                            // make sure it's on the right side, otherwise ignore (widget might be to the left)
                            if (found && nativeWidgetRect.Left > (taskbarRect.Left + taskbarRect.Right) / 2.0)
                            {
                                // Convert absolute screen position to relative position within taskbar
                                primaryPos += (int)(nativeWidgetRect.Left - taskbarRect.Left) - 1 - physicalWidth;
                                break;
                            }
                        }
                        catch (Exception ex) // catch exception when getting widget position
                        {
                            Logger.Warn(ex, "Failed to get Widgets button position.");
                        }
                    }

                    // try to position next to system tray
                    if (!isMainTaskbarSelected)
                    {
                        // find secondary tray with automation
                        (bool found, Rect trayRect) = GetSystemTrayRect(taskbarHandle);

                        if (found)
                        {
                            // Convert absolute screen position to relative position within taskbar
                            double trayOffset = isVertical
                                ? trayRect.Top - taskbarRect.Top
                                : trayRect.Left - taskbarRect.Left;
                            primaryPos += (int)trayOffset - physicalWidth - (isVertical ? 2 : 1);
                            break;
                        }
                    }
                    else
                    {
                        // Primary taskbar: for vertical, try automation first (more reliable on ExplorerPatcher)
                        if (isVertical)
                        {
                            (bool trayFound, Rect trayAutomationRect) = GetSystemTrayRect(taskbarHandle);
                            if (trayFound && trayAutomationRect.Top >= taskbarRect.Top)
                            {
                                primaryPos += (int)(trayAutomationRect.Top - taskbarRect.Top) - physicalWidth - 2;
                                break;
                            }
                        }

                        // Primary taskbar: TrayNotifyWnd (original approach for horizontal, fallback for vertical)
                        if (_trayHandle == IntPtr.Zero || _lastSelectedMonitor != SettingsManager.Current.TaskbarWidgetSelectedMonitor)
                            _trayHandle = FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);

                        if (_trayHandle != IntPtr.Zero)
                        {
                            GetWindowRect(_trayHandle, out RECT trayWndRect);
                            // Convert absolute screen position to relative position within taskbar
                            double trayOffset = isVertical
                                ? trayWndRect.Top - taskbarRect.Top
                                : trayWndRect.Left - taskbarRect.Left;

                            // For vertical: validate the tray is in the lower half of the taskbar
                            if (!isVertical || trayOffset > taskbarHeight / 2)
                            {
                                primaryPos += (int)trayOffset - physicalWidth - (isVertical ? 2 : 6); // trayOffset isn't 100% accurate, so we subtract a few pixels
                                break;
                            }
                        }
                        else if (!isVertical)
                        {
                            // TrayNotifyWnd not found on horizontal: fallback to right alignment,
                            // since we are aligning to the right side and know the size of the taskbar.
                            primaryPos += taskbarWidth - physicalWidth - 20;
                            break;
                        }
                    }

                    // Final fallback: place near the end of the taskbar
                    primaryPos += primarySize - physicalWidth - 20;
                }
                catch (Exception ex)
                {
                    // Fallback to left alignment
                    Logger.Warn(ex, "Failed to get System Tray position.");
                    primaryPos = isVertical ? primarySize - physicalWidth - 20 : 20;
                }
                break;
        }

        primaryPos += SettingsManager.Current.TaskbarWidgetManualPadding;

        double targetLeftDips = (isVertical ? crossPos : primaryPos) / dpiScale;
        double targetTopDips = (isVertical ? primaryPos : crossPos) / dpiScale;
        double targetWidthDips = physicalWidth / dpiScale;
        double targetHeightDips = physicalHeight / dpiScale;

        // The visualizer positions itself against these targets (it runs next, while
        // the widget may still be mid-flight holding stale base values).
        _widgetTargetLeftDips = targetLeftDips;
        _widgetTargetTopDips = targetTopDips;
        _widgetTargetWidthDips = targetWidthDips;

        // After 90° LayoutTransform the visual bounding rect has swapped dimensions
        double rectW = isVertical ? physicalHeight : physicalWidth;
        double rectH = isVertical ? physicalWidth : physicalHeight;
        var finalRect = new Rect(targetLeftDips * dpiScale, targetTopDips * dpiScale, rectW, rectH);

        // While expanded, retain compact targets for anchoring/visualizer placement;
        // only the expansion owns the widget's live width, height and position.
        if (_expansionOwnsLayout)
            return finalRect;

        // Live values (mid-flight reads return the animated value, so a retarget
        // continues from the partial width instead of snapping). NaN = never laid
        // out yet (first paint): treat as already at target.
        double curLeft = Canvas.GetLeft(Widget);
        if (double.IsNaN(curLeft)) curLeft = targetLeftDips;
        double curTop = Canvas.GetTop(Widget);
        if (double.IsNaN(curTop)) curTop = targetTopDips;
        double curWidth = Widget.Width;
        if (double.IsNaN(curWidth)) curWidth = targetWidthDips;

        bool sameTarget = _widgetResizeRunning
            && Math.Abs(_widgetResizeTargetWidth - targetWidthDips) <= 0.5
            && Math.Abs(_widgetResizeTargetLeft - targetLeftDips) <= 0.5
            && Math.Abs(_widgetResizeTargetTop - targetTopDips) <= 0.5;

        // A reposition tick while the morph is still heading at the same target
        // (timer, late cover event): leave the running clocks alone and keep the
        // union region so the flight is never clipped mid-way.
        if (sameTarget)
            return _widgetResizeUnionRect;

        bool canAnimate = animateResize
            && AreAnimationsEnabled
            && SettingsManager.Current.TaskbarWidgetResizeAnimated
            && Visibility == Visibility.Visible
            && !orientationJustFlipped
            && Math.Abs(targetWidthDips - curWidth) > 0.5;

        if (!canAnimate)
        {
            // Instant path (also the killer of a superseded morph heading elsewhere):
            // removed clocks never complete, so a stale completion can never park here.
            _widgetResizeVersion++;
            _widgetResizeRunning = false;
            Widget.BeginAnimation(Canvas.LeftProperty, null);
            Widget.BeginAnimation(Canvas.TopProperty, null);
            Widget.BeginAnimation(WidthProperty, null);
            Widget.ParkControlsFollow();
            // Set widget position within canvas
            // primaryPos → left (horizontal) or top (vertical); crossPos → top (horizontal) or left (vertical)
            Canvas.SetLeft(Widget, targetLeftDips);
            Canvas.SetTop(Widget, targetTopDips);
            Widget.Width = targetWidthDips;
            Widget.Height = targetHeightDips;
            _widgetResizeTargetWidth = targetWidthDips;
            _widgetResizeTargetLeft = targetLeftDips;
            _widgetResizeTargetTop = targetTopDips;
            return finalRect;
        }

        // Animated morph: same duration/easing as the song-change text entrance started
        // in the same commit, so letters, background disc and outer width land together.
        // Left/Top ride along so the position setting stays the anchor (left grows
        // rightward, center grows both ways, right grows leftward).
        _widgetResizeVersion++;
        int version = _widgetResizeVersion;
        _widgetResizeRunning = true;
        _widgetResizeTargetWidth = targetWidthDips;
        _widgetResizeTargetLeft = targetLeftDips;
        _widgetResizeTargetTop = targetTopDips;
        _widgetResizeFinalRect = finalRect;

        // The window region must cover the whole flight (union of start + final):
        // parking it at the final size upfront would clip a shrinking widget and hide
        // the morph. It shrinks back to the final rect when the Width clock completes.
        var startRect = new Rect(curLeft * dpiScale, curTop * dpiScale,
            isVertical ? physicalHeight : curWidth * dpiScale,
            isVertical ? curWidth * dpiScale : physicalHeight);
        startRect.Union(finalRect);
        _widgetResizeUnionRect = startRect;

        int msDuration = FluentFlyout.Controls.TaskbarWidget.TaskbarWidgetAnimationEnvironment.GetDurationMs();
        var easing = GetEasing(true);

        // Rebase in one UI block (drop old clocks, hold the live values as base), then
        // animate: no flash, no snap, seamless mid-flight retargets.
        Widget.BeginAnimation(Canvas.LeftProperty, null);
        Widget.BeginAnimation(Canvas.TopProperty, null);
        Widget.BeginAnimation(WidthProperty, null);
        Canvas.SetLeft(Widget, curLeft);
        Canvas.SetTop(Widget, curTop);
        Widget.Width = curWidth;
        Widget.Height = targetHeightDips;

        DoubleAnimation widthAnimation = new()
        {
            From = curWidth,
            To = targetWidthDips,
            Duration = TimeSpan.FromMilliseconds(msDuration),
            EasingFunction = easing
        };
        widthAnimation.Completed += (s, e) =>
        {
            if (version != _widgetResizeVersion)
                return;
            _widgetResizeRunning = false;
            // Exact settle: park widget + visualizer (which shares this version) and
            // shrink the region from the flight union down to the final rects.
            Widget.BeginAnimation(Canvas.LeftProperty, null);
            Widget.BeginAnimation(Canvas.TopProperty, null);
            Widget.BeginAnimation(WidthProperty, null);
            Canvas.SetLeft(Widget, _widgetResizeTargetLeft);
            Canvas.SetTop(Widget, _widgetResizeTargetTop);
            Widget.Width = _widgetResizeTargetWidth;
            TaskbarVisualizer.BeginAnimation(Canvas.LeftProperty, null);
            TaskbarVisualizer.BeginAnimation(Canvas.TopProperty, null);
            if (!double.IsNaN(_vizResizeTargetLeft))
                Canvas.SetLeft(TaskbarVisualizer, _vizResizeTargetLeft);
            if (!double.IsNaN(_vizResizeTargetTop))
                Canvas.SetTop(TaskbarVisualizer, _vizResizeTargetTop);
            Widget.ParkControlsFollow();
            try
            {
                if (_widgetResizeWindowHandle != IntPtr.Zero)
                    UpdateWindowRegion(_widgetResizeWindowHandle, _widgetResizeFinalRect, _vizResizeFinalRect);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Taskbar Widget error settling resize region");
            }
        };
        Widget.BeginAnimation(WidthProperty, widthAnimation);

        if (Math.Abs(targetLeftDips - curLeft) > 0.5)
        {
            Widget.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
            {
                From = curLeft,
                To = targetLeftDips,
                Duration = TimeSpan.FromMilliseconds(msDuration),
                EasingFunction = easing
            });
        }
        if (Math.Abs(targetTopDips - curTop) > 0.5)
        {
            Widget.BeginAnimation(Canvas.TopProperty, new DoubleAnimation
            {
                From = curTop,
                To = targetTopDips,
                Duration = TimeSpan.FromMilliseconds(msDuration),
                EasingFunction = easing
            });
        }

        // Interior follow: the text containers already snapped to the final width, so a
        // controls block sitting after the text would teleport to its final X while the
        // outer edge is still morphing. The control rides it (cur - target) -> 0 with the
        // same duration/easing instance, tracking W(t) - W_new exactly for every anchor.
        Widget.AnimateControlsFollow(curWidth - targetWidthDips, msDuration, easing);

        return _widgetResizeUnionRect;
    }

    private Rect PositionVisualizer(IntPtr taskbarHandle, RECT taskbarRect, double dpiScale, bool isMainTaskbarSelected, bool isVertical, bool animateResize)
    {
        if (!SettingsManager.Current.TaskbarVisualizerEnabled)
        {
            _vizResizeFinalRect = Rect.Empty;
            _vizResizeUnionRect = Rect.Empty;
            return Rect.Empty;
        }

        // Rotate visualizer 90° on vertical taskbar so it fits the slim width.
        // Guarded like the widget transform above: LayoutTransform is a full layout pass.
        bool vizOrientationJustFlipped = _lastVisualizerIsVertical != isVertical;
        if (_lastVisualizerIsVertical != isVertical)
        {
            TaskbarVisualizer.LayoutTransform = isVertical ? new System.Windows.Media.RotateTransform(90) : null;
            _lastVisualizerIsVertical = isVertical;
        }

        int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        int taskbarWidth = taskbarRect.Right - taskbarRect.Left;

        // TaskbarVisualizer.Height (40) is the cross-axis extent for both orientations:
        //   horizontal: actual height = 40, centered vertically (-1 to match native element alignment)
        //   vertical:   visual width after rotation = 40, centered horizontally
        int crossSize = isVertical ? taskbarWidth : taskbarHeight;
        int crossOffset = isVertical ? 0 : -1; // -1 aligns with native taskbar elements on horizontal
        int crossPos = (crossSize - (int)(TaskbarVisualizer.Height * dpiScale)) / 2 + crossOffset;

        // TaskbarVisualizer.Width (84) is the primary-axis extent for both orientations:
        //   horizontal: actual width = 84
        //   vertical:   visual height after rotation = 84
        // Position adjacent to the widget along the primary axis. Uses the widget's
        // just-computed targets (not its live Canvas values, which may be mid-flight).
        double widgetPrimaryStart = isVertical ? _widgetTargetTopDips : _widgetTargetLeftDips;
        double widgetWidthDips = _widgetTargetWidthDips;
        int primaryPos;

        switch (SettingsManager.Current.TaskbarVisualizerPosition)
        {
            case 0: // before widget (left for horizontal, above for vertical)
                primaryPos = (int)(widgetPrimaryStart * dpiScale) - (int)(TaskbarVisualizer.Width * dpiScale);
                break;

            case 1: // after widget (right for horizontal, below for vertical)
                // Widget.Width holds the logical width; after 90° rotation its visual height = Widget.Width * dpiScale
                primaryPos = (int)(widgetPrimaryStart * dpiScale) + (int)(widgetWidthDips * dpiScale);
                break;

            default:
                primaryPos = 0;
                break;
        }

        double targetVizLeftDips = (isVertical ? crossPos : primaryPos) / dpiScale;
        double targetVizTopDips = (isVertical ? primaryPos : crossPos) / dpiScale;

        // After 90° LayoutTransform the visual bounding rect has swapped dimensions
        double rectW = isVertical ? TaskbarVisualizer.Height * dpiScale : TaskbarVisualizer.Width * dpiScale;
        double rectH = isVertical ? TaskbarVisualizer.Width * dpiScale : TaskbarVisualizer.Height * dpiScale;
        var finalVizRect = new Rect(targetVizLeftDips * dpiScale, targetVizTopDips * dpiScale, rectW, rectH);
        _vizResizeFinalRect = finalVizRect;

        double curVizLeft = Canvas.GetLeft(TaskbarVisualizer);
        if (double.IsNaN(curVizLeft)) curVizLeft = targetVizLeftDips;
        double curVizTop = Canvas.GetTop(TaskbarVisualizer);
        if (double.IsNaN(curVizTop)) curVizTop = targetVizTopDips;

        // Same-target tick while the widget morph flies: don't touch anything, keep
        // the union region. (Shares the widget's version; parked by its completion.)
        bool vizSameTarget = _widgetResizeRunning
            && Math.Abs(_vizResizeTargetLeft - targetVizLeftDips) <= 0.5
            && Math.Abs(_vizResizeTargetTop - targetVizTopDips) <= 0.5;
        if (vizSameTarget)
            return _vizResizeUnionRect;

        // The visualizer only ever rides along a running widget morph (its position is
        // a pure function of the widget's): any other pass applies it instantly.
        bool vizCanAnimate = animateResize
            && _widgetResizeRunning
            && AreAnimationsEnabled
            && Visibility == Visibility.Visible
            && !vizOrientationJustFlipped
            && (Math.Abs(targetVizLeftDips - curVizLeft) > 0.5 || Math.Abs(targetVizTopDips - curVizTop) > 0.5);

        if (!vizCanAnimate)
        {
            TaskbarVisualizer.BeginAnimation(Canvas.LeftProperty, null);
            TaskbarVisualizer.BeginAnimation(Canvas.TopProperty, null);
            // Set visualizer position within canvas
            // primaryPos → left (horizontal) or top (vertical); crossPos → top (horizontal) or left (vertical)
            Canvas.SetLeft(TaskbarVisualizer, targetVizLeftDips);
            Canvas.SetTop(TaskbarVisualizer, targetVizTopDips);
            _vizResizeTargetLeft = targetVizLeftDips;
            _vizResizeTargetTop = targetVizTopDips;
            // A static visualizer's flight union IS its final rect. It must be stored:
            // a same-target tick during a widget morph returns the stored union, and
            // the never-computed default (0,0,0,0) clipped the visualizer out of the
            // window region for the whole morph on every song change.
            _vizResizeUnionRect = finalVizRect;
            return finalVizRect;
        }

        _vizResizeTargetLeft = targetVizLeftDips;
        _vizResizeTargetTop = targetVizTopDips;
        var startVizRect = new Rect(curVizLeft * dpiScale, curVizTop * dpiScale, rectW, rectH);
        startVizRect.Union(finalVizRect);
        _vizResizeUnionRect = startVizRect;

        int vizMs = FluentFlyout.Controls.TaskbarWidget.TaskbarWidgetAnimationEnvironment.GetDurationMs();
        var vizEasing = GetEasing(true);
        TaskbarVisualizer.BeginAnimation(Canvas.LeftProperty, null);
        TaskbarVisualizer.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetLeft(TaskbarVisualizer, curVizLeft);
        Canvas.SetTop(TaskbarVisualizer, curVizTop);
        if (Math.Abs(targetVizLeftDips - curVizLeft) > 0.5)
        {
            TaskbarVisualizer.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
            {
                From = curVizLeft,
                To = targetVizLeftDips,
                Duration = TimeSpan.FromMilliseconds(vizMs),
                EasingFunction = vizEasing
            });
        }
        if (Math.Abs(targetVizTopDips - curVizTop) > 0.5)
        {
            TaskbarVisualizer.BeginAnimation(Canvas.TopProperty, new DoubleAnimation
            {
                From = curVizTop,
                To = targetVizTopDips,
                Duration = TimeSpan.FromMilliseconds(vizMs),
                EasingFunction = vizEasing
            });
        }

        return _vizResizeUnionRect;
    }

    public void UpdateUi(string title, string artist, BitmapImage? icon, GlobalSystemMediaTransportControlsSessionPlaybackStatus? playbackStatus, GlobalSystemMediaTransportControlsSessionPlaybackControls? playbackControls = null)
    {
        if (!SettingsManager.Current.TaskbarWidgetEnabled)
        {
            if (_timer.IsEnabled) // pause timer to save resources
                _timer.Stop();

            Dispatcher.Invoke(CollapseWindowWithFade);
            return;
        }

        // Belt and braces: if Loaded wiring hasn't run yet, wire it now so controls work.
        if (_mainWindow == null && Application.Current?.MainWindow is MainWindow mw)
        {
            _mainWindow = mw;
            Widget.SetMainWindow(mw);
        }

        // First real song ever: from here on the window is allowed to be visible.
        // Pure stop/idle events before that keep it hidden (no startup placeholder flash).
        if (title != "-" || artist != "-")
            _hasPublishedMedia = true;

        // Autohide - Widget hides when playback is paused
        _lastPlaybackStatus = playbackStatus;

        if ((SettingsManager.Current.TaskbarWidgetAutoHide))
        {
            if (playbackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing || _widgetExpanded)
            {
                _autoHideTimer?.Stop();
                _autoHideTimer = null;
                // Showing is handled by the single gated block below (after positioning),
                // so resume never flashes an unpositioned window.
            }
            else
            {
                StartAutoHideTimer();
            }
        }

        if (!_timer.IsEnabled)
            _timer.Start();

        // Delegate UI update to widget control
        Widget.UpdateUi(title, artist, icon, playbackStatus, playbackControls);

        // Single queued block per metadata event (same Background priority = FIFO):
        // position first, then show only once positioned — no first-frame-in-the-
        // corner. Previously two separate BeginInvokes per event (two queue hops
        // plus two layout passes per event in a song-change burst).
        Dispatcher.BeginInvoke(() =>
        {
            UpdatePosition(true);

            if (!_hasPublishedMedia || !_hasEverBeenPositioned)
                return;

            // When autohide is on and playback is paused, the delayed-hide timer owns the
            // window's visibility; force-showing here would cancel its fade-out.
            if (SettingsManager.Current.TaskbarWidgetAutoHide &&
                _lastPlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing && !_widgetExpanded)
                return;

            EnsureWindowVisible();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Keeps paused media visible while expanded and restores delayed hiding on close.
    /// </summary>
    private void StartAutoHideTimer()
    {
        if (_autoHideTimer != null) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_autoHideTimer == timer) _autoHideTimer = null;
            if (!_widgetExpanded && _lastPlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                CollapseWindowWithFade();
        };
        _autoHideTimer = timer;
        timer.Start();
    }

    /// <summary>Uses the same animation settings as the widget content.</summary>
    private bool AreAnimationsEnabled =>
        FluentFlyout.Controls.TaskbarWidget.TaskbarWidgetAnimationEnvironment.AreAnimationsEnabled;

    /// <summary>
    /// Returns the user's chosen easing function, or <see langword="null"/> for linear
    /// when "linear" is selected, mirroring the main flyout's behaviour.
    /// </summary>
    private EasingFunctionBase? GetEasing(bool easeOut) =>
        FluentFlyout.Controls.TaskbarWidget.TaskbarWidgetAnimationEnvironment.GetEasing(_mainWindow, easeOut);

    /// <summary>
    /// Ensures the widget window is visible, fading it in when it was hidden (autohide,
    /// restart) and cancelling any fade-out that is still in progress.
    /// </summary>
    private void EnsureWindowVisible()
    {
        ++_visibilityVersion;
        _windowFadingOut = false;
        IsHitTestVisible = true;
        BeginAnimation(OpacityProperty, null);

        if (!AreAnimationsEnabled)
        {
            Visibility = Visibility.Visible;
            Opacity = 1;
            return;
        }

        bool wasHidden = Visibility != Visibility.Visible;
        Visibility = Visibility.Visible;

        if (wasHidden)
        {
            Opacity = 0;
            DoubleAnimation fadeInAnimation = new()
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(FluentFlyout.Controls.TaskbarWidget.TaskbarWidgetAnimationEnvironment.GetDurationMs()),
                EasingFunction = GetEasing(true)
            };
            BeginAnimation(OpacityProperty, fadeInAnimation);
        }
        else
        {
            // A fade-out may have just been cancelled; restore full opacity immediately.
            Opacity = 1;
        }
        RaiseWidgetAboveTaskbar();
    }

    private void RaiseWidgetAboveTaskbar()
    {
        if (_closed || _windowFadingOut || !SettingsManager.Current.TaskbarWidgetEnabled
            || Visibility != Visibility.Visible) return;
        // Explorer can move its own topmost window ahead of us without changing
        // geometry. Restore only Z order: no native move, resize, or canvas reflow.
        WindowHelper.SetTopmost(this);
    }

    private void InstallShellZOrderHooks()
    {
        if (_shellZOrderProc != null) return;
        _shellZOrderProc = (_, eventType, hwnd, _, _, _, _) =>
        {
            if (_closed || hwnd != _taskbarHandle || eventType == 0x8003 // EVENT_OBJECT_HIDE
                || _topmostRefreshPending || Dispatcher.HasShutdownStarted) return;
            _topmostRefreshPending = true;
            Dispatcher.BeginInvoke(() =>
            {
                _topmostRefreshPending = false;
                RaiseWidgetAboveTaskbar();
            }, DispatcherPriority.Render);
        };
        // Observe only shell show/reorder and foreground changes, outside its
        // process. Skip our own events so raising this HWND cannot loop the hook.
        const uint skipOwnProcess = 0x0002;
        _shellZOrderHook = SetWinEventHook(0x8002, 0x8004, IntPtr.Zero,
            _shellZOrderProc, 0, 0, WINEVENT_OUTOFCONTEXT | skipOwnProcess);
        _shellForegroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _shellZOrderProc, 0, 0, WINEVENT_OUTOFCONTEXT | skipOwnProcess);
        if (_shellZOrderHook == IntPtr.Zero || _shellForegroundHook == IntPtr.Zero)
            Logger.Warn("Taskbar Z-order hook unavailable; native visibility polling remains active.");
    }

    /// <summary>
    /// Hides the widget window, fading it out first when animations are enabled.
    /// </summary>
    private void CollapseWindowWithFade()
    {
        if (_widgetExpanded && SettingsManager.Current.TaskbarWidgetEnabled) return;
        if (_windowFadingOut) return;
        int version = ++_visibilityVersion;
        IsHitTestVisible = false;
        CloseWidgetExpansion(animate: false);
        if (Visibility != Visibility.Visible)
            return;

        if (!AreAnimationsEnabled)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        DoubleAnimation fadeOutAnimation = new()
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(FluentFlyout.Controls.TaskbarWidget.TaskbarWidgetAnimationEnvironment.GetDurationMs()),
            EasingFunction = GetEasing(false)
        };
        fadeOutAnimation.Completed += (s, e) =>
        {
            if (version != _visibilityVersion || _widgetExpanded) return;
            _windowFadingOut = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            Visibility = Visibility.Collapsed;
        };
        _windowFadingOut = true;
        BeginAnimation(OpacityProperty, fadeOutAnimation);
    }

    public void RefreshExpansionPreference()
    {
        Widget.ApplyExpansionPreference();
        if (!SettingsManager.Current.TaskbarWidgetExpandOnClick)
            CloseWidgetExpansion();
    }

    public void ToggleWidgetExpansion()
    {
        if (_widgetExpanded) { CloseWidgetExpansion(); return; }
        if (!SettingsManager.Current.TaskbarWidgetEnabled || !SettingsManager.Current.TaskbarWidgetExpandOnClick
            || !Widget.HasPublishedSong
            || Visibility != Visibility.Visible || _compactWidgetRect.IsEmpty) return;

        _widgetExpanded = true;
        EnsureWindowVisible();
        _expandedAnchorRect = _compactWidgetRect;
        _autoHideTimer?.Stop();
        _autoHideTimer = null;
        if (!_expansionOwnsLayout)
        {
            // Hold the current rendered rectangle before dropping compact resize clocks.
            Rect live = LiveWidgetRect();
            bool vertical = _lastWidgetIsVertical == true;
            if (vertical)
                live = new Rect(Canvas.GetLeft(Widget) * _positionDpiScale,
                    Canvas.GetTop(Widget) * _positionDpiScale,
                    Widget.Height * _positionDpiScale, Widget.Width * _positionDpiScale);
            _widgetResizeVersion++;
            _widgetResizeRunning = false;
            ParkExpansionGeometry(live);
            Widget.ParkControlsFollow();
            TaskbarVisualizer.BeginAnimation(Canvas.LeftProperty, null);
            TaskbarVisualizer.BeginAnimation(Canvas.TopProperty, null);
            _expansionOwnsLayout = true;
        }
        Widget.SetExpandedState(true);
        _expansionOutsideHook ??= new MouseClickOutsideHook(ClickInsideWidget, () => CloseWidgetExpansion(), Dispatcher);
        if (!_expansionOutsideHook.Install())
            Logger.Warn("Widget outside-click hook unavailable; click the song again to collapse.");
        MorphWidgetExpansion(ExpandedWidgetRect());
    }

    public void CloseWidgetExpansion(bool animate = true)
    {
        if (!_expansionOwnsLayout) return;
        _widgetExpanded = false;
        _expansionOutsideHook?.Dispose();
        _expansionOutsideHook = null;
        Widget.SetExpandedState(false);
        MorphWidgetExpansion(_compactWidgetRect, animate);
    }

    private Rect ExpandedWidgetRect()
    {
        Rect widgetScreen = _expandedAnchorRect;
        widgetScreen.Offset(_taskbarScreenRect.Left, _taskbarScreenRect.Top);
        var box = TaskbarWidgetExpansion.Expand(ToBox(widgetScreen), ToBox(_taskbarScreenRect), ToBox(_monitorWorkArea),
            340 * _positionDpiScale,
            156 * _positionDpiScale);
        return new Rect(box.Left - _taskbarScreenRect.Left, box.Top - _taskbarScreenRect.Top, box.Width, box.Height);
    }

    private static TaskbarWidgetExpansion.Box ToBox(Rect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);

    private Rect LiveWidgetRect() => new(Canvas.GetLeft(Widget) * _positionDpiScale,
        Canvas.GetTop(Widget) * _positionDpiScale, Widget.Width * _positionDpiScale, Widget.Height * _positionDpiScale);

    private void ParkExpansionGeometry(Rect rect)
    {
        Widget.BeginAnimation(Canvas.LeftProperty, null);
        Widget.BeginAnimation(Canvas.TopProperty, null);
        Widget.BeginAnimation(WidthProperty, null);
        Widget.BeginAnimation(HeightProperty, null);
        Widget.LayoutTransform = null;
        Widget.SetVerticalMode(false);
        Canvas.SetLeft(Widget, rect.Left / _positionDpiScale);
        Canvas.SetTop(Widget, rect.Top / _positionDpiScale);
        Widget.Width = rect.Width / _positionDpiScale;
        Widget.Height = rect.Height / _positionDpiScale;
    }

    private void MorphWidgetExpansion(Rect target, bool animate = true)
    {
        Rect from = LiveWidgetRect();
        double fromProgress = Widget.ExpansionProgress;
        Widget.BeginAnimation(FluentFlyout.Controls.TaskbarWidgetControl.ExpansionProgressProperty, null);
        Widget.ExpansionProgress = fromProgress;
        ParkExpansionGeometry(from);
        int version = ++_expansionVersion;
        _expansionTargetRect = target;
        _expansionFlightRect = from;
        _expansionFlightRect.Union(target);
        _expansionAnimating = animate && AreAnimationsEnabled && from != target;
        ApplyWidgetHostBounds(_expansionFlightRect);
        if (!_expansionAnimating)
        {
            ParkExpansionGeometry(target);
            FinishWidgetExpansion(version);
            return;
        }
        // Use the Island's actual underdamped spring, sampled into one shared
        // progress curve so size and position keep the same edge throughout.
        double durationMs = FluentFlyout.Controls.TaskbarWidget.TaskbarWidgetAnimationEnvironment.GetDurationMs();
        var spring = IslandPhysics.Coefficients(durationMs, false);
        double timeMs = Math.Max(450, durationMs * 2.5);
        DoubleAnimationUsingKeyFrames Animation(double a, double b, bool size = false)
        {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(timeMs) };
            double progress = 0, velocity = 0;
            const double dt = 1.0 / 240;
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(a, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            for (double t = dt; t < timeMs / 1000; t += dt)
            {
                IslandPhysics.Step(ref progress, ref velocity, 1, spring.KP, spring.CP, dt);
                double value = IslandPhysics.Lerp(a, b, IslandPhysics.BounceCurve(progress));
                if (size) value = Math.Max(Math.Min(a, b) * (1 - IslandPhysics.BounceCompress), value);
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
            }
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(b, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(timeMs))));
            return animation;
        }
        // Reserve overshoot as well as the start/end, otherwise the spring is clipped.
        _expansionFlightRect.Inflate(Math.Abs(target.Width - from.Width) * 0.06 + 2,
            Math.Abs(target.Height - from.Height) * 0.06 + 2);
        ApplyWidgetHostBounds(_expansionFlightRect);
        var height = Animation(from.Height / _positionDpiScale, target.Height / _positionDpiScale, true);
        height.Completed += (_, _) => FinishWidgetExpansion(version);
        Widget.BeginAnimation(WidthProperty, Animation(from.Width / _positionDpiScale, target.Width / _positionDpiScale, true));
        Widget.BeginAnimation(Canvas.LeftProperty, Animation(from.Left / _positionDpiScale, target.Left / _positionDpiScale));
        Widget.BeginAnimation(Canvas.TopProperty, Animation(from.Top / _positionDpiScale, target.Top / _positionDpiScale));
        Widget.BeginAnimation(FluentFlyout.Controls.TaskbarWidgetControl.ExpansionProgressProperty,
            Animation(fromProgress, _widgetExpanded ? 1 : 0));
        Widget.BeginAnimation(HeightProperty, height);
    }

    private void FinishWidgetExpansion(int version)
    {
        if (version != _expansionVersion) return;
        _expansionAnimating = false;
        ParkExpansionGeometry(_expansionTargetRect);
        Widget.BeginAnimation(FluentFlyout.Controls.TaskbarWidgetControl.ExpansionProgressProperty, null);
        Widget.ExpansionProgress = _widgetExpanded ? 1 : 0;
        Widget.CompleteExpansionTransition();
        Widget.UpdateLayout();
        ApplyWidgetHostBounds(_expansionTargetRect);
        if (_widgetExpanded) return;
        _expansionOwnsLayout = false;
        _lastWidgetIsVertical = null;
        var hwnd = new WindowInteropHelper(this).Handle;
        CalculateAndSetPosition(_taskbarHandle, hwnd, GetSelectedTaskbarHandle(out bool main) == _taskbarHandle && main);
        if (SettingsManager.Current.TaskbarWidgetAutoHide
            && _lastPlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            StartAutoHideTimer();
    }

    private void ApplyWidgetHostBounds(Rect widgetRegion)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Reserve one fixed band for both layouts and spring overshoot. Only the
        // visible region changes; DWM never presents new native bounds with an old
        // canvas transform, which produced the down-left flash during metadata ticks.
        Rect host = _monitorArea;
        if (host.IsEmpty) return;
        switch (TaskbarWidgetExpansion.EdgeOf(ToBox(_taskbarScreenRect), ToBox(_monitorWorkArea)))
        {
            case TaskbarWidgetExpansion.Edge.Bottom:
                double top = Math.Max(host.Top, _taskbarScreenRect.Top - 180 * _positionDpiScale);
                host = new Rect(host.Left, top, host.Width, host.Bottom - top);
                break;
            case TaskbarWidgetExpansion.Edge.Top:
                host.Height = Math.Min(host.Height, _taskbarScreenRect.Bottom + 180 * _positionDpiScale - host.Top);
                break;
            case TaskbarWidgetExpansion.Edge.Left:
                host.Width = Math.Min(host.Width, _taskbarScreenRect.Right + 380 * _positionDpiScale - host.Left);
                break;
            case TaskbarWidgetExpansion.Edge.Right:
                double left = Math.Max(host.Left, _taskbarScreenRect.Left - 380 * _positionDpiScale);
                host = new Rect(left, host.Top, host.Right - left, host.Height);
                break;
        }
        _canvasOffsetPhysical = new Vector(_taskbarScreenRect.Left - host.Left,
            _taskbarScreenRect.Top - host.Top);
        if (WidgetCanvas.RenderTransform is not TranslateTransform offset)
            WidgetCanvas.RenderTransform = offset = new TranslateTransform();
        offset.X = _canvasOffsetPhysical.X / _positionDpiScale;
        offset.Y = _canvasOffsetPhysical.Y / _positionDpiScale;
        UpdateWindowRegion(hwnd, widgetRegion, _visualizerRect);
        if (host == _nativeHostScreenRect) return;
        _nativeHostScreenRect = host;
        SetWindowPos(hwnd, HWND_TOPMOST, (int)host.Left, (int)host.Top,
            (int)host.Width, (int)host.Height, SWP_NOACTIVATE);
    }

    private bool ClickInsideWidget(int x, int y)
    {
        if (!_widgetExpanded) return true;
        Rect rect = LiveWidgetRect();
        rect.Offset(_taskbarScreenRect.Left, _taskbarScreenRect.Top);
        return rect.Contains(x, y);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (_shellZOrderHook != IntPtr.Zero) UnhookWinEvent(_shellZOrderHook);
        if (_shellForegroundHook != IntPtr.Zero) UnhookWinEvent(_shellForegroundHook);
        _shellZOrderHook = _shellForegroundHook = IntPtr.Zero;
        _shellZOrderProc = null;
        _timer.Stop();
        _autoHideTimer?.Stop();
        _expansionOutsideHook?.Dispose();
        ++_expansionVersion;
        _widgetExpanded = _expansionOwnsLayout = false;
        base.OnClosed(e);
    }

    private (bool, Rect) GetTaskbarXamlElementRect(IntPtr taskbarHandle, ref AutomationElement? elementCache, string elementName)
    {
        if (taskbarHandle == IntPtr.Zero)
            return (false, Rect.Empty);

        try
        {
            // reset if monitor changed
            if (_lastSelectedMonitor != SettingsManager.Current.TaskbarWidgetSelectedMonitor)
            {
                elementCache = null;
                _automationBoundsCache.Remove(elementName);
                _automationBoundsCache.Clear();
            }

            // Fresh bounds: serve from cache, no COM round-trip on the UI thread.
            if (elementCache != null
                && _automationBoundsCache.TryGetValue(elementName, out var cached)
                && DateTime.UtcNow - cached.utc < AutomationBoundsTtl
                && cached.rect != Rect.Empty)
            {
                return (true, cached.rect);
            }

            // find widget in XAML
            if (elementCache == null)
            {
                if (_pendingAutomationTasks.TryGetValue(elementName, out var pendingTask) && !pendingTask.IsCompleted)
                    return (false, Rect.Empty);

                AutomationElement? found = null;
                var findTask = Task.Run(() =>
                {
                    var root = AutomationElement.FromHandle(taskbarHandle);
                    found = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, elementName));
                });
                _pendingAutomationTasks[elementName] = findTask;

                if (!findTask.Wait(1000))
                {
                    Logger.Warn("Timeout querying taskbar XAML element: " + elementName);
                    return (false, Rect.Empty);
                }

                // Propagate any exception from the background thread
                findTask.GetAwaiter().GetResult();
                elementCache = found;
            }

            if (elementCache == null) // widget most likely disabled
                return (false, Rect.Empty);

            try
            {
                if (_pendingAutomationTasks.TryGetValue(elementName, out var pendingTask) && !pendingTask.IsCompleted)
                {
                    elementCache = null;
                _automationBoundsCache.Remove(elementName);
                    return (false, Rect.Empty);
                }

                var cachedElement = elementCache;
                var boundsTask = Task.Run(() => cachedElement.Current.BoundingRectangle);
                _pendingAutomationTasks[elementName] = boundsTask;

                if (!boundsTask.Wait(500))
                {
                    Logger.Warn("Timeout getting bounds for taskbar XAML element: " + elementName);
                    elementCache = null;
                _automationBoundsCache.Remove(elementName);
                    return (false, Rect.Empty);
                }

                Rect elementRect = boundsTask.GetAwaiter().GetResult();

                if (elementRect == Rect.Empty) // widget shown before but most likely disabled now
                {
                    elementCache = null;
                _automationBoundsCache.Remove(elementName); // reset cache
                    _automationBoundsCache.Remove(elementName);
                    return (false, Rect.Empty);
                }

                _automationBoundsCache[elementName] = (elementRect, DateTime.UtcNow);
                return (true, elementRect);
            }
            catch (ElementNotAvailableException)
            {
                // element became stale, reset cache
                Logger.Warn("Taskbar XAML element became stale, resetting cache: " + elementName);
                elementCache = null;
                _automationBoundsCache.Remove(elementName);
                return (false, Rect.Empty);
            }
        }
        catch (COMException ex)
        {
            Logger.Warn(ex, "COM error retrieving taskbar XAML element Rect: " + elementName);
            elementCache = null; // reset cache on error
            return (false, Rect.Empty);
        }
        catch (ElementNotAvailableException)
        {
            Logger.Warn("Taskbar XAML element not available, resetting cache: " + elementName);
            elementCache = null;
            return (false, Rect.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error retrieving taskbar XAML element Rect: " + elementName);
            elementCache = null; // reset cache on error
            return (false, Rect.Empty);
        }
    }

    /// <summary>
    /// Attempts to locate the Windows taskbar widgets button and retrieves its bounding rectangle.
    /// </summary>
    /// <returns>A tuple where the first value indicates whether the widgets button was found (<see langword="true"/> if found;
    /// otherwise, <see langword="false"/>), and the second value is the bounding rectangle of the button if found, or
    /// <see cref="Rect.Empty"/> if not found.</returns>
    private (bool, Rect) GetTaskbarWidgetRect(IntPtr taskbarHandle)
    {
        return GetTaskbarXamlElementRect(taskbarHandle, ref _widgetElement, "WidgetsButton");
    }

    private (bool, Rect) GetSystemTrayRect(IntPtr taskbarHandle)
    {
        return GetTaskbarXamlElementRect(taskbarHandle, ref _trayElement, "SystemTrayIcon");
    }

    private (bool, Rect) GetTaskbarFrameRect(IntPtr taskbarHandle)
    {
        return GetTaskbarXamlElementRect(taskbarHandle, ref _taskbarFrameElement, "TaskbarFrame");
    }
}
