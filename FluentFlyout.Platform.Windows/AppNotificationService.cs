using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FluentFlyout.Platform.Windows;

/// <summary>Windows App SDK notification adapter; UI activation stays outside this module.</summary>
public sealed class AppNotificationService : IDisposable
{
    private AppNotificationManager? manager;
    private bool registered;

    public event EventHandler<AppNotificationActivatedEventArgs>? Invoked;

    public bool IsSupported
    {
        get
        {
            try
            {
                return AppNotificationManager.IsSupported();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool Register()
    {
        if (!IsSupported)
            return false;
        if (registered)
            return true;

        try
        {
            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += ManagerOnNotificationInvoked;
            manager.Register();
            registered = true;
            return true;
        }
        catch
        {
            manager = null;
            registered = false;
            return false;
        }
    }

    public void Show(string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        if (!Register())
            return;

        var notification = new AppNotificationBuilder()
            .AddText(title)
            .AddText(body)
            .BuildNotification();
        manager?.Show(notification);
    }

    public void Dispose()
    {
        if (!registered || manager is null)
            return;

        try
        {
            manager.NotificationInvoked -= ManagerOnNotificationInvoked;
            manager.Unregister();
        }
        catch
        {
        }

        registered = false;
        manager = null;
        GC.SuppressFinalize(this);
    }

    private void ManagerOnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Invoked?.Invoke(this, args);
}
