// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF;
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
    public static bool AreAnimationsEnabled =>
        SettingsManager.Current.TaskbarWidgetAnimated && SettingsManager.Current.FlyoutAnimationSpeed != 0;

    public static int GetDurationMs() => Math.Max(MainWindow.getDuration(), 1);

    public static EasingFunctionBase? GetEasing(MainWindow? mainWindow, bool easeOut)
    {
        if (mainWindow != null)
            return mainWindow.getEasingStyle(easeOut); // null means linear, as in the main flyout
        return new CubicEase { EasingMode = easeOut ? EasingMode.EaseOut : EasingMode.EaseIn };
    }
}
