// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Timer of the Fluent Island (spec 002): container content alongside the music and
/// the application drawer. Compact with icon + remaining + optional progress;
/// expanded with free time + controls on the left and presets on the right; zero
/// notice with forced deployment; navigation between features with the wheel and the
/// arrows.
/// </summary>
public partial class IslandWindow
{
    private readonly IslandTimer _timer = new();
    private IslandContentMode _contentMode; // current content band (IslandWindow.Views.cs)
    private bool _pendingTimerAlert;
    private TimeSpan _staged = TimeSpan.Zero; // value of the reels, custom origin
    private bool _timerInputCustom = true;
    // Anti-reappearance after dismissing: the mouse is still over it and the poll would re-expand it.
    private const int TimerReshowSnoozeSeconds = 2;
    // Reel drag: pixels per unit and gesture state.
    private const double ReelPixelsPerUnit = 24;
    private bool _reelDragging;
    private string _reelDragUnit = "";
    private double _reelDragStartY;
    private int _reelDragStartVal;

    private bool TimerModeAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandTimerEnabled;

    /// <summary>
    /// OWN activity of the timer (change island-lista-de-activos): with a LIVE countdown
    /// —running or PAUSED— the container holds it and its compact shows the time left. A
    /// paused countdown is still a countdown: it used to fall back to rest in «Visible while
    /// active» (002 MOD RF-7) and the user was left without seeing their time. The final
    /// alert does not go in here: it is exclusive and rules on its own (002 MOD RF-2).
    /// </summary>
    private bool IsTimerActiveForCompact() =>
        SettingsManager.Current.IslandEnabled
        && SettingsManager.Current.IslandTimerEnabled
        && _timer.IsCounting;

    // Implementations of the container contract for the timer
    // (001 MOD RF-11, RF-13): each view opens only if it is still usable.
    internal bool ShowTimerExpandedFromContract()
    {
        if (!TimerModeAvailable()) return false;
        ExpandTimer();
        return true;
    }

    internal bool ShowTimerCompactFromContract()
    {
        if (!TimerModeAvailable()) return false;
        ShowTimerCompact();
        return true;
    }

    private bool TimerKeepsAlive() =>
        TimerModeAvailable() && (_timer.IsCounting || _timer.State == IslandTimerState.Alerting || _pendingTimerAlert);

    /// <summary>
    /// Does the timer have a notice to show? Current = the deadline of the temporary
    /// notice is running; PENDING = a countdown action (start, resume, restart) done inside
    /// the expanded view, whose deadline starts when the compact is presented (002 RF-16).
    /// Without this, collapsing from the expanded view went straight to rest and the timer
    /// notice was never seen in «Temporary notice».
    /// </summary>
    private bool TimerNoticeAlive() => _pendingTimerNotice || NoticeAliveFor(IslandContentMode.Timer);

    /// <summary>
    /// «Temporary notice» (001 RF-2, 002 RF-16): the actions that set the countdown
    /// running —start, resume, restart— generate the timer notice just as playing generates
    /// the media one. The deadline does NOT run inside the expanded view: it stays pending and
    /// starts when the timer compact is presented (<see cref="ShowTimerCompact"/>), which is
    /// when the notice is seen, so the user enjoys the whole configured duration. The
    /// countdown itself is left untouched and the final alert (exclusive) takes no notice.
    /// </summary>
    private void ArmTimerNotice()
    {
        if (SettingsManager.Current.IslandVisibilityMode != 1 || !TimerModeAvailable()) return;
        if (HasExclusive()) return;
        // With the timer compact already on screen its current deadline rules: the
        // countdown action does not restart a notice that was already running
        // (001 RF-2). It also holds with the timer inside a screen.
        if (!_expanded && IsBoxShown && ViewShowsFeature(IslandFeatureIds.Timer)) return;
        // The deadline does NOT run inside the expanded view: it stays PENDING and starts
        // when the timer compact is presented (002 RF-16). ArmTemporaryHide consumes it.
        _pendingTimerNotice = true;
        RefreshPresentation();
    }

