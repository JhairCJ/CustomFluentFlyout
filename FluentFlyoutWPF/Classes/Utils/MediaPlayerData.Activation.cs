// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyout.Classes.Utils;

public static partial class MediaPlayerData
{
    private static readonly NLog.Logger ActivationLogger = NLog.LogManager.GetCurrentClassLogger();

    // Called only from the user-requested worker, never the input hook or UI thread.
    public static bool TryActivateMediaPlayer(string mediaPlayerId, string? mediaTitle)
    {
        try
        {
            // Cached display metadata may outlive the process, so resolve again now.
            mediaPlayerCache.TryRemove(mediaPlayerId, out _);
            mediaPlayerIdVariants.TryRemove(mediaPlayerId, out _);
            GetProcessSnapshots(refresh: true);
            GetAndCacheMediaPlayerData(mediaPlayerId);
            if (!mediaPlayerCache.TryGetValue(mediaPlayerId, out var info) || info.ProcessId <= 0) return false;
            using var process = Process.GetProcessById(info.ProcessId);
            string name = process.ProcessName;
            bool browser = name.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                || name.Equals("msedge", StringComparison.OrdinalIgnoreCase);
            var processes = Process.GetProcessesByName(name);
            var ids = new HashSet<int>();
            foreach (var candidate in processes)
            {
                using (candidate) ids.Add(candidate.Id);
            }
            var windows = new List<(IntPtr Handle, uint ProcessId)>();
            EnumWindows((handle, _) =>
            {
                GetWindowProcessId(handle, out uint pid);
                if (ids.Contains((int)pid) && IsWindowVisible(handle)) windows.Add((handle, pid));
                return true;
            }, IntPtr.Zero);

            IntPtr associated = process.MainWindowHandle;
            // A PID with one window gives an unambiguous fallback. With multiple
            // browser windows, never choose a different window arbitrarily.
            var owned = windows.Where(w => w.ProcessId == info.ProcessId).ToList();
            if (associated == IntPtr.Zero && owned.Count == 1) associated = owned[0].Handle;
            if (!browser) return ActivateWindow(associated);

            if (!string.IsNullOrWhiteSpace(mediaTitle))
            {
                var matches = new List<(IntPtr Window, AutomationElement Tab)>();
                bool complete = true;
                foreach (var window in windows)
                {
                    try
                    {
                        var root = AutomationElement.FromHandle(window.Handle);
                        var tabs = root.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                        foreach (AutomationElement tab in tabs)
                            if (tab.Current.Name.Contains(mediaTitle, StringComparison.OrdinalIgnoreCase))
                                matches.Add((window.Handle, tab));
                    }
                    catch (Exception ex) { complete = false; ActivationLogger.Debug(ex, "Browser window unavailable while locating media tab"); }
                }
                if (complete && matches.Count == 1)
                {
                    var match = matches[0];
                    try
                    {
                        if (match.Tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern))
                        {
                            ((SelectionItemPattern)pattern).Select();
                            return ActivateWindow(match.Window);
                        }
                        if (match.Tab.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                        {
                            ((InvokePattern)pattern).Invoke();
                            return ActivateWindow(match.Window);
                        }
                    }
                    catch (Exception ex) { ActivationLogger.Debug(ex, "Media tab disappeared before activation"); }
                }
            }
            return ActivateWindow(associated);
        }
        catch (Exception ex)
        {
            ActivationLogger.Warn(ex, "Unable to activate existing media player");
            return false;
        }
    }

    private static bool ActivateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;
        if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
        return SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);
}
