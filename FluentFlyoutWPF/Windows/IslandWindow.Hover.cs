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
/// Detección del puntero sobre el Island: hover vivo (micro-crecimiento),
/// franja de tolerancia alrededor del borde superior, veto de repliegue
/// mientras el cursor estorba y los clics de apertura.
///
/// <para>El hover SOLO produce el micro-crecimiento: abrir contenido exige un
/// clic explícito (001 MOD RF-3, RF-10). La tolerancia horizontal/vertical es
/// configurable (<c>IslandHoverTolerance*</c>) y se mide en DIPs del monitor
/// principal, escalada por su DPI.</para>
/// </summary>
public partial class IslandWindow
{
    private int HoverTolH => Math.Clamp(SettingsManager.Current.IslandHoverToleranceHorizontal < 0 ? 12 : SettingsManager.Current.IslandHoverToleranceHorizontal, 0, 80);
    private int HoverTolV => Math.Clamp(SettingsManager.Current.IslandHoverToleranceVertical < 0 ? 4 : SettingsManager.Current.IslandHoverToleranceVertical, 0, 40);

    private void Box_MouseEnter(object sender, MouseEventArgs e) => HoverDetected();

    /// <summary>
    /// Detección de puntero (001 MOD RF-3, RF-10): el hover SOLO produce el
    /// micro-crecimiento vivo; abrir contenido exige un clic explícito
    /// (HandleIslandClick). La zona de detección se mantiene, pero por sí sola
    /// nunca despliega contenido. T2: hover sobre inactivo no redespliega hasta
    /// nuevo activo o clic (001 MOD RF-4, 002 MOD RF-7).
    /// </summary>
    private void HoverDetected()
    {
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        if (_expanded || _drag || _reelDragging) return;
        // T2: si ya estamos en reposo inactivo/nada por falta de activa vigente
        // en Visible mientras activo, el hover NO redespliega compacto.
        if (_inactiveShown) { /* solo micro-crecimiento, ya lo hace abajo */ }
        else if (SettingsManager.Current.IslandVisibilityMode == 0 && ResolveActiveVigenteForVisible() == null)
        {
            // Sin activa vigente en Visible: no re-desplegar por hover.
            // Se permite solo micro-crecimiento si ya hay caja visible.
            if (!IsBoxShown) return;
        }
        // Anti-reapertura: el usuario acaba de ocultar la caja con el cursor
        // encima; la detección no debe devolverle contenido de inmediato.
        if (DateTime.UtcNow < _hoverSnoozeUntil) return;
        if (!_inactiveHot)
        {
            _inactiveHot = true;
            if (AnimationsEnabled) EnsureLoop();
            else { _inactiveHotT = 1; ApplyFrame(); }
        }
    }

    /// <summary>
    /// Franja de detección pegada al borde superior del monitor: cubre el hueco
    /// entre el borde físico y la línea/isla para que acercar el cursor al borde
    /// despierte el hover (y, con ello, el micro-crecimiento) sin exigir puntería.
    /// </summary>
    private void PollFringeHover()
    {
        if (_expanded || _drag) return;
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        if (!NativeMethods.GetCursorPos(out var p)) return;
        var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
        if (primary.monitorArea.Width == 0) return;
        double tolH = HoverTolH * primary.dpiX / 96.0;
        double tolV = HoverTolV * primary.dpiY / 96.0;
        double halfRaw = LineFullWidth * 0.5 * primary.dpiX / 96.0 + tolH;
        double cx = primary.workArea.Left + primary.workArea.Width / 2;
        if (Math.Abs(p.X - cx) > halfRaw) return;
        double lineTop = primary.workArea.Top + (IsNotch ? 1 : Math.Clamp(SettingsManager.Current.IslandLineTopOffset, 0, 60)) * primary.dpiY / 96.0;
        if (p.Y < primary.monitorArea.Top - 2 || p.Y > lineTop + 3 + tolV) return;
        HoverDetected();
    }

