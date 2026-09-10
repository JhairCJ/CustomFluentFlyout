using FluentFlyout.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FluentFlyout.App;

public sealed partial class LockWindow : Window
{
    private readonly WindowInteropService interop = new();
    private readonly MonitorService monitorService = new();
    private readonly DispatcherQueueTimer hideTimer;

    public LockWindow(string keyName, bool isEnabled)
    {
        InitializeComponent();

        KeyNameText.Text = keyName;
        StateText.Text = isEnabled ? "On" : "Off";
        LockIcon.Glyph = isEnabled ? "\uE72E" : "\uE785"; // Locked vs Unlocked icon

        hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.Interval = TimeSpan.FromMilliseconds(2000);
        hideTimer.Tick += (s, e) =>
        {
            hideTimer.Stop();
            AppWindow.Hide();
        };

        ConfigureWindow();
    }

    public nint Hwnd => WindowNative.GetWindowHandle(this);

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        interop.SetToolWindow(Hwnd);
        interop.SetNoActivate(Hwnd);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(200, 60));

        // Position at bottom center of primary monitor
        var monitor = monitorService.GetSelectedMonitor(0);
        int x = monitor.WorkArea.Left + (monitor.WorkArea.Width - 200) / 2;
        int y = monitor.WorkArea.Bottom - 80;
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    public void UpdateState(string keyName, bool isEnabled)
    {
        KeyNameText.Text = keyName;
        StateText.Text = isEnabled ? "On" : "Off";
        LockIcon.Glyph = isEnabled ? "\uE72E" : "\uE785";

        AppWindow.Show();
        hideTimer.Stop();
        hideTimer.Start();
    }
}
