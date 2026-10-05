// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Activity reasons that can wake the Island up. They are combinable: a burst
/// (metadata + playback + timer + context) is coalesced into ONE reconciliation with
/// all the reasons and the last state (change island-actividad-orientada-eventos,
/// 001 MOD RF-1; 002 MOD RF-13).
/// </summary>
[Flags]
internal enum IslandActivityReason
{
    None = 0,
    Media = 1 << 0,
    Timer = 1 << 1,
    Pointer = 1 << 2,
    Context = 1 << 3,
    Settings = 1 << 4,
    Calendar = 1 << 5,
    /// <summary>Slow recovery net: covers lost events without polling.</summary>
    Recovery = 1 << 6,
    /// <summary>Bluetooth devices: connection and refinement of their battery.</summary>
    Bluetooth = 1 << 7,
    /// <summary>Clipboard: a copied item arrived or left.</summary>
    Clipboard = 1 << 8,
    /// <summary>Weather: new data arrived (or the configured place changed).</summary>
    Weather = 1 << 9,
    /// <summary>Charger: the machine was plugged in or unplugged.</summary>
    Power = 1 << 10,
}

/// <summary>
/// Event-oriented, low-power activity: it replaces the 200 ms heartbeat and the
/// 40 ms pointer poll with a coalesced mailbox, cadences limited per content and a 5 s
/// recovery net.
///
/// <para>Rules it upholds:</para>
/// <list type="bullet">
/// <item><b>Coalesced mailbox</b> (001 MOD RF-1, ADDED RF-3): the sources
/// (media, timer, context, pointer, settings, calendar) publish reasons and the
/// container reconciles ONCE with the last state, without losing events that arrive
/// during the reconciliation.</item>
/// <item><b>Cadence per content</b> (001 MOD RF-16; 002 MOD RF-15): the
/// view refresh only runs while its content is on screen; there is no global
/// 40-200 ms cycle at all.</item>
/// <item><b>5 s recovery</b> (001 MOD RF-2/RF-28, 002 MOD RF-13/RF-16):
/// a single slow alarm re-reads context, media and timer to cover lost
/// events, suspension or resume.</item>
/// <item><b>Expiry with its own alarm</b> (002 MOD RF-2/RF-6): the timer
/// arms a one-shot alarm on its target time and the temporary notice
/// keeps its expiry through a one-shot timer.</item>
/// <item><b>Context through Windows events</b> (001 MOD RF-12): suppression,
/// foreground, DPI and geometry are recomputed on receiving the event and served
/// from a cached snapshot in the hot paths.</item>
/// </list>
///
/// <para>Part of IslandWindow; suppression and the equalizer live in
/// <c>IslandWindow.Monitoring.cs</c>, the pointer in <c>IslandWindow.Hover.cs</c>
/// and the state machine in <c>IslandWindow.States.cs</c>.</para>
/// </summary>
public partial class IslandWindow
{
    // Safety net: slow enough to wake nothing and short
    // enough to meet the 5 s recovery maximum (001 ADDED RF-3).
    private const int RecoveryIntervalMs = 5000;
    // Fringe fallback when the native hook is not available (001 MOD RF-3).
    private const int FallbackHoverIntervalMs = 250;
    // View refresh ONLY while there is visible content (001 MOD RF-16;
    // 002 MOD RF-15): timer countdown, expanded seek and calendar
    // countdowns. Never with the view hidden.
    private const int ViewRefreshIntervalMs = 300;

    // --- activity mailbox ---
    private IslandActivityReason _activityReasons;
    private bool _activityScheduled;
    private bool _reconciling;

    // --- cadences limited per content ---
    private DispatcherTimer? _recovery;
    private DispatcherTimer? _timerWake;
    private DispatcherTimer? _viewRefresh;
    private DispatcherTimer? _hoverFallback;
    private bool _fallbackInside;
    private IslandPointerHook? _pointerHook;
    // Should it be installed right now? (only with a live gate; see SyncPointerHook).
    private bool _pointerHookWanted;

