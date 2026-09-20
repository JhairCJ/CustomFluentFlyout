// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Text;
using Windows.Media.Control;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Vigilancia del contenedor sin sondeo: supresión contextual cacheada y
/// ecualizador condicionado a reproducción visible.
///
/// <para>El antiguo latido de 200 ms desapareció (change
/// island-actividad-orientada-eventos, 001 MOD RF-1/RF-16): la actividad llega
/// por el buzón de <c>IslandWindow.Activity.cs</c>, la supresión se recalcula
/// solo al recibir un evento de Windows (001 MOD RF-12) y el ecualizador corre
/// únicamente con media visible reproduciéndose (001 MOD RF-14).</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// Supresión contextual vigente (001 RF-8/14, 001 MOD RF-12): se sirve desde
    /// la instantánea cacheada; solo se recalcula por evento de Windows o en la
    /// recuperación de 5 s, nunca en cada ciclo.
    /// </summary>
    private bool Suppressed() => _ctxValid ? _ctxSuppressed : ComputeSuppressed();

    /// <summary>
    /// Cálculo real de la supresión (pantalla completa o una ventana que cubre
    /// todo el monitor principal apagan el Island; el escritorio Progman/WorkerW
    /// no cuenta como aplicación). Es una consulta impura: se llama solo al
    /// refrescar la instantánea de contexto.
    /// </summary>
    private bool ComputeSuppressed()
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
                var primary = PrimaryMonitor();
                if (primary.monitorArea.Width != 0 && r.Left <= primary.monitorArea.Left && r.Top <= primary.monitorArea.Top && r.Right >= primary.monitorArea.Right && r.Bottom >= primary.monitorArea.Bottom)
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Arranque/parada del visualizador de audio y su número de barras. Solo se
    /// llama desde la reconciliación y desde los cambios de contenido: el
    /// ecualizador es el mayor trabajo de idle y no debe latir (001 MOD RF-14).
    /// </summary>
    private void SyncEq()
    {
        bool want = EqShouldRun();
        if (want && !_eqRunning) { _eqRunning = true; _eqBars = -1; _eq.Start(); }
        else if (!want && _eqRunning) { _eqRunning = false; _eq.Stop(); }
        int bars = Math.Clamp(SettingsManager.Current.IslandEqBarCount, 1, 10);
        if (_eqRunning && bars != _eqBars) { _eqBars = bars; _eq.ResizeBarList(bars); }
    }

    /// <summary>
    /// ¿Debe correr el ecualizador? (001 MOD RF-14): MIENTRAS hay media visible
    /// reproduciéndose, con el audio real; SI el Island está inactivo, pausado,
    /// suprimido o en reposo, se mantiene detenido. La consulta al gestor
    /// multimedia solo ocurre cuando la sesión presentada no consta reproduciendo.
    /// </summary>
    private bool EqShouldRun()
    {
        if (!SettingsManager.Current.IslandEnabled || !SettingsManager.Current.IslandEqEnabled) return false;
        if (Suppressed() && !HasExclusive()) return false;
        // Reposo inactivo: sin contenido visible no hay nada que animar.
        if (_inactiveShown || _inactiveTt == 1 || _inactiveT >= 1) return false;
        if (!IsBoxShown) return false;
        // Solo con la capa musical delante: con timer, cajón, estante o calendario
        // a la vista el visualizador no está en pantalla.
        if (_contentMode != IslandContentMode.Media) return false;
        if (_timer.State == IslandTimerState.Alerting) return false;
        if (!MediaContentAvailable()) return false;
        return _music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            || NewestPlaying() != null;
    }
}
