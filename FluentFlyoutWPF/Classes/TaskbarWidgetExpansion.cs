// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>Physical screen geometry for growing the widget itself toward the monitor.</summary>
internal static class TaskbarWidgetExpansion
{
    internal enum Edge { Bottom, Top, Left, Right }
    internal readonly record struct Box(double Left, double Top, double Width, double Height)
    {
        internal double Right => Left + Width;
        internal double Bottom => Top + Height;
        internal double CenterX => Left + Width / 2;
        internal double CenterY => Top + Height / 2;
    }

    internal static Edge EdgeOf(Box taskbar, Box workArea)
    {
        if (taskbar.Top >= workArea.Bottom - 1) return Edge.Bottom;
        if (taskbar.Bottom <= workArea.Top + 1) return Edge.Top;
        if (taskbar.Right <= workArea.Left + 1) return Edge.Left;
        if (taskbar.Left >= workArea.Right - 1) return Edge.Right;
        double dx = taskbar.CenterX - workArea.CenterX;
        double dy = taskbar.CenterY - workArea.CenterY;
        return Math.Abs(dx) > Math.Abs(dy)
            ? (dx < 0 ? Edge.Left : Edge.Right)
            : (dy < 0 ? Edge.Top : Edge.Bottom);
    }

    // The outer edge stays anchored to the compact widget. There is no gap and no
    // second surface. The taskbar + work area together bound the visible monitor.
    internal static Box Expand(Box widget, Box taskbar, Box workArea, double width, double height)
    {
        var monitor = Union(taskbar, workArea);
        width = Math.Min(width, monitor.Width);
        height = Math.Min(height, monitor.Height);
        double left = widget.CenterX - width / 2;
        double top = widget.CenterY - height / 2;
        switch (EdgeOf(taskbar, workArea))
        {
            case Edge.Bottom: top = widget.Bottom - height; break;
            case Edge.Top: top = widget.Top; break;
            case Edge.Left: left = widget.Left; break;
            case Edge.Right: left = widget.Right - width; break;
        }
        return new Box(Math.Clamp(left, monitor.Left, monitor.Right - width),
            Math.Clamp(top, monitor.Top, monitor.Bottom - height), width, height);
    }

    internal static Box Union(Box a, Box b)
    {
        double left = Math.Min(a.Left, b.Left), top = Math.Min(a.Top, b.Top);
        return new Box(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }
}
