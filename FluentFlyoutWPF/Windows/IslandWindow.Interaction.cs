using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using System.Windows.Interop;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FluentFlyoutWPF.Windows;

public partial class IslandWindow
{
    private long _profilePasses, _profileFrames, _profileMeasures;
    private double _profileMaxReconcileMs;
    private void RecordIslandPerformance()
    {
        if (!SettingsManager.Current.IslandEnabled)
        {
            _profilePasses = _profileFrames = _profileMeasures = 0;
            _profileMaxReconcileMs = 0;
            return;
        }
        SessionDiagnostics.Mark(DiagnosticStage.IslandPerformance, generation: _profilePasses,
            durationMs: _profileMaxReconcileMs, x: _profileMeasures, y: _profileFrames,
            width: ContentExpandedWidth, height: _backgroundCache.Count,
            dpi: IsVisible ? 1 : 0, phase: _loopOn ? 1 : 0);
        _profilePasses = _profileFrames = _profileMeasures = 0;
        _profileMaxReconcileMs = 0;
    }

    private string? _suspendedExpandedFeature;
    private readonly Dictionary<string, DateTime> _pendingNotices = new(StringComparer.Ordinal);

    private void PublishOrdinaryNotice(string featureId)
    {
        if (_disposed || Suppressed()) return;
        var now = IslandClock.Now;
        int duration = Math.Clamp(SettingsManager.Current.IslandVisibilityDuration, 1000, 10000);
        _activity.Pulse(featureId, now, now.AddMilliseconds(duration), alwaysTemporal: true);
        _pendingNotices[featureId] = now.AddMilliseconds(duration);
        PostActivity(IslandActivityReason.Pointer);
    }

    private void ReconcileDeferredNotices()
    {
        var now = IslandClock.Now;
        foreach (var pending in _pendingNotices.ToArray())
        {
            if (pending.Value <= now) { _pendingNotices.Remove(pending.Key); _activity.ForgetNotice(pending.Key); }
            else if (!InteractionProtected && !_expanded && !HasExclusive()) _pendingNotices.Remove(pending.Key);
        }
    }

    private int _settingsUpdateDepth;
    private bool _appearancePending, _measurementPending, _presentationPending, _backgroundPending;
    public void BeginSettingsUpdate() { _screenMembers.Clear(); _settingsUpdateDepth++; }
    public void EndSettingsUpdate()
    {
        if (_settingsUpdateDepth == 0 || --_settingsUpdateDepth != 0) return;
        bool appearance = _appearancePending, measurement = _measurementPending, presentation = _presentationPending, background = _backgroundPending;
        _appearancePending = _measurementPending = _presentationPending = _backgroundPending = false;
        if (appearance) RefreshAppearance();
        else if (measurement) SyncMeasuredHeight();
        if (background) UpdateBackgroundMode();
        if (presentation) RefreshPresentation();
        PostActivity(IslandActivityReason.Settings);
    }

    private IntPtr _keyboardPreviousForeground;
    private uint _keyboardPreviousProcess;
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private bool _keyboardMode;
    private bool _timerLayoutPending;
    private bool _paintingReels;
    private bool InteractionProtected => _keyboardMode || _drag || _reelDragging || _shelfDragging
        || _shelfDropHot || _islandMenuOpen;
    private bool _shelfDragging;
    private bool _islandMenuOpen;
    private int _actionStatusVersion;
    private Action? _actionRetry;
    private void ActionRetry_Click(object sender, RoutedEventArgs e) => _actionRetry?.Invoke();
    private void ActionSettings_Click(object sender, RoutedEventArgs e) { _main.Show(); _main.Activate(); }

    private void ShowActionFeedback(string message, Action? retry = null, bool settings = false)
    {
        if (_disposed) return;
        ActionStatus.Text = message;
        ActionFeedback.Visibility = Visibility.Visible;
        _actionRetry = retry;
        ActionRetry.Visibility = retry == null ? Visibility.Collapsed : Visibility.Visible;
        ActionSettings.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        int version = ++_actionStatusVersion;
        _ = AfterIslandDelay(5000, () =>
        {
            if (version == _actionStatusVersion) { ActionStatus.Text = ""; ActionFeedback.Visibility = Visibility.Collapsed; _actionRetry = null; }
        });
    }

