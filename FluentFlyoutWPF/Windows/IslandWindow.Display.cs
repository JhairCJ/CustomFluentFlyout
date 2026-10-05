// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FluentFlyout.Classes;

namespace FluentFlyoutWPF.Windows;

public partial class IslandWindow
{
    private bool _displayRefreshPending;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        QueueIslandDisplayRefresh();
    }

    private void QueueIslandDisplayRefresh()
    {
        if (_disposed || _displayRefreshPending || Dispatcher.HasShutdownStarted) return;
        _displayRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _displayRefreshPending = false;
            if (_disposed) return;
            _ctxValid = false;
            RefreshContextSnapshot();
            PositionTopCenter();
            RefreshAppearance();
            SyncMeasuredHeight();
            UpdateLayout();
            var hwnd = new WindowInteropHelper(this).Handle;
            uint dpi = hwnd == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(hwnd);
            if (dpi > 0)
                NativeMethods.SetWindowPos(hwnd, 0, 0, 0,
                    (int)Math.Ceiling(Width * dpi / 96.0), (int)Math.Ceiling(Height * dpi / 96.0),
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            PostActivity(IslandActivityReason.Context | IslandActivityReason.Recovery);
        });
    }
}
