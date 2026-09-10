namespace FluentFlyout.Core;

public interface IWindowManager
{
    bool IsMainWindowVisible { get; }

    void ShowMediaFlyout();

    void ShowSettings(string? page = null);

    void ShowOnboarding();

    void ShowNextUp(MediaSnapshot snapshot);

    void ShowLockKeys(string key, bool enabled);

    void ShowVolumeMixer();

    void RecreateTaskbarHost();

    void ShowMainWindow();

    void HideMainWindow();

    void ToggleMainWindow();
}
