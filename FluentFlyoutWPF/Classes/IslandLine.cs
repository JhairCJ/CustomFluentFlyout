// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// The Island's GREY LINE - the handle that announces there is something to open - and
/// the POINTER STRIP that accompanies it, as pure and testable decisions.
///
/// <para>The line is a door's HANDLE and the edge strip IS that door: that is why both
/// are resolved here and by the same rules. They used to live in separate branches - the
/// line in the per-frame paint, the strip in pointer presentation - and from that came
/// the two usual inconsistencies: an invisible line with an active door (a trigger
/// nobody sees, which is annoying) and a closed door with a painted line (a handle that
/// opens nothing).</para>
///
/// <list type="bullet">
/// <item><b>One single offset</b>: on notch the line hugs the edge; on the floating
/// island the setting lowers it, clamped. The rule was written three times - paint,
/// pointer detection and fold-back veto - and all three had to match by hand.</item>
/// <item><b>The handle exists if there is a door</b>: with nothing to open it is not
/// painted, because a stripe that leads nowhere is a lie.</item>
/// <item><b>The door follows what is visible</b>: with nothing drawn there is a strip
/// only if the user deliberately asked for an invisible door
/// (<c>IslandHiddenAccess</c>).</item>
/// </list>
/// </summary>
public static class IslandLine
{
    /// <summary>Grey line height (DIP).</summary>
    public const double BarHeight = 3;

    /// <summary>Grey line width with the box fully hidden (DIP).</summary>
    public const double BarWidth = 120;

    /// <summary>Line offset on notch: it is part of the edge and does not move (DIP).</summary>
    public const double NotchTopDip = 1;

    /// <summary>
    /// Vertical offset of the line (and of the status dot), in DIP from the window's
    /// top edge: the SINGLE rule shared by the paint, the pointer strip and the
    /// fold-back veto.
    /// </summary>
    public static double TopDip(bool notch, int configuredOffset) =>
        notch ? NotchTopDip : Math.Clamp(configuredOffset, 0, 60);

    /// <summary>
    /// 0..1 factor for the line width: 1 with the box fully hidden, 0 when the box (or
    /// the reveal's dot) has already taken its place. It follows the FASTER of the two
    /// springs because <c>p</c> finishes before <c>q</c> when emerging expanded, and it
    /// also fades out with the content opacity so the stripe does not outlive the fade
    /// towards the inactive piece.
    /// </summary>
    public static double WidthFactor(double p, double q, double contentOpacity) =>
        (1 - Math.Max(IslandPhysics.Smooth(p), IslandPhysics.Smooth(q)))
        * Math.Clamp(contentOpacity, 0, 1);

    /// <summary>
    /// Is the line (and its dot) painted? It needs the setting on AND something to
    /// open: the line is a door's handle, never a loose ornament.
    /// </summary>
    public static bool Shown(bool enabled, bool doorAvailable) => enabled && doorAvailable;

    /// <summary>
    /// Does the top edge pointer strip exist? With the box in view the strip is the box
    /// itself (plus its tolerance); with the box hidden the line holds it; and with
    /// nothing drawn only if the user asked for the invisible door. Without a strip the
    /// pointer does nothing in that zone.
    /// </summary>
    public static bool AccessZone(bool boxShown, bool lineShown, bool allowWhenHidden) =>
        boxShown || lineShown || allowWhenHidden;
}
