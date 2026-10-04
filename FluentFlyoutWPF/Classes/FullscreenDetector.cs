// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
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
    /// Suppresses overlays for an absent user or a foreground application visibly
    /// covering the target monitor. Notification policy alone is not fullscreen:
    /// Start, taskbar previews and presentation settings can also block notifications.
    /// </summary>
    public static bool IsFullscreenOrAwayState(Rect monitorArea)
    {
        var state = QueryState();
        if (state == QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT) return true;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground)
            || IsShellSurface(foreground) || monitorArea.IsEmpty || monitorArea.Width <= 0)
            return false;

        // Maximized, captioned windows are ordinary desktop applications even
        // with an auto-hidden taskbar. Borderless fullscreen removes the caption.
        const int caption = 0x00C00000, maximized = 0x01000000;
        int style = GetWindowLong(foreground, GWL_STYLE);
        if ((style & caption) == caption && (style & maximized) != 0
            && state != QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
            return false;

        // GetWindowRect includes invisible resize borders; use DWM's visible
        // physical bounds so a maximized window cannot accidentally cover a monitor.
        const int extendedFrameBounds = 9;
        if (DwmGetWindowAttribute(foreground, extendedFrameBounds, out var bounds, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(foreground, out bounds))
            return false;
        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top
            && bounds.Left <= monitorArea.Left && bounds.Top <= monitorArea.Top
            && bounds.Right >= monitorArea.Right && bounds.Bottom >= monitorArea.Bottom;
    }

    private static IntPtr _classifiedWindow;
    private static uint _classifiedProcess;
    private static bool _classifiedAsShell, _classifiedAsTaskbarInteraction;

    /// <summary>Start, search, task switching or previews currently own foreground.</summary>
    public static bool IsTaskbarInteractionActive()
    {
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && IsShellSurface(foreground) && _classifiedAsTaskbarInteraction;
    }

    private static bool IsShellSurface(IntPtr window)
    {
        GetWindowProcessId(window, out uint pid);
        if (window == _classifiedWindow && pid == _classifiedProcess) return _classifiedAsShell;
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        string className = name.ToString();
        bool shell = className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "TaskListThumbnailWnd" or "TaskListOverlayWnd" or "TaskSwitcherWnd"
            or "MultitaskingViewFrame" or "XamlExplorerHostIslandWindow" or "ImmersiveLauncher" or "DV2ControlHost";
        if (!shell && pid != 0)
        {
            if (pid == Environment.ProcessId) shell = true;
            else
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    shell = process.ProcessName is "StartMenuExperienceHost" or "ShellExperienceHost"
                        or "SearchHost" or "SearchApp" or "SearchUI";
                }
                catch (ArgumentException) { return false; } // Window's process already exited.
                catch (System.ComponentModel.Win32Exception) { return false; }
            }
        }
        _classifiedWindow = window;
        _classifiedProcess = pid;
        _classifiedAsShell = shell;
        _classifiedAsTaskbarInteraction = shell && pid != Environment.ProcessId
            && className is not "Progman" and not "WorkerW";
        return shell;
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