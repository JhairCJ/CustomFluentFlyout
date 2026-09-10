using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FluentFlyout.Platform.Windows;

public enum MediaKeyAction
{
    PlayPause,
    NextTrack,
    PreviousTrack,
    Stop
}

public enum VolumeKeyAction
{
    Mute,
    VolumeDown,
    VolumeUp
}

public enum LockKeyType
{
    CapsLock,
    NumLock,
    ScrollLock
}

public sealed class KeyboardHookService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;

    private const int VkCapital = 0x14;
    private const int VkNumlock = 0x90;
    private const int VkScroll = 0x91;

    private const int VkVolumeMute = 0xAD;
    private const int VkVolumeDown = 0xAE;
    private const int VkVolumeUp = 0xAF;

    private const int VkMediaNextTrack = 0xB0;
    private const int VkMediaPrevTrack = 0xB1;
    private const int VkMediaStop = 0xB2;
    private const int VkMediaPlayPause = 0xB3;

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    private readonly HookProc hookDelegate;
    private nint hookId = 0;
    private bool disposed;

    public event Action<MediaKeyAction>? MediaKeyPressed;
    public event Action<VolumeKeyAction>? VolumeKeyPressed;
    public event Action<LockKeyType, bool>? LockKeyPressed;

    public KeyboardHookService()
    {
        hookDelegate = LowLevelKeyboardHandler;
    }

    public void Start()
    {
        if (hookId != 0 || disposed) return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        if (module != null)
        {
            var hMod = GetModuleHandle(module.ModuleName);
            hookId = SetWindowsHookEx(WhKeyboardLl, hookDelegate, hMod, 0);
        }
    }

    public void Stop()
    {
        if (hookId != 0)
        {
            UnhookWindowsHookEx(hookId);
            hookId = 0;
        }
    }

    private nint LowLevelKeyboardHandler(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WmKeydown || wParam == WmSyskeydown))
        {
            int vkCode = Marshal.ReadInt32(lParam);

            switch (vkCode)
            {
                case VkMediaPlayPause:
                    MediaKeyPressed?.Invoke(MediaKeyAction.PlayPause);
                    break;
                case VkMediaNextTrack:
                    MediaKeyPressed?.Invoke(MediaKeyAction.NextTrack);
                    break;
                case VkMediaPrevTrack:
                    MediaKeyPressed?.Invoke(MediaKeyAction.PreviousTrack);
                    break;
                case VkMediaStop:
                    MediaKeyPressed?.Invoke(MediaKeyAction.Stop);
                    break;

                case VkVolumeMute:
                    VolumeKeyPressed?.Invoke(VolumeKeyAction.Mute);
                    break;
                case VkVolumeDown:
                    VolumeKeyPressed?.Invoke(VolumeKeyAction.VolumeDown);
                    break;
                case VkVolumeUp:
                    VolumeKeyPressed?.Invoke(VolumeKeyAction.VolumeUp);
                    break;

                case VkCapital:
                    bool capsOn = (GetKeyState(VkCapital) & 1) != 0;
                    LockKeyPressed?.Invoke(LockKeyType.CapsLock, capsOn);
                    break;
                case VkNumlock:
                    bool numOn = (GetKeyState(VkNumlock) & 1) != 0;
                    LockKeyPressed?.Invoke(LockKeyType.NumLock, numOn);
                    break;
                case VkScroll:
                    bool scrollOn = (GetKeyState(VkScroll) & 1) != 0;
                    LockKeyPressed?.Invoke(LockKeyType.ScrollLock, scrollOn);
                    break;
            }
        }

        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
