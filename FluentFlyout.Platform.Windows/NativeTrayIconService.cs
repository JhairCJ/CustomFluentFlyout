using System.Runtime.InteropServices;
using FluentFlyout.Core;

namespace FluentFlyout.Platform.Windows;

/// <summary>
/// A notification-area icon service whose owner HWND is supplied by the host, rather than an AppWindow.
/// </summary>
public sealed class NativeTrayIconService : ITrayIconService, IDisposable
{
    private readonly INotificationAreaShell shell;
    private readonly Func<nint> ownerWindowHandle;
    private readonly uint iconId;
    private readonly nint iconHandle;
    private string toolTip = string.Empty;
    private nint visibleOwnerHandle;
    private bool disposed;

    public NativeTrayIconService(Func<nint> ownerWindowHandle, nint iconHandle = 0, uint iconId = 1)
        : this(new ShellNotificationArea(), ownerWindowHandle, iconHandle, iconId)
    {
    }

    internal NativeTrayIconService(INotificationAreaShell shell, Func<nint> ownerWindowHandle, nint iconHandle = 0, uint iconId = 1)
    {
        this.shell = shell ?? throw new ArgumentNullException(nameof(shell));
        this.ownerWindowHandle = ownerWindowHandle ?? throw new ArgumentNullException(nameof(ownerWindowHandle));
        this.iconHandle = iconHandle != 0 ? iconHandle : LoadIconW(0, (nint)32512); // IDI_APPLICATION
        this.iconId = iconId;
    }

    public bool IsVisible { get; private set; }

    public void Show()
    {
        ThrowIfDisposed();
        nint hwnd = ownerWindowHandle();
        if (hwnd == 0)
            return;

        NotifyIconCommand command = IsVisible ? NotifyIconCommand.Modify : NotifyIconCommand.Add;
        IsVisible = shell.Notify(command, CreateData(hwnd, includeIcon: true));
        if (IsVisible)
            visibleOwnerHandle = hwnd;
    }

    public void Hide()
    {
        if (disposed || !IsVisible)
            return;

        IsVisible = !shell.Notify(NotifyIconCommand.Delete, CreateData(visibleOwnerHandle, includeIcon: false));
        if (!IsVisible)
            visibleOwnerHandle = 0;
    }

    public void SetToolTip(string text)
    {
        ThrowIfDisposed();
        toolTip = text ?? string.Empty;
        if (IsVisible)
            IsVisible = shell.Notify(NotifyIconCommand.Modify, CreateData(visibleOwnerHandle, includeIcon: true));
    }

    public void Dispose()
    {
        if (disposed)
            return;

        Hide();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private NotificationIconData CreateData(nint hwnd, bool includeIcon) => new()
    {
        Hwnd = hwnd,
        Id = iconId,
        Icon = includeIcon ? iconHandle : 0,
        ToolTip = toolTip,
        IncludeIcon = includeIcon,
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern nint LoadIconW(nint instance, nint iconName);
}

internal interface INotificationAreaShell
{
    bool Notify(NotifyIconCommand command, NotificationIconData data);
}

internal enum NotifyIconCommand : uint { Add = 0, Modify = 1, Delete = 2 }

internal sealed record NotificationIconData
{
    public required nint Hwnd { get; init; }
    public required uint Id { get; init; }
    public required nint Icon { get; init; }
    public required string ToolTip { get; init; }
    public required bool IncludeIcon { get; init; }
}

internal sealed class ShellNotificationArea : INotificationAreaShell
{
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    public bool Notify(NotifyIconCommand command, NotificationIconData data)
    {
        NativeNotifyIconData nativeData = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeNotifyIconData>(),
            hWnd = data.Hwnd,
            uID = data.Id,
            uFlags = NifMessage | NifTip | (data.IncludeIcon ? NifIcon : 0),
            hIcon = data.Icon,
            szTip = data.ToolTip.Length > 127 ? data.ToolTip[..127] : data.ToolTip,
        };
        return ShellNotifyIconW((uint)command, ref nativeData);
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconW(uint message, ref NativeNotifyIconData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeNotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }
}
