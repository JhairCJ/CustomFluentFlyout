using System.ComponentModel;
using System.Runtime.InteropServices;
using FluentFlyout.Classes;

namespace FluentFlyoutWPF.Classes;

internal interface ITaskbarAttachmentNative
{
    bool IsWindow(IntPtr window);
    IntPtr Parent(IntPtr window);
    int Style(IntPtr window);
    void Style(IntPtr window, int style);
    void Parent(IntPtr window, IntPtr parent);
    (int X, int Y) ClientPoint(IntPtr parent, int x, int y);
}

/// <summary>The upstream child-window contract, with checked native errors.</summary>
internal sealed class TaskbarAttachment(ITaskbarAttachmentNative? native = null)
{
    private readonly ITaskbarAttachmentNative _native = native ?? new Win32();
    internal bool Attach(IntPtr child, IntPtr taskbar)
    {
        if (!_native.IsWindow(child) || !_native.IsWindow(taskbar)) return false;
        if (_native.Parent(child) == taskbar) return false;
        _native.Style(child, (_native.Style(child) & ~unchecked((int)0x80000000)) | 0x40000000);
        _native.Parent(child, taskbar);
        if (_native.Parent(child) != taskbar) throw new InvalidOperationException("Taskbar parenting was not applied");
        return true;
    }
    internal (int X, int Y) ClientPoint(IntPtr parent, int x, int y) => _native.ClientPoint(parent, x, y);
    private sealed class Win32 : ITaskbarAttachmentNative
    {
        public bool IsWindow(IntPtr window) => window != IntPtr.Zero && NativeMethods.IsWindow(window);
        public IntPtr Parent(IntPtr window) => NativeMethods.GetParent(window);
        public int Style(IntPtr window) => NativeMethods.GetWindowLong(window, -16);
        public void Style(IntPtr window, int style)
        {
            Marshal.SetLastPInvokeError(0);
            int previous = SetWindowLong(window, -16, style);
            if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        public void Parent(IntPtr window, IntPtr parent)
        {
            Marshal.SetLastPInvokeError(0);
            var previous = NativeMethods.SetParent(window, parent);
            if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        public (int X, int Y) ClientPoint(IntPtr parent, int x, int y)
        {
            var point = new NativeMethods.POINT { X = x, Y = y };
            if (!NativeMethods.ScreenToClient(parent, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return (point.X, point.Y);
        }
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr window, int index, int value);
    }
}
