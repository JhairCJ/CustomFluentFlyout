// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentFlyout.Controls.TaskbarWidget;

/// <summary>
/// Single source of truth for taskbar-widget animation gating, duration and easing.
/// Extracted from the duplicated <c>AreAnimationsEnabled</c> / <c>GetEasing</c> logic
/// that lived in both <c>TaskbarWidgetControl</c> and <c>TaskbarWindow</c>, so the two
/// stay consistent by construction (widget toggle + global flyout speed + user easing).
/// </summary>
internal static class TaskbarWidgetAnimationEnvironment
{
    internal static void AnimateHover(SolidColorBrush brush, Color target)
    {
        if (!AreAnimationsEnabled)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;
            return;
        }
        // An omitted From continues from the currently presented color on reversal.
        brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    internal static void Pulse(FrameworkElement element)
    {
        if (!AreAnimationsEnabled) return;
        double opacity = (double)element.GetAnimationBaseValue(UIElement.OpacityProperty);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(opacity * 0.72, opacity, TimeSpan.FromMilliseconds(80))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    public static bool AreAnimationsEnabled =>
        SettingsManager.Current.TaskbarWidgetAnimated && SettingsManager.Current.FlyoutAnimationSpeed != 0;

    public static int GetDurationMs() => Math.Max(MainWindow.getDuration(), 1);

    public static double GetExpansionDurationMs(bool expanded) =>
        Math.Clamp(GetDurationMs() * (expanded ? 0.6 : 0.45), 90, expanded ? 240 : 180);

    public static EasingFunctionBase? GetEasing(MainWindow? mainWindow, bool easeOut)
    {
        if (mainWindow != null)
            return mainWindow.getEasingStyle(easeOut); // null means linear, as in the main flyout
        return new CubicEase { EasingMode = easeOut ? EasingMode.EaseOut : EasingMode.EaseIn };
    }
}