    // --- context snapshot (001 MOD RF-12) ---
    private MonitorUtil.MonitorInfo _ctxPrimary;
    private bool _ctxSuppressed;
    private bool _ctxValid;
    private bool _ctxRefreshing;
    private IntPtr _foregroundHook = IntPtr.Zero;
    private NativeMethods.WinEventProc? _foregroundProc;

    private void InitActivity()
    {
        RefreshContextSnapshot();

        // Recovery net: the only permanent watch of the container, at 5 s.
        _recovery = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(RecoveryIntervalMs),
        };
        _recovery.Tick += OnRecoveryTick;
        _recovery.Start();

        SyncPointerHook();
        InstallForegroundHook();
        try
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Island: the system events could not be registered; the 5 s recovery is used");
        }

        // Safety net on close: no hook callback outlives the
        // window, even if the close does not go through Dispose (001 MOD RF-3).
        Closed += (_, _) => ShutdownActivity();
    }

    /// <summary>
    /// Releases every activity resource: no live timers and no callbacks after the
    /// close (001 MOD RF-3; 002 MOD RF-13).
    /// </summary>
    private void ShutdownActivity()
    {
        _recovery?.Stop();
        _recovery = null;
        _viewRefresh?.Stop();
        _viewRefresh = null;
        _hoverFallback?.Stop();
        _hoverFallback = null;
        _timerWake?.Stop();
        _timerWake = null;

        RemovePointerHook();
        _pointerHookWanted = false;

        if (_foregroundHook != IntPtr.Zero)
        {
            try { NativeMethods.UnhookWinEvent(_foregroundHook); } catch { }
            _foregroundHook = IntPtr.Zero;
        }
        _foregroundProc = null;

        try
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Coalesced mailbox (001 MOD RF-1; 002 MOD RF-13)
    // ------------------------------------------------------------------

    /// <summary>
    /// Publishes an activity reason. Bursts accumulate and produce ONE single
    /// reconciliation in the same UI cycle, with the last state: the intermediate
    /// events are neither lost nor painted.
    /// </summary>
    private void PostActivity(IslandActivityReason reason)
    {
        if (_disposed) return;
        _activityReasons |= reason;
        if (_activityScheduled || _reconciling) return;
        _activityScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reconcile));
    }

    private void Reconcile()
    {
        _activityScheduled = false;
        if (_disposed) return;
        if (_reconciling) return;
        _reconciling = true;
        try
        {
            // The whole mailbox is drained: the changes arriving DURING the
            // reconciliation are handled in the same pass with the last state.
            while (!_disposed)
            {
                var reasons = _activityReasons;
                if (reasons == IslandActivityReason.None) break;
                _activityReasons = IslandActivityReason.None;
                ReconcileCore(reasons);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Island: error while reconciling the activity");
        }
        finally
        {
            _reconciling = false;
        }
    }

    /// <summary>
    /// Single reconciliation of the container with the last state: suppression,
    /// presentation of the current activity, hover, equalizer, arrows and view
    /// only if it is on screen. It is the old heartbeat emptied of polling: it
    /// queries nothing it should not and never runs on its own clock.
    /// </summary>
    private void ReconcileCore(IslandActivityReason reasons)
    {
        // Each pass reads the media system from scratch (once per pass): the read memos
        // die here, so no decision is taken with data from the previous pass and the 5 s
        // recovery keeps seeing a fresh state.
        InvalidateMediaReads();
        if ((reasons & IslandActivityReason.Context) != 0 || !_ctxValid)
            RefreshContextSnapshot();

        // The mouse hook is installed/removed with the GATE. This pass is where
        // settings, suppression and expiries land, so it is always re-evaluated here
        // (idempotent and cheap: it only compares a boolean).
        SyncPointerHook();

        if (!SettingsManager.Current.IslandEnabled)
        {
            _wasSuppressed = false;
            SnapHidden();
            Visibility = Visibility.Collapsed;
            return;
        }

        // Persistent exclusive access (002 RF-2) goes through suppression (001 RF-8/14).
        if (Suppressed() && !HasExclusive() && !DictationFeedbackAvailable())
        {
            if (!_wasSuppressed)
            {
                _wasSuppressed = true;
                if (AnimationsEnabled && IsBoxShown)
                {
                    Visibility = Visibility.Visible;
                    GoHidden();
                    return;
                }
                SnapHidden();
            }
            else if (IsBoxShown)
            {
                GoHidden();
            }
            if (!IsBoxShown && !_loopOn) Visibility = Visibility.Collapsed;
            UpdateVisibleRefresh();
            return;
        }
        if (_wasSuppressed)
        {
            _wasSuppressed = false;
            // On leaving suppression, the exclusive one was already visible if it got through.
            if (HasExclusive() && IsBoxShown) { }
            else if (_pendingTimerAlert && SettingsManager.Current.IslandEnabled) { _pendingTimerAlert = false; ShowTimerAlert(); }
            // The view (compact of what is active or rest) is resolved by the active list.
            else
                RefreshPresentation();
        }
        else if (Suppressed() && HasExclusive() && !IsBoxShown)
        {
            // The exclusive one arrived while suppressed: deploy it even if suppression continues.
            RefreshPresentation();
        }

        // Timer expiry: recovery of lost events and re-arming of the
        // one-shot alarm (002 MOD RF-2/RF-6/RF-13).
        if ((reasons & IslandActivityReason.Timer) != 0)
            PollTimerSafely();

        // The only reconciliation with the media system of this pass: it adopts whatever
        // session is active and leaves ready the identity of what is playing.
        if ((reasons & IslandActivityReason.Media) != 0)
            ReconcileMediaState();
        else if ((reasons & IslandActivityReason.Recovery) != 0)
            ReconcileMediaState(recoveryOnly: true);

        // Bluetooth: a connection presents its notice by itself (OnBluetoothConnected);
        // here only what is ALREADY on screen is refined (a battery reading that arrives
        // late), it is never deployed again (change island-bluetooth-conectado RF-3/RF-5).
        if ((reasons & IslandActivityReason.Bluetooth) != 0)
            ReconcileBluetoothState();

        // Weather: new data (or a new place) only repaints the weather view if it is
        // already in front; it never deploys anything (change island-clima RF-3).
        if ((reasons & IslandActivityReason.Weather) != 0)
            ReconcileWeatherState();

        // Charger: the state change presents its notice by itself
        // (OnChargerConnected/OnChargerDisconnected); here it is only repainted if its
        // notice is still in front (change island-cargador).
        if ((reasons & IslandActivityReason.Power) != 0)
            ReconcilePowerState();

        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        // The VIEW comes out of the active list, in the same turn: an active event is
        // shown right away, without waiting for the geometry to settle or for any gate
        // (change island-lista-de-activos). The decision is a single one, even if the
        // reason arrived through different paths.
        if (DateTime.UtcNow >= _hoverSnoozeUntil) RefreshPresentation();
        if (_expanded && !_drag && !_reelDragging && !IsMouseOverBoxOrStrip()) LeaveHover();
        SyncEq();
        UpdateArrows();
        if (_contentMode == IslandContentMode.Timer && IsBoxShown && TimerModeAvailable())
        {
            RefreshTimerUI();
            EnsureTimerContentShown();
        }
        UpdateLine();
        UpdateVisibleRefresh();
    }

    /// <summary>
    /// Refreshes only what is visible: timer countdown, expanded seek and calendar
    /// countdowns. The view timer lives only while its view is on screen; turning it off
    /// does not touch the countdown (002 MOD RF-15; 001 MOD RF-14/RF-16).
    /// </summary>
    private void UpdateVisibleRefresh()
    {
        bool want = !_disposed && SettingsManager.Current.IslandEnabled && IsBoxShown
            && (_contentMode switch
            {
                IslandContentMode.Timer => TimerModeAvailable(),
                IslandContentMode.Calendar => CalendarModeAvailable(),
                // The expanded seek is only painted with the media view in front.
                IslandContentMode.Media => _expanded && MusicContentShown(),
                // The expanded view of a combined screen ages its countdowns
                // (timer, calendar) and its seek. Collapsed it does not go through here:
                // it shows ONE feature and its route sets the cadence.
                IslandContentMode.Screen => _expanded,
                _ => false,
            });
        if (want)
        {
            if (_viewRefresh == null)
            {
                _viewRefresh = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(ViewRefreshIntervalMs),
                };
                _viewRefresh.Tick += (_, _) => RefreshVisibleContent();
            }
            if (!_viewRefresh.IsEnabled) _viewRefresh.Start();
        }
        else
        {
            _viewRefresh?.Stop();
        }
    }

    private void RefreshVisibleContent()
    {
        if (_disposed || !IsBoxShown) return;
        if (_contentMode == IslandContentMode.Timer && TimerModeAvailable())
        {
            RefreshTimerUI();
            EnsureTimerContentShown();
        }
        else if (_contentMode == IslandContentMode.Calendar && CalendarModeAvailable())
        {
            // The countdowns («in 4 min») are recomputed when painting: repainting
            // while the view is on screen ages them on their own, with no heartbeat.
            RefreshCalendarList();
        }
        else if (_contentMode == IslandContentMode.Media && _expanded && Current() is { } session)
        {
            UpdateSeek(session);
        }
        else if (_contentMode == IslandContentMode.Screen)
        {
            RefreshCombinedScreenTick();
        }
    }

    // ------------------------------------------------------------------
    // Timer one-shot alarm and recovery (002 MOD RF-2/RF-6/RF-13)
    // ------------------------------------------------------------------

    /// <summary>
    /// Timer state change notification (start, pause, resume,
    /// restart, cancel, expire): it re-arms the expiry alarm, publishes
    /// the activity and refreshes only what is visible. The countdown does not depend on
    /// any heartbeat.
    /// </summary>
    private void OnTimerChanged()
    {
        ArmTimerWake();
        PostActivity(IslandActivityReason.Timer);
        UpdateVisibleRefresh();
    }

    /// <summary>
    /// Re-arms the SINGLE alarm on the target time of the timer. With the
    /// countdown stopped it turns off; without it there would be no expiry (002 MOD RF-6).
    /// After a suspension, the 5 s recovery re-arms it or honours it.
    /// </summary>
    private void ArmTimerWake()
    {
        if (_disposed) return;
        if (_timer.State != IslandTimerState.Running)
        {
            _timerWake?.Stop();
            return;
        }
        var left = _timer.Remaining;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        if (_timerWake == null)
        {
            _timerWake = new DispatcherTimer(DispatcherPriority.Normal);
            _timerWake.Tick += (_, _) =>
            {
                _timerWake?.Stop();
                // The target time rules: when it fires it is checked against the clock.
                _timer.Poll(DateTime.UtcNow);
            };
        }
        _timerWake.Stop();
        _timerWake.Interval = left + TimeSpan.FromMilliseconds(40);
        _timerWake.Start();
    }

    /// <summary>
    /// Safe expiry check: if the timer is still running and its target time has already
    /// passed (e.g. after suspending the machine), it is honoured here.
    /// </summary>
    private void PollTimerSafely()
    {
        if (_timer.State != IslandTimerState.Running) { ArmTimerWake(); return; }
        if (_timer.Remaining > TimeSpan.Zero) { ArmTimerWake(); return; }
        _timer.Poll(DateTime.UtcNow);
    }

    // ------------------------------------------------------------------
    // Slow recovery of lost events (001 MOD RF-2/RF-28; 002 RF-13)
    // ------------------------------------------------------------------

    private void OnRecoveryTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // Impure context (foreground/fullscreen/DPI) only here and on events.
        _ctxValid = false;
        PostRecovery();
        // Temporary notice: if its one-shot was lost, it is honoured here within 5 s.
        if (!_noticeCheckActive && _noticeUntil != DateTime.MinValue && _noticeUntil <= DateTime.UtcNow)
            RetractTemporaryNotice(_noticeVersion);
    }

    /// <summary>
    /// Publishes a recovery pass: it re-reads context, media, timer and
    /// calendar WITHOUT the media reason. That way the recovery only acts on what
    /// appeared with no event and never deploys again a view the user had
    /// already settled on (001 MOD RF-2/RF-28).
    /// </summary>
    private void PostRecovery()
    {
        PostActivity(IslandActivityReason.Context | IslandActivityReason.Recovery
            | IslandActivityReason.Timer | IslandActivityReason.Calendar);
    }

    // ------------------------------------------------------------------
    // Context and geometry through Windows events (001 MOD RF-12)
    // ------------------------------------------------------------------

    /// <summary>
    /// Recomputes the context snapshot: primary monitor (area, work area and
    /// DPI) and current suppression. The hot paths read the snapshot instead of
    /// querying Windows.
    /// </summary>
    private void RefreshContextSnapshot()
    {
        // Guard against recursion: ComputeSuppressed queries PrimaryMonitor and
        // this one, on refresh, would come back in here.
        if (_ctxRefreshing) return;
        _ctxRefreshing = true;
        try
        {
            try
            {
                _ctxPrimary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
            }
            catch
            {
                _ctxPrimary = default;
            }
            _ctxValid = true;
            _ctxSuppressed = ComputeSuppressed();
        }
        finally
        {
            _ctxRefreshing = false;
        }
    }

    /// <summary>
    /// Cached primary monitor (001 MOD RF-12): no enumerating monitors on every
    /// pointer crossing or repositioning. If the snapshot is not valid, it
    /// recomputes it.
    /// </summary>
    private MonitorUtil.MonitorInfo PrimaryMonitor()
    {
        if ((!_ctxValid || _ctxPrimary.monitorArea.Width == 0) && !_ctxRefreshing)
            RefreshContextSnapshot();
        return _ctxPrimary.monitorArea.Width != 0
            ? _ctxPrimary
            : MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
    }

    private void InstallForegroundHook()
    {
        try
        {
            _foregroundProc = OnForegroundEvent;
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        }
        catch
        {
            _foregroundHook = IntPtr.Zero;
        }
    }

    /// <summary>
    /// The foreground window changed: it can change the suppression (a window
    /// covering the monitor) with no polling at all (001 MOD RF-12).
    /// </summary>
    private void OnForegroundEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND) return;
        _ctxValid = false;
        PostActivity(IslandActivityReason.Context);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(QueueIslandDisplayRefresh));

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            HandleSystemWake();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) HandleSystemWake();
    }

    /// <summary>
    /// Machine resume or unlock: the target time of the timer rules, so its
    /// alarm is re-armed and everything that could have been lost is recovered,
    /// context included (001 MOD RF-2/RF-28; 002 MOD RF-6/RF-13).
    /// </summary>
    private void HandleSystemWake() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            _ctxValid = false;
            RefreshContextSnapshot();
            PollTimerSafely();
            PostRecovery();
        }));

    // ------------------------------------------------------------------
    // Native pointer notification with fallback (001 MOD RF-3)
    // ------------------------------------------------------------------

    /// <summary>
    /// The mouse hook (WH_MOUSE_LL) only lives while there is a live GATE: the system
    /// goes through it on EVERY mouse movement on the desktop, so with the Island
    /// off, suppressed or with nothing to point at, it is removed. For whoever only wants
    /// temporary notices —Bluetooth, dictation— no mouse callback is left behind.
    /// </summary>
    private void SyncPointerHook()
    {
        if (_disposed) return;
        bool want = SettingsManager.Current.IslandEnabled && !Suppressed() && AccessZoneActive();
        if (want == _pointerHookWanted) return;
        _pointerHookWanted = want;
        if (want) InstallPointerHook();
        else RemovePointerHook();
    }

    /// <summary>Removes the hook and its fallback: no live timers and no leftover crossings.</summary>
    private void RemovePointerHook()
    {
        _hoverFallback?.Stop();
        _hoverFallback = null;
        _fallbackInside = false;
        _pointerHook?.Dispose();
        _pointerHook = null;
    }

    private void InstallPointerHook()
    {
        if (_pointerHook != null || _hoverFallback != null) return;
        var hook = new IslandPointerHook(FringeContains, OnPointerFringeCross);
        if (hook.Install())
        {
            _pointerHook = hook;
            return;
        }
        hook.Dispose();
        Logger.Warn("Island: pointer hook not available; the bounded 250 ms detection is used without querying media");
        _hoverFallback = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FallbackHoverIntervalMs),
        };
        _hoverFallback.Tick += (_, _) => PollFringeFallback();
        _hoverFallback.Start();
    }

    private void OnPointerFringeCross(bool inside)
    {
        if (_disposed) return;
        if (inside) HoverDetected();
        else PointerLeftFringe();
    }
}
