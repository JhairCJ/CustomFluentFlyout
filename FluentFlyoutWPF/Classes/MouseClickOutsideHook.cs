// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Windows.Threading;
using static FluentFlyout.Classes.NativeMethods;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Queues dismissal when a mouse press falls outside the current transient surface.
/// Installed only while needed: no-activate windows cannot rely on focus loss.
/// The native callback never consumes a click or blocks on window/layout work.
/// </summary>
internal sealed class MouseClickOutsideHook : IDisposable
{
    private readonly Func<int, int, bool> _isInside;
    private readonly Action _onOutsideClick;
    private readonly Dispatcher _dispatcher;
    // The delegate must stay alive: the native hook stores the function pointer.
    private LowLevelMouseProc? _proc;
    private IntPtr _hook = IntPtr.Zero;
    private int _version;

    /// <summary>Did the native hook get installed? Only then is there real notification.</summary>
    public bool IsInstalled => _hook != IntPtr.Zero;

    public MouseClickOutsideHook(Func<int, int, bool> isInside, Action onOutsideClick, Dispatcher dispatcher)
    {
        _isInside = isInside;
        _onOutsideClick = onOutsideClick;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Installs the hook. Returns false —without throwing— when the system denies it:
    /// the widget then simply stays expanded until it is toggled or media stops.
    /// </summary>
    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        try
        {
            _proc = HookProc;
            _hook = SetWindowsHookExMouse(WH_MOUSE_LL, _proc, GetModuleHandle(null!), 0);
        }
        catch
        {
            _hook = IntPtr.Zero;
        }
        return IsInstalled;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsButtonDown(wParam))
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (!_isInside(data.pt.X, data.pt.Y))
                {
                    // The hook must return fast: the close is queued so the input
                    // thread never blocks on window teardown.
                    int version = _version;
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (_hook != IntPtr.Zero && version == _version) _onOutsideClick();
                    }, DispatcherPriority.Normal);
                }
            }
            catch
            {
                // A hook callback must never take down the input thread.
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsButtonDown(IntPtr wParam) =>
        wParam == (IntPtr)WM_LBUTTONDOWN
        || wParam == (IntPtr)WM_RBUTTONDOWN
        || wParam == (IntPtr)WM_MBUTTONDOWN;

    /// <summary>
    /// Releases the hook: after this no callback runs again. Safe to call twice.
    /// </summary>
    public void Dispose()
    {
        ++_version;
        if (_hook != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }
        _proc = null;
    }
}
