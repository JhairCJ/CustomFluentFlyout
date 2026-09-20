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

    /// <summary>
    /// Tolerancia al abandonar el Island expandido (001 MOD RF-4): segundos de
    /// espera a que el puntero vuelva antes de replegarse. Un roce al cambiar de
    /// contenido o pasar de camino a otra ventana no debe cerrar la tarjeta.
    /// </summary>
    private const int HoverLeaveGraceMs = 2000;

    private void Box_MouseEnter(object sender, MouseEventArgs e) => HoverDetected();

    /// <summary>
    /// Detección de puntero (001 MOD RF-3, RF-10): el hover SOLO produce el
    /// micro-crecimiento vivo; abrir contenido exige un clic explícito
    /// (HandleIslandClick). La zona de detección se mantiene, pero por sí sola
    /// nunca despliega contenido. T2: hover sobre inactivo no redespliega hasta
    /// nuevo activo o clic (001 MOD RF-4, 002 MOD RF-7). La dispara la
    /// notificación nativa de entrada a la franja (o su fallback de 250 ms).
    /// </summary>
    private void HoverDetected()
    {
        // El puntero volvió: se descarta la espera del repliegue en curso. Va
        // antes de cualquier salida temprana porque el gesto del usuario es el
        // mismo aunque el Island esté a punto de replegarse (001 MOD RF-4).
        CancelHoverLeave();
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
    /// ¿El puntero está dentro de la franja de detección? Prueba de rectángulo
    /// contra la geometría cacheada (001 MOD RF-12): sin enumerar monitores, sin
    /// consultar multimedia y sin despertar nada. La usa el hook nativo y el
    /// fallback de 250 ms (001 MOD RF-3).
    /// </summary>
    private bool FringeContains(int x, int y)
    {
        if (_disposed || !SettingsManager.Current.IslandEnabled || Suppressed()) return false;
        var primary = PrimaryMonitor();
        if (primary.monitorArea.Width == 0) return false;
        double tolH = HoverTolH * primary.dpiX / 96.0;
        double tolV = HoverTolV * primary.dpiY / 96.0;
        double halfRaw = LineFullWidth * 0.5 * primary.dpiX / 96.0 + tolH;
        double cx = primary.workArea.Left + primary.workArea.Width / 2;
        if (Math.Abs(x - cx) > halfRaw) return false;
        double lineTop = primary.workArea.Top + (IsNotch ? 1 : Math.Clamp(SettingsManager.Current.IslandLineTopOffset, 0, 60)) * primary.dpiY / 96.0;
        return y >= primary.monitorArea.Top - 2 && y <= lineTop + 3 + tolV;
    }

    /// <summary>
    /// El puntero salió de la franja: solo se apaga el micro-crecimiento; el
    /// repliegue real lo decide la reconciliación, que re-comprueba si el puntero
    /// sigue sobre la caja o su franja (sin falsos cierres al pasar de la franja
    /// a la caja).
    /// </summary>
    private void PointerLeftFringe()
    {
        if (_inactiveHot)
        {
            _inactiveHot = false;
            if (AnimationsEnabled) EnsureLoop();
            else { _inactiveHotT = 0; ApplyFrame(); }
        }
        PostActivity(IslandActivityReason.Pointer);
    }

    /// <summary>
    /// Fallback acotado de la franja (001 MOD RF-3): solo corre si el hook nativo
    /// no está disponible; detecta cruces cada 250 ms y NUNCA consulta el gestor
    /// multimedia. El clic abre igual, porque la franja sigue siendo hit-testeable.
    /// </summary>
    private void PollFringeFallback()
    {
        if (_disposed || Suppressed()) return;
        if (!SettingsManager.Current.IslandEnabled) return;
        if (!NativeMethods.GetCursorPos(out var p)) return;
        bool inside = FringeContains(p.X, p.Y);
        if (inside == _fallbackInside) return;
        _fallbackInside = inside;
        if (inside) HoverDetected();
        else PointerLeftFringe();
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
        if (IsLeavingTowardTopEdge()) return; // gracia hacia el borde: se repliega al salir de verdad
        LeaveHover();
    }

    // ------------------------------------------------------------------
    // Tolerancia al abandonar el expandido (001 MOD RF-4)
    // ------------------------------------------------------------------

    /// <summary>
    /// Espera del repliegue por puntero: al salir del expandido NO se repliega de
    /// inmediato. Se espera <see cref="HoverLeaveGraceMs"/> a que el puntero vuelva
    /// —mover el ratón a otra ventana, pasar por encima de camino a otro sitio o
    /// un roce al cambiar de contenido no deben cerrar la tarjeta— y solo si no
    /// vuelve se resuelve el repliegue. La espera se arma UNA vez: mientras está
    /// pendiente, los avisos de la reconciliación no la prolongan ni la reinician.
    /// </summary>
    private void ArmHoverLeave()
    {
        if (_disposed || !_expanded || _drag || _reelDragging) return;
        if (_hoverLeavePending) return;
        _hoverLeavePending = true;
        int version = ++_hoverLeaveVersion;
        _ = Task.Delay(HoverLeaveGraceMs).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            if (_disposed || version != _hoverLeaveVersion) return;
            _hoverLeavePending = false;
            // El puntero está otra vez sobre el Island (o hay una interacción en
            // curso): la tarjeta se queda como estaba.
            if (!_expanded || _drag || _reelDragging || IsMouseOverBoxOrStrip()) return;
            if (Suppressed()) { HidePerMode(); return; }
            CollapseFromHover();
        }));
    }

    /// <summary>
    /// Cancela la espera vigente (el puntero volvió). El contador de versión
    /// invalida el disparo programado, así que no queda ninguna cadena viva.
    /// </summary>
    private void CancelHoverLeave()
    {
        if (!_hoverLeavePending) return;
        _hoverLeavePending = false;
        _hoverLeaveVersion++;
    }

    /// <summary>
    /// Entrada del repliegue por puntero con tolerancia: arma la espera en vez de
    /// replegar. La usan la salida del ratón, la rueda hacia arriba y la
    /// reconciliación cuando el puntero ya no está encima.
    /// </summary>
    private void LeaveHover() => ArmHoverLeave();

    // Cursor saliendo por arriba hacia el borde (hueco entre borde e isla): no colapsar,
    // si no la notificación de entrada a la franja lo re-expande y se ve encoger-crecer.
    private bool IsLeavingTowardTopEdge()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var p)) return false;
            var primary = PrimaryMonitor();
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
            var primary = PrimaryMonitor();
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
        // Rueda en expandido: cambia de funcionalidad entre las usables (002 MOD
        // RF-3/RF-9); hacia arriba compacta sin cambiar de funcionalidad.
        if (_expanded && UsableFeatureCount() > 1)
        {
            if (e.OriginalSource is DependencyObject wheelSrc && (Seekbar.IsAncestorOf(wheelSrc) || TimerPresetList.IsAncestorOf(wheelSrc) || TimerConfigGrid.IsAncestorOf(wheelSrc))) return;
            if (e.Delta < 0) CycleMode();
            // Gesto explícito: la rueda repliega ya, sin la tolerancia del puntero.
            else if (e.Delta > 0) CollapseFromHover();
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
