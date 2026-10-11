using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using FluentFlyout.Classes;

namespace FluentFlyoutWPF.Classes;

/// <summary>Native input pump. No WPF Dispatcher or device work is allowed here.</summary>
internal sealed class KeyboardInputThread : IDisposable
{
    private readonly Thread _thread;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Completion => _finished.Task;
    private string? _windowClass;
    private readonly Func<int, IntPtr, IntPtr, IntPtr> _route;
    private readonly Action? _reset;
    private IntPtr _hook;
    private uint _threadId;
    private int _disposed;
    private readonly Action<int, bool>? _observeRelease;
    private readonly Func<int, bool>? _observeKey;
    private readonly KeyboardReconciler _reconciler = new();
    private readonly WindowProcedure _windowProcedure;
    private IntPtr _window;
    private readonly Func<bool>? _inputAvailable;
    private bool _desktopAvailable = true;
    private readonly Func<NativeMethods.LowLevelKeyboardProc, IntPtr> _install;
    private readonly Action<IntPtr> _remove;
    private readonly Func<IntPtr, int, IntPtr, IntPtr, IntPtr> _next;
    private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Done)> _commands = new();
    internal Task Ready => _ready.Task;
    internal bool IsAlive => _thread.IsAlive;

    public KeyboardInputThread(Func<int, IntPtr, IntPtr, IntPtr> route, Action? reset = null,
        Func<int, bool>? observeKey = null, Action<int, bool>? observeRelease = null, Func<bool>? inputAvailable = null,
        Func<NativeMethods.LowLevelKeyboardProc, IntPtr>? install = null, Action<IntPtr>? remove = null,
        Func<IntPtr, int, IntPtr, IntPtr, IntPtr>? next = null)
    {
        _route = route;
        _reset = reset;
        _observeKey = observeKey;
        _observeRelease = observeRelease;
        _inputAvailable = inputAvailable;
        _install = install ?? (callback => NativeMethods.SetWindowsHookEx(13, callback, GetModuleHandle(null), 0));
        _remove = remove ?? (handle => NativeMethods.UnhookWindowsHookEx(handle));
        _next = next ?? NativeMethods.CallNextHookEx;
        _callback = Callback;
        _windowProcedure = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "FluentFlyout keyboard input" };
        _thread.Start();
    }
    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Create queue before publishing readiness.
            string windowClass = "FluentFlyoutInput" + _threadId;
            _windowClass = windowClass;
            var definition = new WindowClass { Procedure = _windowProcedure, Name = windowClass, Instance = GetModuleHandle(null) };
            if (RegisterClass(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _window = CreateWindowEx(0, windowClass, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, definition.Instance, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var device = new RawDevice { Page = 1, Usage = 6, Flags = 0x100, Target = _window }; // INPUTSINK, observer only
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SetTimer(_window, 1, 100, IntPtr.Zero);
            WTSRegisterSessionNotification(_window, 0);
            _hook = _install(_callback);
            if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.TrySetResult();
            if (Volatile.Read(ref _disposed) != 0) return;
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Id == 0x8001)
                {
                    while (_commands.TryDequeue(out var command))
                    {
                        try { command.Action(); command.Done.TrySetResult(); }
                        catch (Exception ex) { command.Done.TrySetException(ex); }
                    }
                }
                else { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
        }
        catch (Exception ex)
        { _ready.TrySetException(ex); NLog.LogManager.GetCurrentClassLogger().Error(ex, "Keyboard input pump failed"); }
        finally
        {
            if (_hook != IntPtr.Zero) _remove(_hook);
            _hook = IntPtr.Zero;
            if (_window != IntPtr.Zero)
            {
                WTSUnRegisterSessionNotification(_window);
                KillTimer(_window, 1);
                var device = new RawDevice { Page = 1, Usage = 6, Flags = 1 };
                RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawDevice>());
                DestroyWindow(_window);
            }
            if (_windowClass != null) UnregisterClass(_windowClass, GetModuleHandle(null));
            try { _reset?.Invoke(); } catch { }
            _finished.TrySetResult();
            while (_commands.TryDequeue(out var pending)) pending.Done.TrySetCanceled();
        }
    }
    private IntPtr Callback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                if (message == new IntPtr(0x100) || message == new IntPtr(0x101)
                    || message == new IntPtr(0x104) || message == new IntPtr(0x105))
                {
                    int flags = Marshal.ReadInt32(data, 8);
                    if ((flags & 0x10) == 0 && _observeKey?.Invoke(Marshal.ReadInt32(data)) == true)
                        _reconciler.Hook(Marshal.ReadInt32(data),
                            message == new IntPtr(0x100) || message == new IntPtr(0x104), Environment.TickCount64);
                }
                if (_route(code, message, data) != IntPtr.Zero) return new IntPtr(1);
            }
            catch (Exception ex)
            {
                try { _reset?.Invoke(); } catch { }
                ThreadPool.QueueUserWorkItem(_ => NLog.LogManager.GetCurrentClassLogger().Error(ex,
                    "Keyboard callback failed; passing input through"));
            }
        }
        return _next(_hook, code, message, data);
    }
    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == 0xFF) // WM_INPUT
            {
                uint size = 0;
                uint headerSize = (uint)(8 + 2 * IntPtr.Size);
                GetRawInputData(lParam, 0x10000003, IntPtr.Zero, ref size, headerSize);
                if (size >= headerSize + 16 && size <= 256)
                {
                    var buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetRawInputData(lParam, 0x10000003, buffer, ref size, headerSize) == size
                            && Marshal.ReadInt32(buffer) == 1)
                        {
                            int offset = (int)headerSize;
                            int vk = (ushort)Marshal.ReadInt16(buffer, offset + 6);
                            int flags = (ushort)Marshal.ReadInt16(buffer, offset + 2);
                            // Raw Input distinguishes shift by makecode and ctrl/alt by E0.
                            if (vk == 0x10) vk = Marshal.ReadInt16(buffer, offset) == 0x36 ? 0xA1 : 0xA0;
                            if (vk == 0x11) vk = (flags & 2) != 0 ? 0xA3 : 0xA2;
                            if (vk == 0x12) vk = (flags & 2) != 0 ? 0xA5 : 0xA4;
                            if (_desktopAvailable && Marshal.ReadInt32(buffer, offset + 12) != 0x46464449
                                && _observeKey?.Invoke(vk) == true)
                                _reconciler.Raw(vk, (flags & 1) == 0, Environment.TickCount64);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            else if (message == 0x113)
            {
                bool available = _inputAvailable?.Invoke() ?? true;
                if (_desktopAvailable != available)
                {
                    _desktopAvailable = available;
                    _reconciler.Clear();
                    _reset?.Invoke();
                }
                if (!available) return DefWindowProc(window, message, wParam, lParam);
                bool reinstall = false;
                foreach (var (vk, down) in _reconciler.Missing(Environment.TickCount64))
                {
                    _observeRelease?.Invoke(vk, down);
                    // Raw input confirms activity that the hook failed to deliver.
                    // Cancel and reinstall once per observation batch; never start a gesture here.
                    reinstall = true;
                }
                if (reinstall)
                {
                    SessionDiagnostics.Mark(DiagnosticStage.InputRecovery);
                    _reset?.Invoke();
                    if (_hook != IntPtr.Zero) _remove(_hook);
                    _hook = _install(_callback);
                }
            }
            else if (message is 0x218 or 0x2B1) _reset?.Invoke(); // power/session transition
        }
        catch (Exception ex) { NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Input observation failed"); }
        return DefWindowProc(window, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        // Never join a native input pump from the UI or its own callback.
    }
    internal async Task InvokeAsync(Action action)
    {
        await Ready.ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue((action, done));
        if (!PostThreadMessage(_threadId, 0x8001, IntPtr.Zero, IntPtr.Zero))
            done.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
        await done.Task.ConfigureAwait(false);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    { public uint Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? Menu; public string Name; }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice
    { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr window, uint id, uint interval, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, uint id);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
}
