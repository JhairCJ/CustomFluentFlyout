using System.Runtime.InteropServices;
using System.Windows.Threading;
using FluentFlyoutWPF.Classes;
using static FluentFlyout.Classes.NativeMethods;

// dotnet run --project tools/TaskbarChecks -c Release
// Exercise the real hook helper with native calls replaced; no windows or input hooks.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        int dismissed = 0;
        bool inside = true;
        using var hook = new MouseClickOutsideHook((_, _) => inside, () => dismissed++, Dispatcher.CurrentDispatcher);
        Require(hook.Install() && hook.Install() && ActiveHooks == 1, "Installation must be idempotent");
        Require(Click(WM_LBUTTONDOWN) == (IntPtr)7, "Clicks must pass through");
        Drain();
        Require(dismissed == 0, "Clicking inside must preserve the surface");
        inside = false;
        Click(WM_RBUTTONDOWN);
        Require(dismissed == 0, "Closing must be queued, never run in the native callback");
        Drain();
        Require(dismissed == 1, "Outside clicks must close the surface");
        Click(WM_MBUTTONDOWN);
        hook.Dispose();
        hook.Install(); // Menu closed/reopened, or the target changed back to expansion.
        Drain();
        Require(dismissed == 1 && ActiveHooks == 1, "Old queued clicks must not dismiss the new surface");
        Click(WM_LBUTTONDOWN);
        Click(WM_RBUTTONDOWN);
        hook.Dispose();
        hook.Dispose();
        Drain();
        Require(dismissed == 1 && ActiveHooks == 0, "Teardown must release the hook and invalidate pending clicks");
        Console.WriteLine("Outside-click lifecycle checks passed; no native input was used.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}

namespace FluentFlyout.Classes
{
    // Only the native boundary is stubbed; the production callback and Dispatcher run unchanged.
    internal static class NativeMethods
    {
        internal const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
        internal delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MSLLHOOKSTRUCT { internal POINT pt; }
        private static LowLevelMouseProc? _callback;
        internal static int ActiveHooks;
        internal static IntPtr GetModuleHandle(string? name) => (IntPtr)1;
        internal static IntPtr SetWindowsHookExMouse(int id, LowLevelMouseProc callback, IntPtr module, uint thread)
        {
            _callback = callback;
            ActiveHooks++;
            return (IntPtr)1;
        }
        internal static bool UnhookWindowsHookEx(IntPtr hook) { ActiveHooks--; return true; }
        internal static IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data) => (IntPtr)7;
        internal static IntPtr Click(int message)
        {
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<MSLLHOOKSTRUCT>());
            try
            {
                Marshal.StructureToPtr(new MSLLHOOKSTRUCT { pt = new POINT { X = 50, Y = 50 } }, data, false);
                return _callback!(0, (IntPtr)message, data);
            }
            finally { Marshal.FreeHGlobal(data); }
        }
    }
}
