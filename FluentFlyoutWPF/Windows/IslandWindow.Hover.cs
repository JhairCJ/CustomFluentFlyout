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
/// Pointer detection over the Island: live hover (micro-growth), a tolerance strip
/// around the top edge, a fold-back veto while the cursor is in the way, and the
/// opening clicks.
///
/// <para>Hover ONLY produces the micro-growth: opening content requires an explicit
/// click (001 MOD RF-3, RF-10). The horizontal/vertical tolerance is configurable
/// (<c>IslandHoverTolerance*</c>) and measured in DIPs of the primary monitor,
/// scaled by its DPI.</para>
///
/// <para>The edge strip is the Island's DOOR, and both its geometry and its state
/// come from a single place - <see cref="IslandLine"/> and <c>UpdateLine</c> -: the
/// strip follows the line the user sees, and with nothing visible it stops existing
/// unless an invisible door is requested (<c>IslandHiddenAccess</c>).</para>
/// </summary>
public partial class IslandWindow
{
    private int HoverTolH => Math.Clamp(SettingsManager.Current.IslandHoverToleranceHorizontal < 0 ? 12 : SettingsManager.Current.IslandHoverToleranceHorizontal, 0, 80);
    private int HoverTolV => Math.Clamp(SettingsManager.Current.IslandHoverToleranceVertical < 0 ? 4 : SettingsManager.Current.IslandHoverToleranceVertical, 0, 40);

    /// <summary>
    /// Grace when leaving the expanded Island (001 MOD RF-4): seconds to wait for the
    /// pointer to come back before folding away. A brush while content changes, or
    /// passing on the way to another window, must not close the card.
    /// </summary>
    private const int HoverLeaveGraceMs = 2000;

    private void Box_MouseEnter(object sender, MouseEventArgs e) => HoverDetected();

    /// <summary>
    /// Pointer detection (001 MOD RF-3, RF-10): hover ONLY produces the live
    /// micro-growth; opening content requires an explicit click (HandleIslandClick).
    /// The detection zone is kept, but on its own it never expands content. T2:
    /// hover over inactive does not re-expand until a new active item or a click
    /// (001 MOD RF-4, 002 MOD RF-7). Fired by the native strip-entry notification
    /// (or its 250 ms fallback).
    /// </summary>
    private void HoverDetected()
    {
        // The pointer came back: the pending fold-back wait is discarded. It goes
        // before any early exit because the user's gesture is the same even when the
        // Island is about to fold away (001 MOD RF-4).
        CancelHoverLeave();
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        // Without a live door the strip does not exist: neither micro-growth nor
        // re-expansion (the hook already filters, but the box also comes through here).
        if (!AccessZoneActive()) return;
        // Dictation wins (RF-10): with a session in progress the strip neither grows nor
        // pulls the Island out of its card. The strip is geometry - the native hook
        // and the poll detect it without hit-testing - so the veto has to live here.
        if (DictationActive()) return;
        if (_expanded || _drag || _reelDragging) return;
        // Ultra compact with the Island fully hidden: there is no piece or capsule
        // to point at, so the top strip - from the activity line to the edge - brings
        // it back EXPANDED. It is the only door left; without it the Island would only
        // peek out on the next event. The reopen veto still applies (it does not
        // resurrect itself right after the user closed it).
        if (UltraCompactPullsFromTop)
        {
            if (DateTime.UtcNow < _hoverSnoozeUntil) return;
            if (ExpandLastUsable()) return;
        }
        // T2: if we are already at inactive rest/nothing for lack of a current active item
        // in Visible-while-active, hover does NOT re-expand the compact view.
        if (_inactiveShown) { /* solo micro-crecimiento, ya lo hace abajo */ }
        else if (SettingsManager.Current.IslandVisibilityMode == 0 && ResolveActiveVigenteForVisible() == null)
        {
            // No current active item in Visible mode: do not re-expand on hover.
            // Only micro-growth is allowed if a box is already visible.
            if (!IsBoxShown) return;
        }
        // Anti-reopen: the user just hid the box with the cursor on top of it; detection
        // must not immediately give content back.
        if (DateTime.UtcNow < _hoverSnoozeUntil) return;
        if (!_inactiveHot)
        {
            _inactiveHot = true;
            if (AnimationsEnabled) EnsureLoop();
            else { _inactiveHotT = 1; ApplyFrame(); }
        }
    }

