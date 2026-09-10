namespace FluentFlyout.Core;

public interface ITrayIconService
{
    bool IsVisible { get; }

    void Show();

    void Hide();

    void SetToolTip(string text);
}