    private static DependencyObject? IslandParent(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(node) : node is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(node);

    private bool IsInsideScrollControl(DependencyObject source)
    {
        for (DependencyObject? node = source; node != null; node = IslandParent(node))
            if (node is ScrollViewer scroll && !ReferenceEquals(scroll, ExpandedViewport) && (scroll.ScrollableHeight > 0 || scroll.ScrollableWidth > 0)
                || node is System.Windows.Controls.Primitives.RangeBase) return true;
        return false;
    }

    public void OpenForKeyboard()
    {
        if (_disposed || DictationActive() || !SettingsManager.Current.IslandEnabled) return;
        _keyboardPreviousForeground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowProcessId(_keyboardPreviousForeground, out _keyboardPreviousProcess);
        _keyboardMode = true;
        if (!ExpandLastUsable()) { _keyboardMode = false; return; }
        SetKeyboardActivation(true);
        Activate();
        foreach (var input in new[] { ReelH, ReelM, ReelS })
        {
            input.IsReadOnly = false;
            input.Focusable = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_disposed && _keyboardMode)
            {
                if (TimerExpanded.IsVisible) { ReelH.Focus(); ReelH.SelectAll(); }
                else MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        }));
    }

    private void SetKeyboardActivation(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            enabled ? style & ~NativeMethods.WS_EX_NOACTIVATE : style | NativeMethods.WS_EX_NOACTIVATE);
    }

    private void LeaveKeyboardMode()
    {
        _keyboardMode = false;
        SetKeyboardActivation(false);
        foreach (var input in new[] { ReelH, ReelM, ReelS })
        {
            input.IsReadOnly = true;
            input.Focusable = false;
        }
        Keyboard.ClearFocus();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_keyboardPreviousForeground != IntPtr.Zero && _keyboardPreviousForeground != hwnd
            && NativeMethods.GetForegroundWindow() == hwnd && NativeMethods.IsWindow(_keyboardPreviousForeground))
        {
            NativeMethods.GetWindowProcessId(_keyboardPreviousForeground, out uint process);
            if (process == _keyboardPreviousProcess) SetForegroundWindow(_keyboardPreviousForeground);
        }
        _keyboardPreviousForeground = IntPtr.Zero;
    }

    private void Island_Deactivated(object? sender, EventArgs e)
    {
        if (!_keyboardMode) return;
        CommitTimerEntry();
        LeaveKeyboardMode();
        PostActivity(IslandActivityReason.Pointer);
    }

    private void Island_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_keyboardMode || e.Key != Key.Escape) return;
        e.Handled = true;
        LeaveKeyboardMode();
        if (!HasExclusive()) CollapseFromHover();
    }

    private bool CommitTimerEntry()
    {
        if (_paintingReels || !_keyboardMode || _timer.State != IslandTimerState.Idle) return true;
        if (!int.TryParse(ReelH.Text, out int h) || !int.TryParse(ReelM.Text, out int m)
            || !int.TryParse(ReelS.Text, out int seconds) || h is < 0 or > 24
            || m is < 0 or > 59 || seconds is < 0 or > 59 || (h == 24 && (m != 0 || seconds != 0)))
        {
            TimerStatus.Text = IslandStrings.Get("IslandTimerInvalid", "Enter a duration between 00:00:01 and 24:00:00.");
            return false;
        }
        _staged = new TimeSpan(h, m, seconds);
        _timerInputCustom = true;
        TimerPresetList.SelectedItem = null;
        TimerStartBtn.IsEnabled = _staged > TimeSpan.Zero;
        TimerStatus.Text = _staged == TimeSpan.Zero ? IslandStrings.Get("IslandTimerZero", "Choose at least one second.") : "";
        return true;
    }

    private void TimerEntry_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_paintingReels && _keyboardMode) CommitTimerEntry();
    }

    private void TimerEntry_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTimerEntry();

    private void TimerEntry_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_keyboardMode) return;
        if (e.Key is Key.Up or Key.Down && sender is FrameworkElement { Tag: string unit })
        {
            if (CommitTimerEntry()) ReelStep(unit, e.Key == Key.Up ? 1 : -1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (CommitTimerEntry() && _staged > TimeSpan.Zero) TimerStart_Click(sender, e);
            e.Handled = true;
        }
    }

    private void TimerConfig_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || _loopOn || _timerLayoutPending || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5) return;
        _timerLayoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _timerLayoutPending = false;
            if (_disposed) return;
            LayoutTimerEditor(TimerConfigGrid.ActualWidth);
            SyncMeasuredHeight();
        }));
    }

    private void LayoutTimerEditor(double width)
    {
        ConfigureTimerEditor(TimerConfigGrid, TimerPresetList, width);
    }

    internal static void ConfigureTimerEditor(Grid grid, ListBox presets, double width)
    {
        bool wide = width >= 360;
        grid.ColumnDefinitions[1].Width = new GridLength(wide ? 140 : 0);
        Grid.SetRow(presets, wide ? 0 : 1);
        Grid.SetColumn(presets, wide ? 1 : 0);
        presets.Margin = wide ? new Thickness(12, 0, 0, 0) : new Thickness(0, 8, 0, 0);
        presets.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        if (grid.Children.OfType<ComboBox>().FirstOrDefault() is { } picker)
            picker.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShelfCancelOperation_Click(object sender, RoutedEventArgs e) => SettingsManager.Current.CancelIslandShelfOperation();

    private void ReleaseReelCapture()
    {
        foreach (var input in new[] { ReelH, ReelM, ReelS })
            if (input.IsMouseCaptured) input.ReleaseMouseCapture();
        _reelDragging = false;
    }

    private void Reel_LostCapture(object sender, MouseEventArgs e)
    {
        _reelDragging = false;
        PostActivity(IslandActivityReason.Pointer);
    }

    private void AppLaunch_Action(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IslandApp app }) LaunchApp(app);
    }

    private void ShelfItem_Action(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IslandShelfItem item })
        {
            var menu = new ContextMenu();
            var restore = new MenuItem { Header = IslandStrings.Get("IslandShelfReturnOriginal", "Return to original folder") };
            restore.Click += async (_, _) => await SettingsManager.Current.RemoveIslandShelfItemAsync(item);
            menu.Items.Add(restore);
            menu.PlacementTarget = sender as UIElement;
            _islandMenuOpen = true;
            menu.Closed += (_, _) => { _islandMenuOpen = false; PostActivity(IslandActivityReason.Pointer); };
            menu.IsOpen = true;
        }
    }

    private void OnSystemAppearanceChanged(object? sender, PropertyChangedEventArgs e) => QueueIslandUi(() =>
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
        {
            RefreshAppearance();
            UpdateBackgroundMode();
            SyncPresentationWork();
        }
    });

    private bool? _lastMotionAllowed;

    private void ApplyAccessibleAppearance()
    {
        bool contrast = SystemParameters.HighContrast;
        bool motion = AnimationsEnabled;
        bool motionChanged = _lastMotionAllowed.HasValue && _lastMotionAllowed != motion;
        _lastMotionAllowed = motion;
        Resources["IslandForeground"] = contrast ? SystemColors.WindowTextBrush : Brushes.White;
        Resources["IslandSecondaryOpacity"] = contrast ? 1.0 : 0.75;
        Resources["IslandCaptionOpacity"] = contrast ? 1.0 : 0.75;
        Resources["IslandEmphasisOpacity"] = contrast ? 1.0 : 0.85;
        IslandOutline.Stroke = contrast && SettingsManager.Current.IslandBorderEnabled ? SystemColors.WindowTextBrush : _shelfDropHot ? ShelfDropBrush : SettingsManager.Current.IslandBorderEnabled ? IslandBorderBrush : Brushes.Transparent;
        Resources["IslandTileFill"] = contrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromArgb(0xD9, 0x25, 0x25, 0x25));
        Resources["IslandTileHover"] = contrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromArgb(0xF2, 0x3A, 0x3A, 0x3A));
        Resources["IslandTileBorder"] = contrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255));
        ContentScrim.Background = contrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0));
        if (!motion)
        {
            StopLoop();
            StopAlbumFlip();
            SnapFrame();
        }
        if (motionChanged) UpdateBackgroundMode();
        UpdateRotationPauseState();
    }
}