    /// <summary>
    /// Is the pointer inside the detection strip? A rectangle test against the cached
    /// geometry (001 MOD RF-12): no monitor enumeration, no media queries and nothing
    /// woken up. The native hook and the 250 ms fallback use it (001 MOD RF-3).
    /// </summary>
    private bool FringeContains(int x, int y)
    {
        if (_disposed || !SettingsManager.Current.IslandEnabled || Suppressed()) return false;
        // Without a live door the pointer does not even get here: neither hook nor fallback.
        if (!AccessZoneActive()) return false;
        var primary = PrimaryMonitor();
        if (!FringeBounds(primary, out double left, out double top, out double right, out double bottom)) return false;
        return x >= left && x <= right && y >= top && y <= bottom;
    }

    /// <summary>
    /// The strip's band in PHYSICAL monitor pixels: from where to where the pointer
    /// belongs to the handle. It is the strip's ONLY geometry - used by the native
    /// detection, the 250 ms fallback and the fold-back veto - and its offset comes
    /// from the SAME rule that paints the line (<see cref="IslandLine.TopDip"/>), so
    /// the zone cannot end up above or below the stripe the user sees.
    /// </summary>
    private bool FringeBounds(MonitorUtil.MonitorInfo primary,
        out double left, out double top, out double right, out double bottom)
    {
        left = top = right = bottom = 0;
        if (primary.monitorArea.Width == 0) return false;
        double tolH = HoverTolH * primary.dpiX / 96.0;
        double tolV = HoverTolV * primary.dpiY / 96.0;
        double half = IslandLine.BarWidth * 0.5 * primary.dpiX / 96.0 + tolH;
        double cx = primary.workArea.Left + primary.workArea.Width / 2.0;
        // The window starts at the work-area edge (PositionTopCenter): the strip cannot
        // start higher than the Island itself, or the pointer would have to cross the
        // taskbar to wake it.
        top = primary.workArea.Top;
        bottom = top + (_lineTopDip + IslandLine.BarHeight) * primary.dpiY / 96.0 + tolV;
        left = cx - half;
        right = cx + half;
        return true;
    }

    /// <summary>
    /// The pointer left the strip: only the micro-growth is turned off; the actual
    /// fold-back is decided by reconciliation, which re-checks whether the pointer is
    /// still over the box or its strip (no false closes when moving from the strip
    /// to the box).
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
    /// Bounded strip fallback (001 MOD RF-3): it only runs when the native hook is
    /// unavailable; it detects crossings every 250 ms and NEVER queries the media
    /// manager. Clicking still opens, because the strip remains hit-testable.
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
    // Grace when leaving the expanded view (001 MOD RF-4)
    // ------------------------------------------------------------------

