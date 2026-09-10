using System.Runtime.InteropServices;

namespace FluentFlyout.Platform.Windows;

/// <summary>
/// Native taskbar-host seam. The WinUI content is attached by the App layer after
/// the host HWND has been parented to Explorer's taskbar (WS_CHILD inside
/// Shell_TrayWnd), and positioned next to the system tray with DPI awareness.
/// </summary>
public sealed partial class TaskbarHostController : IDisposable
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoZOrder = 0x0004;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int GwlStyle = -16;
    private const long WsPopup = 0x80000000L;
    private const long WsChild = 0x40000000L;

    private readonly uint taskbarCreatedMessage;
    private nint host;
    private nint parent;
    private nint trayNotify;

    public TaskbarHostController()
    {
        taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    public nint ParentTaskbarHandle => parent;

    public bool IsAttached => host != 0 && parent != 0;

    public bool TryAttach(nint hostWindow, int width, int height)
    {
        if (hostWindow == 0)
            return false;

        host = hostWindow;
        parent = FindWindow("Shell_TrayWnd", null);
        if (parent == 0)
            return false;

        // Child of the taskbar: drop WS_POPUP, take WS_CHILD, or the window
        // floats above the taskbar as a separate entity.
        long style = GetWindowLongPtrW(host, GwlStyle);
        style = (style & ~WsPopup) | WsChild;
        _ = SetWindowLongPtrW(host, GwlStyle, style);

        SetParent(host, parent);
        trayNotify = FindWindowEx(parent, 0, "TrayNotifyWnd", null);

        SetWindowPos(host, 0, 0, 0, Math.Max(1, width), Math.Max(1, height), SwpNoActivate | SwpNoZOrder | SwpShowWindow);
        return true;
    }

    /// <summary>
    /// Computes the placement of the widget inside the taskbar: DPI-scaled size,
    /// vertically centered, horizontally anchored per <paramref name="position"/>
    /// (0 = near start, 1 = center, 2 = next to the system tray) with optional
    /// manual padding. Returns physical pixels relative to the taskbar client area.
    /// </summary>
    public (int X, int Y, int TaskbarWidth, int TaskbarHeight, double DpiScale) ComputePlacement(
        int logicalWidth,
        int logicalHeight,
        int position,
        int manualPadding,
        bool visualizerEnabled)
    {
        if (parent == 0)
            return (0, 0, 0, 0, 1);

        uint dpi = GetDpiForWindow(parent);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;

        GetWindowRect(parent, out var taskbar);
        int taskbarWidth = taskbar.Right - taskbar.Left;
        int taskbarHeight = taskbar.Bottom - taskbar.Top;

        int physicalWidth = (int)Math.Round(logicalWidth * scale);
        int physicalHeight = (int)Math.Round(logicalHeight * scale);
        int crossPos = Math.Max(0, (taskbarHeight - physicalHeight) / 2);

        int primaryPos;
        switch (Math.Clamp(position, 0, 2))
        {
            case 0: // near start
                primaryPos = 20;
                break;

            case 1: // centered
                primaryPos = (taskbarWidth - physicalWidth) / 2;
                break;

            default: // next to the system tray
            {
                if (trayNotify == 0)
                    trayNotify = FindWindowEx(parent, 0, "TrayNotifyWnd", null);

                if (trayNotify != 0)
                {
                    GetWindowRect(trayNotify, out var tray);
                    // Absolute -> relative to the taskbar.
                    primaryPos = (tray.Left - taskbar.Left) - physicalWidth - 2;
                }
                else
                {
                    primaryPos = taskbarWidth - physicalWidth - 20;
                }
                break;
            }
        }

        primaryPos = Math.Clamp(primaryPos + manualPadding, 0, Math.Max(0, taskbarWidth - physicalWidth));

        return (primaryPos, crossPos, taskbarWidth, taskbarHeight, scale);
    }

    /// <summary>
    /// Moves the host window to a physical position relative to the taskbar and
    /// resizes it, optionally showing it.
    /// </summary>
    public bool PlaceAt(int x, int y, int physicalWidth, int physicalHeight, bool show)
    {
        if (host == 0)
            return false;

        return SetWindowPos(
            host, 0, x, y,
            Math.Max(1, physicalWidth), Math.Max(1, physicalHeight),
            SwpNoActivate | SwpNoZOrder | (show ? SwpShowWindow : 0));
    }

    public bool IsTaskbarCreatedMessage(uint message) => message == taskbarCreatedMessage;

    public void SetVisible(bool visible)
    {
        if (host != 0)
            ShowWindow(host, visible ? SwShow : SwHide);
    }

    public void Resize(int width, int height)
    {
        if (host != 0)
            SetWindowPos(host, 0, 0, 0, Math.Max(1, width), Math.Max(1, height), SwpNoActivate | SwpNoZOrder);
    }

    public void Dispose()
    {
        if (host != 0 && parent != 0)
            SetParent(host, 0);

        host = 0;
        parent = 0;
        trayNotify = 0;
        GC.SuppressFinalize(this);
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint afterChild, string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "SetParent")]
    private static partial nint SetParent(nint child, nint parent);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string message);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial long GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial long SetWindowLongPtrW(nint window, int index, long newLong);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static partial uint GetDpiForWindow(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out RECT rect);
}
