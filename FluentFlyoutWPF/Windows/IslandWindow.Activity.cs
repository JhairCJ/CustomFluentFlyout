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
/// Motivos de actividad que pueden despertar al Island. Son combinables: una
/// ráfaga (metadata + playback + timer + contexto) se coalesce en UNA
/// reconciliación con todos los motivos y el último estado (change
/// island-actividad-orientada-eventos, 001 MOD RF-1; 002 MOD RF-13).
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
    /// <summary>Red lenta de recuperación: cubre eventos perdidos sin sondeo.</summary>
    Recovery = 1 << 6,
    /// <summary>Dispositivos Bluetooth: conexión y refinamiento de su batería.</summary>
    Bluetooth = 1 << 7,
    /// <summary>Portapapeles: llegó o se fue una pieza copiada.</summary>
    Clipboard = 1 << 8,
    /// <summary>Clima: llegó un dato nuevo (o cambió el lugar configurado).</summary>
    Weather = 1 << 9,
    /// <summary>Cargador: se enchufó o se desenchufó el equipo.</summary>
    Power = 1 << 10,
}

/// <summary>
/// Actividad orientada a eventos y bajo consumo: sustituye el latido de 200 ms y
/// el poll de puntero de 40 ms por un buzón coalescido, cadencias limitadas por
/// contenido y una red de recuperación de 5 s.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Buzón coalescido</b> (001 MOD RF-1, ADDED RF-3): las fuentes
/// (media, timer, contexto, puntero, ajustes, calendario) publican motivos y el
/// contenedor reconcilia UNA sola vez con el último estado, sin perder eventos
/// que lleguen durante la reconciliación.</item>
/// <item><b>Cadencia por contenido</b> (001 MOD RF-16; 002 MOD RF-15): el
/// refresco de la vista solo corre mientras su contenido está en pantalla; no
/// existe ningún ciclo global de 40–200 ms.</item>
/// <item><b>Recuperación de 5 s</b> (001 MOD RF-2/RF-28, 002 MOD RF-13/RF-16):
/// un único despertador lento relee contexto, media y timer para cubrir eventos
/// perdidos, suspensión o reanudación.</item>
/// <item><b>Vencimiento con despertador propio</b> (002 MOD RF-2/RF-6): el timer
/// arma un despertador único sobre su hora objetivo y el aviso temportal
/// conserva su vencimiento por temporizador de un solo disparo.</item>
/// <item><b>Contexto por eventos de Windows</b> (001 MOD RF-12): supresión,
/// foreground, DPI y geometría se recalculan al recibir el evento y se sirven
/// desde una instantánea cacheada en las rutas calientes.</item>
/// </list>
///
/// <para>Parte del IslandWindow; la supresión y el ecualizador viven en
/// <c>IslandWindow.Monitoring.cs</c>, el puntero en <c>IslandWindow.Hover.cs</c>
/// y la máquina de estados en <c>IslandWindow.States.cs</c>.</para>
/// </summary>
public partial class IslandWindow
{
    // Red de seguridad: lo bastante lenta para no despertar nada y lo bastante
    // corta para cumplir el máximo de 5 s de recuperación (001 ADDED RF-3).
    private const int RecoveryIntervalMs = 5000;
    // Fallback de la franja cuando el hook nativo no está disponible (001 MOD RF-3).
    private const int FallbackHoverIntervalMs = 250;
    // Refresco de la vista SOLO mientras hay contenido visible (001 MOD RF-16;
    // 002 MOD RF-15): cuenta atrás del timer, seek del expandido y cuentas atrás
    // del calendario. Nunca con la vista oculta.
    private const int ViewRefreshIntervalMs = 300;

    // --- buzón de actividad ---
    private IslandActivityReason _activityReasons;
    private bool _activityScheduled;
    private bool _reconciling;

    // --- cadencias limitadas por contenido ---
    private DispatcherTimer? _recovery;
    private DispatcherTimer? _timerWake;
    private DispatcherTimer? _viewRefresh;
    private DispatcherTimer? _hoverFallback;
    private bool _fallbackInside;
    private IslandPointerHook? _pointerHook;

