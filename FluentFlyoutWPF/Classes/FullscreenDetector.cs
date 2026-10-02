// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyoutWPF.Classes;

internal class FullscreenDetector
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Checks if a DirectX exclusive fullscreen application or game is currently running.
    /// </summary>
    /// <returns>
    /// true if a fullscreen DirectX application is running;
    /// false if no fullscreen application is detected, DisableIfFullscreen setting is false, or if the check fails
    /// </returns>
    public static bool IsFullscreenApplicationRunning()
    {
        if (!SettingsManager.Current.DisableIfFullscreen) return false;
        return QueryState() == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;
    }

    /// <summary>
    /// Is the shell in a fullscreen (or absent machine) state?
    ///
    /// <para>Unlike <see cref="IsFullscreenApplicationRunning"/> - which only recognizes
    /// EXCLUSIVE D3D and depends on the Media Flyout setting - this one also recognizes
    /// <c>QUNS_BUSY</c> (the state reported by games and borderless fullscreen video,
    /// which are not exclusive D3D) and <c>QUNS_PRESENTATION_MODE</c> (presentations),
    /// plus the locked machine (<c>QUNS_NOT_PRESENT</c>). The Island steps aside with
    /// it: staying on top of a game was exactly the case that went undetected
    /// (001 RF-8/14).</para>
    ///
    /// <para>It looks at no setting: the caller decides (the Island has its own).</para>
    /// </summary>
    public static bool IsFullscreenOrAwayState()
    {
        return QueryState() switch
        {
            QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN => true,
            QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY => true,
            QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE => true,
            QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT => true,
            _ => false,
        };
    }

    /// <summary>
    /// The shell's UI settings state, or null if the query fails (it never throws:
    /// fullscreen detection must not be able to take the container down).
    /// </summary>
    private static QUERY_USER_NOTIFICATION_STATE? QueryState()
    {
        try
        {
            int result = SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);
            if (result != 0) // 0 means SUCCESS
            {
                throw new Exception($"SHQueryUserNotificationState failed with error code: {result}");
            }
            return state;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error detecting fullscreen state");
            return null;
        }
    }
}