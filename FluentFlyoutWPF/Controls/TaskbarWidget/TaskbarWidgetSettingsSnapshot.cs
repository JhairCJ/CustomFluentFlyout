// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;

namespace FluentFlyout.Controls.TaskbarWidget;

/// <summary>
/// Point-in-time copy of every taskbar-widget setting read during one UI update.
/// <see cref="SettingsManager.Current"/> is a live singleton: reading it dozens of
/// times across a single update is both wasted work and a consistency hazard (a value
/// can flip mid-update). Capture once per update and pass the snapshot down instead.
/// </summary>
internal readonly record struct TaskbarWidgetSettingsSnapshot(
    bool ShowAlbumArt,
    bool ControlsEnabled,
    bool BackgroundBlur,
    bool BackgroundRotate,
    bool Animated,
    int SongChangeAnimation,
    bool ScrollingEnabled,
    bool HideCompletely,
    bool ShowPauseOverlay,
    bool FixedWidth,
    int FixedWidthPx)
{
    public static TaskbarWidgetSettingsSnapshot Capture()
    {
        var s = SettingsManager.Current;
        return new TaskbarWidgetSettingsSnapshot(
            s.TaskbarWidgetShowAlbumArt,
            s.TaskbarWidgetControlsEnabled,
            s.TaskbarWidgetBackgroundBlur,
            s.TaskbarWidgetBackgroundRotate,
            s.TaskbarWidgetAnimated,
            s.TaskbarWidgetSongChangeAnimation,
            s.TaskbarWidgetScrollingEnabled,
            s.TaskbarWidgetHideCompletely,
            s.TaskbarWidgetShowPauseOverlay,
            s.TaskbarWidgetFixedWidth,
            s.TaskbarWidgetFixedWidthPx);
    }
}
