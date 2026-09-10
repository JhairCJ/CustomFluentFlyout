using System.Drawing;
using System.Runtime.InteropServices;

namespace FluentFlyout.Platform.Windows;

public struct ScreenBounds
{
    public int Left;
    public int Top;
    public int Width;
    public int Height;
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public ScreenBounds(int left, int top, int width, int height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }
}

public sealed class MonitorDevice
{
    public string DeviceName { get; init; } = string.Empty;
    public ScreenBounds MonitorArea { get; init; }
    public ScreenBounds WorkArea { get; init; }
    public bool IsPrimary { get; init; }
    public uint DpiX { get; init; } = 96;
    public uint DpiY { get; init; } = 96;
    public double ScaleFactor => DpiX / 96.0;
}

public sealed class MonitorService
{
    public IReadOnlyList<MonitorDevice> GetMonitors()
    {
        var monitors = new List<MonitorDevice>();

        bool Callback(nint hMonitor, nint hdcMonitor, nint lprcMonitor, nint dwData)
        {
            var info = new MonitorInfoEx();
            info.cbSize = Marshal.SizeOf<MonitorInfoEx>();

            if (GetMonitorInfo(hMonitor, ref info))
            {
                uint dpiX = 96, dpiY = 96;
                try
                {
                    GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out dpiX, out dpiY);
                }
                catch
                {
                    dpiX = 96;
                    dpiY = 96;
                }

                monitors.Add(new MonitorDevice
                {
                    DeviceName = new string(info.szDevice).TrimEnd('\0'),
                    MonitorArea = new ScreenBounds(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top),
                    WorkArea = new ScreenBounds(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top),
                    IsPrimary = (info.dwFlags & 1) != 0,
                    DpiX = dpiX,
                    DpiY = dpiY
                });
            }

            return true;
        }

        EnumDisplayMonitors(0, 0, Callback, 0);

        if (monitors.Count == 0)
        {
            // Fallback to primary screen via GetSystemMetrics
            monitors.Add(new MonitorDevice
            {
                DeviceName = "Default Display",
                MonitorArea = new ScreenBounds(0, 0, GetSystemMetrics(0), GetSystemMetrics(1)),
                WorkArea = new ScreenBounds(0, 0, GetSystemMetrics(0), GetSystemMetrics(1)),
                IsPrimary = true
            });
        }

        return monitors;
    }

    public MonitorDevice GetSelectedMonitor(int index)
    {
        var list = GetMonitors();
        if (list.Count == 0)
            return new MonitorDevice { IsPrimary = true, WorkArea = new ScreenBounds(0, 0, 1920, 1080) };

        return list[Math.Clamp(index, 0, list.Count - 1)];
    }

    public (int X, int Y) CalculatePosition(int monitorIndex, int positionPreset, int width, int height, int margin = 20)
    {
        var monitor = GetSelectedMonitor(monitorIndex);
        var work = monitor.WorkArea;

        int x = work.Left + margin;
        int y = work.Top + margin;

        switch (positionPreset)
        {
            case 0: // BottomLeft
                x = work.Left + margin;
                y = work.Bottom - height - margin;
                break;
            case 1: // BottomCenter
                x = work.Left + (work.Width - width) / 2;
                y = work.Bottom - height - margin;
                break;
            case 2: // BottomRight
                x = work.Right - width - margin;
                y = work.Bottom - height - margin;
                break;
            case 3: // TopLeft
                x = work.Left + margin;
                y = work.Top + margin;
                break;
            case 4: // TopCenter
                x = work.Left + (work.Width - width) / 2;
                y = work.Top + margin;
                break;
            case 5: // TopRight
                x = work.Right - width - margin;
                y = work.Top + margin;
                break;
        }

        return (x, y);
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint lprcMonitor, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