    // --- instantánea de contexto (001 MOD RF-12) ---
    private MonitorUtil.MonitorInfo _ctxPrimary;
    private bool _ctxSuppressed;
    private bool _ctxValid;
    private bool _ctxRefreshing;
    private IntPtr _foregroundHook = IntPtr.Zero;
    private NativeMethods.WinEventProc? _foregroundProc;

    private void InitActivity()
    {
        RefreshContextSnapshot();

        // Red de recuperación: única vigilancia permanente del contenedor, a 5 s.
        _recovery = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(RecoveryIntervalMs),
        };
        _recovery.Tick += OnRecoveryTick;
        _recovery.Start();

        InstallPointerHook();
        InstallForegroundHook();
        try
        {
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Island: no se pudieron registrar los eventos del sistema; se usa la recuperación de 5 s");
        }

        // Red de seguridad al cerrar: ningún callback del hook sobrevive a la
        // ventana, aunque el cierre no pase por Dispose (001 MOD RF-3).
        Closed += (_, _) => ShutdownActivity();
    }

    /// <summary>
    /// Suelta cualquier recurso de actividad: sin temporizadores vivos y sin
    /// callbacks después del cierre (001 MOD RF-3; 002 MOD RF-13).
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

        _pointerHook?.Dispose();
        _pointerHook = null;

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
    // Buzón coalescido (001 MOD RF-1; 002 MOD RF-13)
    // ------------------------------------------------------------------

    /// <summary>
    /// Publica un motivo de actividad. Las ráfagas se acumulan y producen UNA
    /// sola reconciliación en el mismo ciclo de UI, con el último estado: los
    /// eventos intermedios no se pierden ni se pintan.
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
            // Se drena el buzón completo: los cambios que lleguen DURANTE la
            // reconciliación se atienden en la misma pasada con el último estado.
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
            Logger.Error(ex, "Island: error al reconciliar la actividad");
        }
        finally
        {
            _reconciling = false;
        }
    }

    /// <summary>
    /// Reconciliación única del contenedor con el último estado: supresión,
    /// presentación de la actividad vigente, hover, ecualizador, flechas y vista
    /// solo si está en pantalla. Es el antiguo latido vaciado de sondeo: no
    /// consulta nada que no deba y jamás corre por reloj propio.
    /// </summary>
    private void ReconcileCore(IslandActivityReason reasons)
    {
        // Cada pasada lee el sistema multimedia de cero (una sola vez por pasada): los
        // memos de lectura mueren aquí, así que ninguna decisión se toma con un dato de
        // la pasada anterior y la recuperación de 5 s sigue viendo un estado fresco.
        InvalidateMediaReads();
        if ((reasons & IslandActivityReason.Context) != 0 || !_ctxValid)
            RefreshContextSnapshot();

        if (!SettingsManager.Current.IslandEnabled)
        {
            _wasSuppressed = false;
            SnapHidden();
            Visibility = Visibility.Collapsed;
            return;
        }

        // Exclusiva persistente (002 RF-2) atraviesa supresión (001 RF-8/14).
        if (Suppressed() && !HasExclusive())
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
            // Al salir de supresión, la exclusiva ya estaba visible si atravesó.
            if (HasExclusive() && IsBoxShown) { }
            else if (_pendingTimerAlert && SettingsManager.Current.IslandEnabled) { _pendingTimerAlert = false; ShowTimerAlert(); }
            else if (TimerKeepsAlive()) ShowTimerCompact();
            else
                RefreshVisibilityState();
        }
        else if (Suppressed() && HasExclusive() && !IsBoxShown)
        {
            // Exclusiva llegó estando suprimido: desplegarla aunque siga la supresión.
            ShowTimerAlert();
        }

        // Vencimiento del timer: recuperación de eventos perdidos y rearme del
        // despertador único (002 MOD RF-2/RF-6/RF-13).
        if ((reasons & IslandActivityReason.Timer) != 0)
            PollTimerSafely();

        // «Visible mientras activo»: si algo entra en actividad sin un evento que
        // lo anuncie, el compacto aparece solo (001 MOD RF-4, RF-24). La pieza se
        // reabre solo cuando ya es la vista asentada (001 MOD RF-16).
        if ((reasons & (IslandActivityReason.Media | IslandActivityReason.Timer | IslandActivityReason.Recovery)) != 0)
        {
            if (AtInactiveRest && SettingsManager.Current.IslandVisibilityMode == 0
                && _timer.State != IslandTimerState.Alerting
                && DateTime.UtcNow >= _hoverSnoozeUntil)
                TryReopenFromInactive();

            if ((reasons & IslandActivityReason.Media) != 0)
                ReconcileMediaState();
            else if ((reasons & IslandActivityReason.Recovery) != 0)
                ReconcileMediaState(recoveryOnly: true);
        }

        // Bluetooth: una conexión presenta su aviso por sí misma (OnBluetoothConnected);
        // aquí solo se refina lo que YA está a la vista (batería que llega tarde),
        // nunca se re-despliega (change island-bluetooth-conectado RF-3/RF-5).
        if ((reasons & IslandActivityReason.Bluetooth) != 0)
            ReconcileBluetoothState();

        // Clima: un dato nuevo (o un lugar nuevo) solo repinta la vista del clima si
        // ya está delante; nunca despliega nada (change island-clima RF-3).
        if ((reasons & IslandActivityReason.Weather) != 0)
            ReconcileWeatherState();

        // Cargador: el cambio de estado presenta su aviso por sí mismo
        // (OnChargerConnected/OnChargerDisconnected); aquí solo se repinta si su
        // aviso sigue delante (change island-cargador).
        if ((reasons & IslandActivityReason.Power) != 0)
            ReconcilePowerState();

        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
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
    /// Refresca solo lo visible: cuenta atrás del timer, seek del expandido y
    /// cuentas atrás del calendario. El temporizador de vista vive únicamente
    /// mientras su vista está en pantalla; apagarlo no toca la cuenta (002 MOD
    /// RF-15; 001 MOD RF-14/RF-16).
    /// </summary>
    private void UpdateVisibleRefresh()
    {
        bool want = !_disposed && SettingsManager.Current.IslandEnabled && IsBoxShown
            && (_contentMode switch
            {
                IslandContentMode.Timer => TimerModeAvailable(),
                IslandContentMode.Calendar => CalendarModeAvailable(),
                // El seek del expandido solo se pinta con la vista de media delante.
                IslandContentMode.Media => _expanded && MusicContentShown(),
                // El expandido de una pantalla combinada envejece sus cuentas
                // (temporizador, calendario) y su seek. Replegada no pasa por aquí:
                // enseña UNA funcionalidad y la cadencia la pone su ruta.
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
            // Las cuentas atrás («en 4 min») se recalculan al pintar: repintar
            // mientras la vista está en pantalla las envejece solas, sin latido.
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
    // Despertador único del timer y recuperación (002 MOD RF-2/RF-6/RF-13)
    // ------------------------------------------------------------------

    /// <summary>
    /// Notificación de cambio de estado del timer (iniciar, pausar, reanudar,
    /// reiniciar, cancelar, vencer): rearma el despertador de vencimiento, publica
    /// la actividad y refresca solo lo visible. La cuenta no depende de latido.
    /// </summary>
    private void OnTimerChanged()
    {
        ArmTimerWake();
        PostActivity(IslandActivityReason.Timer);
        UpdateVisibleRefresh();
    }

    /// <summary>
    /// Rearma el despertador ÚNICO sobre la hora objetivo del timer. Con la
    /// cuenta parada se apaga; sin él no habría vencimiento (002 MOD RF-6). Tras
    /// una suspensión, la recuperación de 5 s lo rearma o lo cumple.
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
                // La hora objetivo manda: al vencer se comprueba contra el reloj.
                _timer.Poll(DateTime.UtcNow);
            };
        }
        _timerWake.Stop();
        _timerWake.Interval = left + TimeSpan.FromMilliseconds(40);
        _timerWake.Start();
    }

    /// <summary>
    /// Comprobación de vencimiento segura: si el timer sigue corriendo y ya pasó
    /// su hora objetivo (p. ej. tras suspender el equipo), lo cumple aquí.
    /// </summary>
    private void PollTimerSafely()
    {
        if (_timer.State != IslandTimerState.Running) { ArmTimerWake(); return; }
        if (_timer.Remaining > TimeSpan.Zero) { ArmTimerWake(); return; }
        _timer.Poll(DateTime.UtcNow);
    }

    // ------------------------------------------------------------------
    // Recuperación lenta de eventos perdidos (001 MOD RF-2/RF-28; 002 RF-13)
    // ------------------------------------------------------------------

    private void OnRecoveryTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // Contexto impuro (foreground/fullscreen/DPI) solo aquí y por evento.
        _ctxValid = false;
        PostRecovery();
        // Aviso temporal: si su disparo único se perdió, se cumple aquí en ≤5 s.
        if (!_noticeCheckActive && _noticeUntil != DateTime.MinValue && _noticeUntil <= DateTime.UtcNow)
            RetractTemporaryNotice(_noticeVersion);
    }

    /// <summary>
    /// Publica una pasada de recuperación: relee contexto, media, timer y
    /// calendario SIN el motivo de media. Así la recuperación solo actúa sobre lo
    /// que apareció sin evento y jamás re-despliega una vista que el usuario ya
    /// tenía decidida (001 MOD RF-2/RF-28).
    /// </summary>
    private void PostRecovery()
    {
        PostActivity(IslandActivityReason.Context | IslandActivityReason.Recovery
            | IslandActivityReason.Timer | IslandActivityReason.Calendar);
    }

    // ------------------------------------------------------------------
    // Contexto y geometría por eventos de Windows (001 MOD RF-12)
    // ------------------------------------------------------------------

    /// <summary>
    /// Recalcula la instantánea de contexto: monitor primario (área, área de
    /// trabajo y DPI) y supresión vigente. Las rutas calientes leen la instantánea
    /// en vez de consultar Windows.
    /// </summary>
    private void RefreshContextSnapshot()
    {
        // Guarda contra recursión: ComputeSuppressed consulta PrimaryMonitor y
        // esta, al refrescar, volvería a entrar aquí.
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
    /// Monitor primario cacheado (001 MOD RF-12): sin enumerar monitores en cada
    /// cruce de puntero o reposicionamiento. Si la instantánea no es válida, la
    /// recalcula.
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
    /// Cambió la ventana en primer plano: puede cambiar la supresión (una ventana
    /// que cubre el monitor) sin ningún sondeo (001 MOD RF-12).
    /// </summary>
    private void OnForegroundEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND) return;
        _ctxValid = false;
        PostActivity(IslandActivityReason.Context);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            _ctxValid = false;
            RefreshContextSnapshot();
            PositionTopCenter();
            RefreshAppearance();
            PostActivity(IslandActivityReason.Context | IslandActivityReason.Recovery);
        }));

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
    /// Reanudación del equipo o desbloqueo: la hora objetivo del timer manda, así
    /// que se rearma su despertador y se recupera todo lo que pudo perderse,
    /// incluido el contexto (001 MOD RF-2/RF-28; 002 MOD RF-6/RF-13).
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
    // Notificación nativa de puntero con fallback (001 MOD RF-3)
    // ------------------------------------------------------------------

    private void InstallPointerHook()
    {
        var hook = new IslandPointerHook(FringeContains, OnPointerFringeCross);
        if (hook.Install())
        {
            _pointerHook = hook;
            return;
        }
        hook.Dispose();
        Logger.Warn("Island: hook de puntero no disponible; se usa la detección acotada de 250 ms sin consultar multimedia");
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
