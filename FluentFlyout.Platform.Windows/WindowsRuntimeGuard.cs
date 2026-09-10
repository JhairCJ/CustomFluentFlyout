namespace FluentFlyout.Platform.Windows;

public static class WindowsRuntimeGuard
{
    public const uint Windows11Build = 22000;

    public static bool IsWindows11OrLater =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, (int)Windows11Build);
}
