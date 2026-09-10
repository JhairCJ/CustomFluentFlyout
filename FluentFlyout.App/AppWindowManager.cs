using FluentFlyout.Core;

namespace FluentFlyout.App;

public sealed class AppWindowManager : IWindowManager
{
    private readonly Func<MainWindow?> mainWindow;
    private readonly Func<SettingsWindow> settingsWindow;

    public AppWindowManager(Func<MainWindow?> mainWindow, Func<SettingsWindow> settingsWindow)
    {
        this.mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        this.settingsWindow = settingsWindow ?? throw new ArgumentNullException(nameof(settingsWindow));
    }

    public bool IsMainWindowVisible => mainWindow()?.AppWindow.IsVisible ?? false;

    public void ShowMediaFlyout()
    {
        var main = mainWindow();
        if (main != null)
        {
            var duration = App.Current.Runtime.Settings.Current.Duration;
            main.ShowFlyout(duration);
        }
    }

    public void ShowSettings(string? page = null)
    {
        App.Current.ShowSettings(page);
    }

    public void ShowOnboarding()
    {
        if (App.Current.Windows.TryGet("onboarding", out var existing) && existing is OnboardingWindow onb)
        {
            onb.Activate();
            return;
        }

        var window = new OnboardingWindow();
        App.Current.Windows.Register("onboarding", window);
        window.Activate();
    }

    public void ShowNextUp(MediaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (App.Current.Windows.TryGet("next-up", out var existing) && existing is NextUpWindow nextUp)
        {
            nextUp.UpdateContent(snapshot);
            return;
        }

        var window = new NextUpWindow(snapshot);
        App.Current.Windows.Register("next-up", window);
    }

    public void ShowLockKeys(string key, bool enabled)
    {
        if (App.Current.Windows.TryGet("lock-keys", out var existing) && existing is LockWindow lockWin)
        {
            lockWin.UpdateState(key, enabled);
            return;
        }

        var window = new LockWindow(key, enabled);
        App.Current.Windows.Register("lock-keys", window);
    }

    public void ShowVolumeMixer()
    {
        if (App.Current.Windows.TryGet("volume-mixer", out var existing) && existing is VolumeMixerWindow mixer)
        {
            mixer.ShowFlyout();
            return;
        }

        var window = new VolumeMixerWindow();
        App.Current.Windows.Register("volume-mixer", window);
        window.ShowFlyout();
    }

    public void RecreateTaskbarHost()
    {
        if (App.Current.Windows.TryGet("taskbar-host", out var existing))
            existing?.Close();

        var window = new TaskbarHostWindow();
        App.Current.Windows.Register("taskbar-host", window);
    }

    public void UpdateMedia(MediaSnapshot snapshot)
    {
        mainWindow()?.UpdateMedia(snapshot);
        if (App.Current.Windows.TryGet("taskbar-host", out var hostWin) && hostWin is TaskbarHostWindow host)
        {
            host.UpdateMedia(snapshot);
        }
    }

    public void ShowMainWindow() => ShowMediaFlyout();

    public void HideMainWindow() => mainWindow()?.AppWindow.Hide();

    public void ToggleMainWindow()
    {
        if (IsMainWindowVisible)
            HideMainWindow();
        else
            ShowMainWindow();
    }
}