    private void Box_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_inactiveHot)
        {
            _inactiveHot = false;
            if (AnimationsEnabled) EnsureLoop();
            else { _inactiveHotT = 0; ApplyFrame(); }
        }
        if (_drag || _reelDragging || Mouse.LeftButton == MouseButtonState.Pressed) return;
        if (IsLeavingTowardTopEdge()) return; // gracia hacia el borde: Tick colapsa al salir de verdad
        LeaveHover();
    }

    // Cursor saliendo por arriba hacia el borde (hueco entre borde e isla): no colapsar,
    // si no el poll de franja lo re-expande a los ~150ms y se ve encoger-crecer.
    private bool IsLeavingTowardTopEdge()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var p)) return false;
            var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
            if (primary.monitorArea.Width == 0) return false;
            double islandOff = (IsNotch ? 0 : Math.Clamp(SettingsManager.Current.IslandTopOffset, 0, 80)) * primary.dpiY / 96.0;
            double islandTop = primary.workArea.Top + islandOff;
            if (p.Y < primary.workArea.Top - 2 || p.Y > islandTop + 2) return false;
            double halfW = ((_expanded || _p > 0.2) ? ContentExpandedWidth : LineFullWidth) * 0.5;
            halfW = halfW * primary.dpiX / 96.0 + HoverTolH * primary.dpiX / 96.0;
            double cx = primary.workArea.Left + primary.workArea.Width / 2;
            return Math.Abs(p.X - cx) <= halfW;
        }
        catch { return false; }
    }

    /// <summary>
    /// ¿El cursor está sobre la caja, la franja o la zona de tolerancia vigente?
    /// Se usa como veto de repliegue: mientras el puntero estorbe, el aviso
    /// temporal no se cierra ni el hover se rearma a medias.
    /// </summary>
    private bool IsMouseOverBoxOrStrip()
    {
        try
        {
            if (IsMouseOver) return true;
            if (IslandBox.IsMouseOver || HoverStrip.IsMouseOver) return true;
            if (!NativeMethods.GetCursorPos(out var p)) return false;
            var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
            if (primary.monitorArea.Width == 0) return false;
            double tolH = HoverTolH * primary.dpiX / 96.0;
            double tolV = HoverTolV * primary.dpiY / 96.0;
            double cx = primary.workArea.Left + primary.workArea.Width / 2;
            double halfW = (_expanded || _p > 0.2)
                ? ContentExpandedWidth * 0.5 * primary.dpiX / 96.0 + tolH
                : LineFullWidth * 0.5 * primary.dpiX / 96.0 + tolH;
            if (Math.Abs(p.X - cx) > halfW) return false;
            if (_expanded || _p > 0.2)
            {
                double top = primary.workArea.Top;
                double islandOff = (IsNotch ? 0 : Math.Clamp(SettingsManager.Current.IslandTopOffset, 0, 80)) * primary.dpiY / 96.0;
                double bottom = top + (_hexp + 8) * primary.dpiY / 96.0 + islandOff + tolV;
                if (p.Y < top - 2 || p.Y > bottom) return false;
                return true;
            }
            double lineTop = primary.workArea.Top + (IsNotch ? 1 : Math.Clamp(SettingsManager.Current.IslandLineTopOffset, 0, 60)) * primary.dpiY / 96.0;
            if (p.Y < primary.monitorArea.Top - 2 || p.Y > lineTop + 3 + tolV) return false;
            return true;
        }
        catch { return false; }
    }

    // --- clics de apertura ---

    // --- pieza inactiva (001 MOD RF-16): hover micro-crece, clic abre la usable ---
    private void InactiveZone_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        // El clic completa el crecimiento y abre la última usable; sin usable
        // no abre vista vacía (001 MOD RF-3, RF-9, RF-16).
        if (!ExpandLastUsable())
            _hoverSnoozeUntil = DateTime.UtcNow.AddSeconds(TimerReshowSnoozeSeconds);
    }

    /// <summary>
    /// Clic en cualquier zona del island que no sea un control específico:
    /// expande la última usable (001 MOD RF-3). Título, álbum, ecualizador y
    /// reels tienen sus propios manejadores.
    /// </summary>
    private void HandleIslandClick(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        e.Handled = true;
        ExpandLastUsable();
    }

    // Clic en el título del compacto: abre el expandido de la última usable
    // (001 MOD RF-3); el burbujeo lo resolvería igual, pero se marca a mano.
    private void CompactMiddle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        e.Handled = true;
        ExpandLastUsable();
    }

    private void IslandBox_Wheel(object sender, MouseWheelEventArgs e)
    {
        // Temporizador: en expandido la rueda cambia de funcionalidad (002 MOD RF-3);
        // hacia arriba compacta sin cambiar de funcionalidad.
        if (_expanded && TimerModeAvailable())
        {
            if (e.OriginalSource is DependencyObject wheelSrc && (Seekbar.IsAncestorOf(wheelSrc) || TimerPresetList.IsAncestorOf(wheelSrc) || TimerConfigGrid.IsAncestorOf(wheelSrc))) return;
            if (e.Delta < 0) CycleMode();
            else if (e.Delta > 0) LeaveHover();
            e.Handled = true;
            return;
        }
        if (_expanded) return;
        // En compacto, la rueda hacia abajo es selección explícita de contenido
        // (equivale al clic, 001 MOD RF-3); hacia arriba ya está compacto.
        if (Math.Clamp(SettingsManager.Current.IslandExpandTrigger, 0, 2) is not (1 or 2)) return;
        if (e.OriginalSource is DependencyObject src && Seekbar.IsAncestorOf(src)) return;
        if (e.Delta < 0)
        {
            ExpandLastUsable();
            e.Handled = true;
        }
    }
}