    private void InitTimer()
    {
        _timer.Finished += OnTimerFinished;
        // State change notification (002 MOD RF-2/RF-13): the host re-arms its one-shot
        // expiry alarm and publishes the activity; the countdown does NOT depend on any
        // heartbeat.
        _timer.Changed += OnTimerChanged;
        TimerPresetList.ItemsSource = SettingsManager.Current.IslandTimerPresets;
    }

    /// <summary>
    /// Enable setting: turning it off with a live countdown cancels it (RF-10).
    /// </summary>
    public void RefreshTimerEnabled()
    {
        if (!TimerModeAvailable())
        {
            _timer.Cancel();
            _pendingTimerAlert = false;
            if (_contentMode == IslandContentMode.Timer)
            {
                _contentMode = IslandContentMode.Media;
                if (_expanded)
                {
                    // The music only comes back if the music content is
                    // enabled and the snapshot has it available.
                    var session = MusicContentShown() ? Current() : null;
                    if (session != null) RefreshUi(session);
                    else { ClearMusicResidue(); HidePerMode(); }
                }
                else HidePerMode();
            }
        }
        RefreshTimerUI();
        ApplyContentVisibility();
    }

    private void RefreshTimerUI()
    {
        UpdateArrows();
        if (!TimerModeAvailable()) return;
        TimerRemaining.Text = IslandTimer.FormatHms(_timer.Remaining);
        // The progress is only shown if its setting asks for it and the ultra compact
        // mode does not remove it: in ultra the middle of the capsule is empty on purpose.
        TimerProgressZone.Visibility = SettingsManager.Current.IslandTimerShowProgress && !UltraCompactOn
            ? Visibility.Visible : Visibility.Collapsed;
        double track = TimerProgressTrack.ActualWidth;
        TimerProgressFill.Width = track > 0 ? track * _timer.ElapsedFraction : 0;
        bool idle = _timer.State == IslandTimerState.Idle;
        if (idle) PaintReels();
        TimerRunOrigin.Text = _timer.OriginLabel;
        TimerRunRemaining.Text = IslandTimer.FormatHms(_timer.Remaining);
        TimerRunPauseGlyph.Symbol = _timer.State == IslandTimerState.Paused
            ? Wpf.Ui.Controls.SymbolRegular.Play24
            : Wpf.Ui.Controls.SymbolRegular.Pause24;
        TimerStartBtn.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        TimerRestartBtn.Visibility = idle && _timer.Configured > TimeSpan.Zero
            ? Visibility.Visible : Visibility.Collapsed;
        TimerAlertName.Text = _timer.OriginLabel;
    }

    private void PaintReels()
    {
        long total = (long)_staged.TotalSeconds;
        ReelH.Text = $"{total / 3600:00}";
        ReelM.Text = $"{(total % 3600) / 60:00}";
        ReelS.Text = $"{total % 60:00}";
    }

    /// <summary>
    /// Switches the content layers and, with the same change, adjusts the per-content
    /// cadences and the equalizer: the view refresh and the visualizer only run while
    /// their content is on screen (001 MOD RF-14/16; 002 MOD RF-15).
    /// </summary>
    private void ApplyContentVisibility()
    {
        ApplyContentVisibilityCore();
        // The ultra compact mode is applied after deciding the layers: it only removes
        // the MIDDLE of the view that ended up in front (see IslandWindow.UltraCompact.cs).
        ApplyUltraCompactContent();
        SyncEq();
        UpdateVisibleRefresh();
    }

