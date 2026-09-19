// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls.TaskbarWidget;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text;
using Windows.Media.Control;
using static WindowsMediaController.MediaManager;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Vigilancia periódica del contenedor: el tick de reconciliación (supresión,
/// sesión presentada, ecualizador, cuenta del temporizador y repliegues que no
/// traen evento propio) y la detección de supresión contextual.
///
/// <para>El tick es la red de seguridad del contrato de actividad: si algo entra
/// en actividad sin un evento que lo anuncie, el compacto aparece solo
/// (001 MOD RF-4, RF-24).</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// Latido del contenedor cada <see cref="TickIntervalMs"/> ms: aplica
    /// supresión, concilia el snapshot musical, resuelve reposo/actividad sin
    /// evento, mantiene viva la cuenta del temporizador y avanza el seek del
    /// expandido. Cadencia corta a propósito: la actividad que no trae evento
    /// propio (001 MOD RF-4) no debe tardar medio segundo en verse.
    /// </summary>
    private void Tick()
    {
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
        SyncExistingMediaState();
        // «Visible mientras activo»: si algo entra en actividad sin un evento que
        // lo anuncie (actividad leída en el tick, sesión adoptada tarde), el
        // compacto aparece solo, igual que si el evento hubiera llegado: el reposo
        // nunca tapa lo que está activo (001 MOD RF-4, RF-24). La pieza se
        // reabre solo cuando ya es la vista asentada: a mitad de un repliegue
        // deliberado (fase 1 del repliegue en dos fases) el reposo es tránsito y
        // no debe abortarse (001 MOD RF-16).
        if (AtInactiveRest && SettingsManager.Current.IslandVisibilityMode == 0
            && _timer.State != Classes.IslandTimerState.Alerting
            && DateTime.UtcNow >= _hoverSnoozeUntil)
            TryReopenFromInactive();
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        if (_expanded && !_drag && !_reelDragging && !IsMouseOverBoxOrStrip()) LeaveHover();
        SyncEq();
        _timer.Poll(DateTime.UtcNow);
        UpdateArrows();
        if (_contentMode == IslandContentMode.Timer && IsBoxShown)
        {
            RefreshTimerUI();
            EnsureTimerContentShown();
        }
        // El calendario se repinta con el latido: sus cuentas atrás («en 4 min») se
        // recalculan al pintar, así que envejecen solas sin temporizador propio.
        if (_contentMode == IslandContentMode.Calendar && IsBoxShown) RefreshCalendarList();
        var s = Current();
        if (s != null && _expanded) UpdateSeek(s);
        UpdateLine();
    }

    /// <summary>
    /// Supresión contextual (001 RF-8/14): pantalla completa o una ventana que
    /// cubre todo el monitor principal apagan el Island; el escritorio
    /// (Progman/WorkerW) no cuenta como aplicación.
    /// </summary>
    private bool Suppressed()
    {
        if (FullscreenDetector.IsFullscreenApplicationRunning()) return true;
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && NativeMethods.GetWindowRect(fg, out var r))
            {
                // ponytail: el escritorio (Progman/WorkerW) cubre todo el monitor pero no es una app.
                var sb = new StringBuilder(256);
                if (NativeMethods.GetClassName(fg, sb, sb.Capacity) > 0)
                {
                    string cls = sb.ToString();
                    if (cls == "Progman" || cls == "WorkerW") return false;
                }
                var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
                if (primary.monitorArea.Width != 0 && r.Left <= primary.monitorArea.Left && r.Top <= primary.monitorArea.Top && r.Right >= primary.monitorArea.Right && r.Bottom >= primary.monitorArea.Bottom)
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Arranque/parada del visualizador de audio y su número de barras.</summary>
    private void SyncEq()
    {
        bool want = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandEqEnabled;
        if (want && !_eqRunning) { _eqRunning = true; _eqBars = -1; _eq.Start(); }
        else if (!want && _eqRunning) { _eqRunning = false; _eq.Stop(); }
        int bars = Math.Clamp(SettingsManager.Current.IslandEqBarCount, 1, 10);
        if (_eqRunning && bars != _eqBars) { _eqBars = bars; _eq.ResizeBarList(bars); }
    }
}