    /// <summary>
    /// Pointer fold-back wait: leaving the expanded view does NOT fold immediately.
    /// It waits <see cref="HoverLeaveGraceMs"/> for the pointer to come back - moving
    /// the mouse to another window, passing over on the way somewhere else, or a
    /// brush while content changes must not close the card - and only if it does not
    /// come back is the fold-back resolved. The wait is armed ONCE: while it is
    /// pending, reconciliation notices neither extend nor restart it.
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
            // The pointer is over the Island again (or an interaction is in progress): the
            // card stays as it was.
            if (!_expanded || _drag || _reelDragging || IsMouseOverBoxOrStrip()) return;
            if (Suppressed()) { HidePerMode(); return; }
            CollapseFromHover();
        }));
    }

    /// <summary>
    /// Cancels the pending wait (the pointer came back). The version counter
    /// invalidates the scheduled trigger, so no live chain is left behind.
    /// </summary>
    private void CancelHoverLeave()
    {
        if (!_hoverLeavePending) return;
        _hoverLeavePending = false;
        _hoverLeaveVersion++;
    }

    /// <summary>
    /// Pointer fold-back entry with grace: it arms the wait instead of folding. Mouse
    /// leave, the wheel up and reconciliation when the pointer is no longer on top
    /// all use it.
    /// </summary>
    private void LeaveHover() => ArmHoverLeave();

    // Cursor leaving upwards towards the edge (the gap between edge and island): do not
    // collapse, otherwise the strip-entry notification re-expands it and you see it shrink-grow.
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
            double halfW = ((_expanded || _p > 0.2) ? ContentExpandedWidth : IslandLine.BarWidth) * 0.5;
            halfW = halfW * primary.dpiX / 96.0 + HoverTolH * primary.dpiX / 96.0;
            double cx = primary.workArea.Left + primary.workArea.Width / 2;
            return Math.Abs(p.X - cx) <= halfW;
        }
        catch { return false; }
    }

    /// <summary>
    /// Is the cursor over the box, the strip or the current tolerance zone? It is
    /// used as a fold-back veto: while the pointer is in the way, the temporary notice
    /// does not close and hover does not re-arm halfway.
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
            // Expanded: the zone is the open box, with its real measured height.
            if (_expanded || _p > 0.2)
            {
                double expandedHalf = ContentExpandedWidth * 0.5 * primary.dpiX / 96.0 + tolH;
                if (Math.Abs(p.X - cx) > expandedHalf) return false;
                double top = primary.workArea.Top;
                double islandOff = (IsNotch ? 0 : Math.Clamp(SettingsManager.Current.IslandTopOffset, 0, 80)) * primary.dpiY / 96.0;
                double bottom = top + (_hexp + 8) * primary.dpiY / 96.0 + islandOff + tolV;
                return p.Y >= top - 2 && p.Y <= bottom;
            }
            // At rest the zone is the handle's strip: the same geometry as the native
            // detection, and with no door it is not "over" anything.
            if (!AccessZoneActive()) return false;
            if (!FringeBounds(primary, out double fringeLeft, out double fringeTop, out double fringeRight, out double fringeBottom)) return false;
            return p.X >= fringeLeft && p.X <= fringeRight && p.Y >= fringeTop && p.Y <= fringeBottom;
        }
        catch { return false; }
    }

    // --- opening clicks ---

    // --- inactive piece (001 MOD RF-16): hover micro-grows, click opens the usable one ---
    private void InactiveZone_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        // With the door closed the strip does not exist: the click does nothing.
        if (!AccessZoneActive()) return;
        // The click completes the growth and opens the last usable one; with none usable
        // it does not open an empty view (001 MOD RF-3, RF-9, RF-16).
        if (!ExpandLastUsable())
            _hoverSnoozeUntil = DateTime.UtcNow.AddSeconds(TimerReshowSnoozeSeconds);
    }

    /// <summary>
    /// Click anywhere on the island that is not a specific control: it expands the
    /// last usable one (001 MOD RF-3). Title, album, equalizer and reels have their
    /// own handlers.
    /// </summary>
    private void HandleIslandClick(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        e.Handled = true;
        ExpandLastUsable();
    }

    // Click on the compact view's title: it opens the expanded view of the last
    // usable one (001 MOD RF-3); bubbling would resolve it the same way, but it is
    // marked by hand.
    private void CompactMiddle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        e.Handled = true;
        ExpandLastUsable();
    }

    private void IslandBox_Wheel(object sender, MouseWheelEventArgs e)
    {
        // Hit-testing is already off during dictation (RF-10); this closes the door for
        // good in case the event arrives another way.
        if (DictationActive()) return;
        // Wheel while expanded: it moves to the next SCREEN with something usable
        // (002 MOD RF-3/RF-9; change island-pantallas RF-4: the unit is the screen);
        // up it compacts without changing screen.
        if (_expanded && UsableScreenCount() > 1)
        {
            if (e.OriginalSource is DependencyObject wheelSrc && (Seekbar.IsAncestorOf(wheelSrc) || TimerPresetList.IsAncestorOf(wheelSrc) || TimerConfigGrid.IsAncestorOf(wheelSrc))) return;
            if (e.Delta < 0) CycleMode();
            // Explicit gesture: the wheel folds right away, without the pointer grace.
            else if (e.Delta > 0) CollapseFromHover();
            e.Handled = true;
            return;
        }
        if (_expanded) return;
        // In compact, the wheel down is an explicit content selection (equivalent to a
        // click, 001 MOD RF-3); up it is already compact.
        if (Math.Clamp(SettingsManager.Current.IslandExpandTrigger, 0, 2) is not (1 or 2)) return;
        if (e.OriginalSource is DependencyObject src && Seekbar.IsAncestorOf(src)) return;
        if (e.Delta < 0)
        {
            ExpandLastUsable();
            e.Handled = true;
        }
    }
}
