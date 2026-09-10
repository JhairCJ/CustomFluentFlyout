using Microsoft.UI.Xaml;
using WinRT.Interop;
using System.Runtime.InteropServices;

namespace FluentFlyout.Platform.Windows;

public sealed partial class WindowInteropService
{
    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExToolWindow = 0x00000080;

    public nint GetHwnd(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return WindowNative.GetWindowHandle(window);
    }

    public bool TryGetHwnd(Window? window, out nint hwnd)
    {
        hwnd = window is null ? 0 : WindowNative.GetWindowHandle(window);
        return hwnd != 0;
    }

    public void SetNoActivate(nint hwnd)
    {
        if (hwnd == 0) return;

        nint currentStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        if ((currentStyle & WsExNoActivate) != 0)
            return;

        SetWindowLongPtr(hwnd, GwlExStyle, currentStyle | WsExNoActivate);
    }

    public void SetToolWindow(nint hwnd)
    {
        if (hwnd == 0) return;

        nint currentStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        if ((currentStyle & WsExToolWindow) != 0)
            return;

        SetWindowLongPtr(hwnd, GwlExStyle, currentStyle | WsExToolWindow);
    }

    public bool HasNoActivateStyle(nint hwnd)
    {
        if (hwnd == 0) return false;
        return (GetWindowLongPtr(hwnd, GwlExStyle) & WsExNoActivate) != 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint newLong);
}