    /// <summary>
    /// Switches the content layers of the container (music, timer, drawer, shelf or
    /// calendar) in compact and expanded. Each layer is shown only if its feature is still
    /// usable: without availability there is no empty view (001 MOD RF-9).
    /// </summary>
    private void ApplyContentVisibilityCore()
    {
        // The compact layer recovers its usual width: the compact never groups
        // (change island-pantallas), so it measures what a single feature takes —or what
        // its two ends measure, with the ultra compact mode on—.
        CompactLayer.Width = RestCompactWidth(SingleCompactLayerWidth);
        if (_contentMode == IslandContentMode.Screen)
        {
            // The SCREEN mode is the composition of the EXPANDED view (its columns, from
            // left to right): it is what a click opens. The painting cannot call
            // ExpandCurrentScreen/ShowCurrentScreenCompact because those routes come back
            // in here and mix states. If availability left a single feature, the mode is
            // changed and the normal painting presents it as a rich view.
            var members = CurrentScreenFeatures();
            if (_expanded && members.Count > 1 && CurrentScreenColumnCount() > 0)
            {
                ApplyScreenLayerVisibility();
                return;
            }
            IIslandFeature? owner = _expanded
                ? members.FirstOrDefault()
                : CompactMemberOfCurrentScreen();
            if (owner is { } feature && (!_expanded || ColumnFor(feature.Id) != null))
            {
                _contentMode = ModeForFeature(feature.Id);
                SelectFeature(feature.Id);
            }
            else if (_expanded && owner is { })
            {
                // Member with no column of its own (a compact-only feature): the
                // container is not left expanded with a screen with nothing to show.
                _expanded = false;
                _contentMode = ModeForFeature(owner.Id);
                SelectFeature(owner.Id);
            }
            else
            {
                ShowInactiveOrHidden();
                return;
            }
        }
        // Outside a combined screen the expanded panels live in their usual place
        // (a no-op if none of them had been moved to a column).
        RestoreExpandedHomes();
        // The FACE that is seen is the one of the feature of the current mode —its card
        // knows which compact tile to light up and which panels—: a single decision, with no
        // chain of branches that could leave the previous view layer on.
        //
        // If its mode is not presentable right now (feature turned off, timer with no
        // countdown, a screen with no composition…), the box keeps the music face, which is
        // the default content of the container. That is what keeps an empty box from ever
        // showing anything stranger than the player on blank (001 MOD RF-9).
        var card = FeatureCardOfMode(_contentMode);
        if (card == null || !card.ModeAvailable()) card = FeatureCard(IslandFeatureIds.Media);
        if (card != null) ShowContentFace(card);
        UpdateArrows();
    }

    // Entry fade only: the outgoing one collapses instantly so as not to measure two
    // stacked panels (that inflated the height).
    private void CrossfadeTimerPanels(bool showConfig, bool showRun)
    {
        FadeInPanel(TimerExpanded, showConfig);
        FadeInPanel(TimerRunPanel, showRun);
    }

