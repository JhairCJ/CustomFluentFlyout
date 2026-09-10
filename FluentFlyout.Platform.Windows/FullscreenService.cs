using System.Runtime.InteropServices;

namespace FluentFlyout.Platform.Windows;

public sealed class FullscreenService
{
    private enum QueryUserNotificationState
    {
        QunsNotPresent = 1,
        QunsBusy = 2,
        QunsRunningD3dFullScreen = 3,
        QunsPresentationMode = 4,
        QunsAcceptsNotifications = 5,
        QunsQuietTime = 6,
        QunsApp = 7
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QueryUserNotificationState pquns);

    public bool IsFullscreenAppRunning()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) == 0)
            {
                return state == QueryUserNotificationState.QunsRunningD3dFullScreen;
            }
        }
        catch
        {
            // Fallback if query fails
        }

        return false;
    }
}