    // Fade ONLY on the hidden->visible transition: the LOCAL opacity of the panel is
    // always 1 (the final value), and the animation only drives it during the 150 ms of
    // the fade. That way, repeating ApplyContentVisibility (container updates, settings,
    // start/pause) can never push the panel back to opacity 0 and leave the box black
    // (001/002 ADDED RF-1).
    private static void FadeInPanel(UIElement el, bool show)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 1; // local value = final state; the animation only covers the gesture
        if (!show)
        {
            el.Visibility = Visibility.Collapsed;
            return;
        }
        if (el.Visibility == Visibility.Visible) return;
        el.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        fadeIn.Completed += (_, _) => el.BeginAnimation(UIElement.OpacityProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    // Every engine state change re-switches the panels and re-measures the height.
    // The loop guarantees it with SyncMeasuredHeight if the target changed.
    private void RefreshTimerModeView()
    {
        // An action that leaves the countdown running (start, resume, restart)
        // generates the timer notice in «Temporary notice» (002 RF-16): it is the only
        // point through which the engine reports its new state, so the notice is armed
        // here and not on each button.
        if (_timer.State == IslandTimerState.Running) ArmTimerNotice();
        // The view is resolved by the ACTIVE LIST (change island-lista-de-activos): a
        // live countdown —running or paused— holds its compact, and the cancelled countdown
        // gives way to another active one or to rest. Here only the event is published; the
        // T2 rule of 002 MOD RF-7 (a pause holds nothing) is MODIFIED: a paused countdown
        // still has a remaining time to show.
        RefreshPresentation();
        RefreshTimerUI();
        SyncMeasuredHeight();
    }

    /// <summary>
    /// Safety net of 001/002 ADDED RF-1: while the timer is the active and visible
    /// content, its panel (compact or expanded) has to be present. If a container update
    /// left the box without any timer panel (black surface), the content is applied again
    /// and the incident is logged so it can be diagnosed.
    /// </summary>
    private void EnsureTimerContentShown()
    {
        if (_disposed || _contentMode != IslandContentMode.Timer || !IsBoxShown || !TimerModeAvailable()) return;
        if (_timer.State == IslandTimerState.Alerting
            && TimerAlert.Visibility == Visibility.Visible) return;
        bool compactOk = TimerCompactGrid.Visibility == Visibility.Visible;
        bool expandedOk = TimerExpanded.Visibility == Visibility.Visible
            || TimerRunPanel.Visibility == Visibility.Visible
            || TimerAlert.Visibility == Visibility.Visible;
        if (_expanded ? expandedOk : compactOk) return;
        Logger.Warn("Island: timer content missing with the box visible " +
            "(expanded={Expanded}, timerState={State}, compact={Compact}, " +
            "config={Config}, run={Run}, alert={Alert}); re-applying content",
            _expanded, _timer.State,
            TimerCompactGrid.Visibility, TimerExpanded.Visibility,
            TimerRunPanel.Visibility, TimerAlert.Visibility);
        ApplyContentVisibility();
        RefreshTimerUI();
        SyncMeasuredHeight();
    }

    private void UpdateArrows()
    {
        // Optional arrows: only with more than one SCREEN with something usable, whatever
        // their features are (002 MOD RF-9; change island-pantallas RF-4: the navigation
        // goes by screens).
        bool show = _expanded && SettingsManager.Current.IslandTimerShowArrows && IsBoxShown
            && UsableScreenCount() > 1;
        ModePrevBtn.Visibility = ModeNextBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTimerCompact()
    {
        if (!TimerModeAvailable()) { SnapHidden(); return; }
        // «Temporary notice»: the timer compact is a notice like the media one and it
        // expires too (002 RF-8/RF-16) with the countdown intact behind it. The PENDING
        // notice —a countdown action done inside the expanded view— starts its deadline
        // here: ArmTemporaryHide consumes it, which is where the notice rule lives.
        ShowCompactView(IslandContentMode.Timer, TimerFeature, RefreshTimerUI);
    }

    /// <summary>
    /// End of the countdown by user action (the X of the final notice or cancel from the
    /// running panel): the Island does NOT stay open on the configuration, it contracts
    /// like any other collapse (001 MOD RF-4, 002 RF-6). The veto for the pointer being over
    /// it does not apply here —the user just finished the timer on purpose— and the hover
    /// reopening is silenced so that the box does not come back on its own after 150 ms.
    /// </summary>
    private void CompactAfterTimerStopped()
    {
        _staged = TimeSpan.Zero;
        TimerStatus.Text = "";
        _hoverSnoozeUntil = DateTime.UtcNow.AddSeconds(TimerReshowSnoozeSeconds);
        _expanded = false;
        RefreshTimerUI();
        // The timer closing is an event of its own: in «Temporary notice», with music
        // playing, its compact returns to the view with its own deadline (002 RF-6).
        if (SettingsManager.Current.IslandVisibilityMode == 1 && ActiveMediaSession() is { } session)
        {
            ShowMusicCompact(session);
            return;
        }
        // In any other case the active list rules: the feature still alive or rest,
        // with no residue and without opening an empty box (001 MOD RF-4).
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        ClearMusicResidue();
        RefreshPresentation();
    }

    private void ExpandTimer()
    {
        if (!TimerModeAvailable()) return;
        ShowExpandedView(IslandContentMode.Timer, TimerFeature, RefreshTimerUI);
    }

    private void OnTimerFinished() => Dispatcher.Invoke(ShowTimerAlert);

    /// <summary>
    /// Zero notice: it forces the deployment even if it was hidden; with persistent
    /// exclusive access it goes through suppression (001 RF-8/14, 002 RF-2).
    /// </summary>
    private void ShowTimerAlert()
    {
        if (!TimerModeAvailable()) return;
        if (!SettingsManager.Current.IslandEnabled) { _pendingTimerAlert = true; return; }
        // skipIfExpanded:false - the alert is exclusive and must prevail over the
        // current view even if the box was already expanded (002 RF-2/RF-6).
        ShowExpandedView(IslandContentMode.Timer, TimerFeature, RefreshTimerUI, skipIfExpanded: false);
    }

    /// <summary>
    /// Switches SCREEN (wheel or arrows, 002 MOD RF-3/RF-9): it walks through the screens
    /// with something usable in the order of the screen list, wrapping around, in the given
    /// direction. With the expanded view in front each screen opens its column composition
    /// —or the rich view of its single feature— and, with the compact, the rich view of the
    /// feature holding it (the compact never groups), so without availability it never lands
    /// on an empty view (001 MOD RF-9).
    ///
    /// <para>The countdown engine and the snapshot are never touched: changing view does not
    /// cancel nor restart the timer countdown (002 MOD RF-9, RF-13).</para>
    /// </summary>
    private void CycleMode(int direction = 1)
    {
        // Modal alert: until stop or restart there is no way out to the other modes.
        if (_timer.State == IslandTimerState.Alerting) return;
        // Navigation by SCREENS (change island-pantallas RF-4): each step looks for the
        // next screen with something usable, wrapping around, and presents it in the state
        // the container is in (its columns if it is expanded, the rich view of one of its
      // features if it is compact).
        if (_screens.Count == 0) return;
        for (int step = 1; step <= _screens.Count; step++)
        {
            int index = WrapUnit(_screenIndex + direction * step, _screens.Count);
            if (ScreenUsableFeatures(_screens[index]).Count == 0) continue;
            _screenIndex = index;
            bool shown = _expanded ? ExpandCurrentScreen() : ShowCurrentScreenCompact();
            if (!shown) HidePerMode();
            return;
        }
    }

    // --- expanded view controls ---

    private void TimerCompact_Click(object sender, MouseButtonEventArgs e) => ExpandTimer();

    private void ModeArrow_Click(object sender, RoutedEventArgs e) =>
        CycleMode(ReferenceEquals(sender, ModePrevBtn) ? -1 : 1);

    private static int WrapUnit(int value, int count) => ((value % count) + count) % count;

    private void SplitStaged(out int h, out int m, out int s)
    {
        long total = (long)_staged.TotalSeconds;
        h = (int)(total / 3600);
        m = (int)((total % 3600) / 60);
        s = (int)(total % 60);
    }

    private void ReelStep(string unit, int dir)
    {
        SplitStaged(out int h, out int m, out int s);
        switch (unit)
        {
            case "H": h = WrapUnit(h + dir, 25); break;
            case "M": m = WrapUnit(m + dir, 60); break;
            default: s = WrapUnit(s + dir, 60); break;
        }
        _staged = new TimeSpan(h, m, s);
        _timerInputCustom = true;
        TimerPresetList.SelectedItem = null;
        TimerStatus.Text = "";
        RefreshTimerUI();
    }

    private void Reel_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string unit) return;
        _reelDragging = true;
        _reelDragUnit = unit;
        _reelDragStartY = e.GetPosition(this).Y;
        SplitStaged(out int h, out int m, out int s);
        _reelDragStartVal = unit switch { "H" => h, "M" => m, _ => s };
        el.CaptureMouse();
        e.Handled = true;
    }

    private void Reel_Move(object sender, MouseEventArgs e)
    {
        if (!_reelDragging || sender is not FrameworkElement el || !el.IsMouseCaptured) return;
        // Up increases, down decreases; the value wraps (endless reel).
        int steps = (int)((_reelDragStartY - e.GetPosition(this).Y) / ReelPixelsPerUnit);
        SplitStaged(out int h, out int m, out int s);
        int current = _reelDragUnit switch { "H" => h, "M" => m, _ => s };
        int count = _reelDragUnit == "H" ? 25 : 60;
        int next = WrapUnit(_reelDragStartVal + steps, count);
        if (next == current) return;
        switch (_reelDragUnit)
        {
            case "H": h = next; break;
            case "M": m = next; break;
            default: s = next; break;
        }
        _staged = new TimeSpan(h, m, s);
        _timerInputCustom = true;
        TimerPresetList.SelectedItem = null;
        TimerStatus.Text = "";
        PaintReels();
    }

    private void Reel_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.IsMouseCaptured) el.ReleaseMouseCapture();
        _reelDragging = false;
        RefreshTimerUI();
    }

    // Wheel on a digit: up decreases, down increases (the owner's criterion).
    private void Reel_Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.Tag is not string unit) return;
        ReelStep(unit, e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void TimerPresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimerPresetList.SelectedItem is TimerPreset preset)
        {
            _timerInputCustom = false;
            _staged = TimeSpan.FromSeconds(preset.DurationSeconds);
            TimerStatus.Text = "";
            RefreshTimerUI();
        }
    }

    private void TimerStart_Click(object sender, RoutedEventArgs e)
    {
        TimeSpan duration;
        string origin;
        if (!_timerInputCustom && TimerPresetList.SelectedItem is TimerPreset preset)
        {
            duration = TimeSpan.FromSeconds(preset.DurationSeconds);
            origin = preset.Name;
        }
        else
        {
            duration = _staged;
            origin = IslandStrings.Get("IslandTimerOrigin", "Timer");
        }
        if (!_timer.Start(duration, origin))
            TimerStatus.Text = IslandStrings.Get("IslandInvalidDuration", "Invalid duration: use 00:00:01 to 24:00:00.");
        else
        {
            NoteFeatureEvent("timer");
            TimerStatus.Text = "";
        }
        RefreshTimerModeView();
    }

    private void TimerPause_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.State == IslandTimerState.Running) _timer.Pause();
        else if (_timer.State == IslandTimerState.Paused) { _timer.Resume(); NoteFeatureEvent("timer"); }
        RefreshTimerModeView();
    }

    private void TimerRestart_Click(object sender, RoutedEventArgs e)
    {
        _timer.Restart();
        _staged = _timer.Configured;
        TimerStatus.Text = "";
        RefreshTimerModeView();
    }

    private void TimerCancel_Click(object sender, RoutedEventArgs e)
    {
        // Cancelling is a deliberate end: the Island contracts, it does not stay
        // open on the configuration (001 MOD RF-4).
        _timer.Cancel();
        CompactAfterTimerStopped();
    }

    private void TimerAlertRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.Configured > TimeSpan.Zero)
            _timer.Start(_timer.Configured, _timer.OriginLabel);
        RefreshTimerModeView();
    }

    private void TimerAlertDismiss_Click(object sender, RoutedEventArgs e)
    {
        // X of the final notice (002 RF-6): it cancels the countdown and contracts the
        // Island (to media if there is a session, otherwise to inactive/nothing depending on
        // the toggle), without staying showing the dangling notice (001 MOD RF-4).
        _timer.Cancel();
        SelectFeature("media");
        CompactAfterTimerStopped();
    }
}
