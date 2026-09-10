using System.Collections.ObjectModel;
using FluentFlyout.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace FluentFlyout.App;

public sealed class AppSessionUiItem
{
    public required int ProcessId { get; init; }
    public required string DisplayName { get; init; }
    public double VolumePercent { get; set; }
    public string VolumePercentText => $"{(int)VolumePercent}%";
}

public sealed partial class VolumeMixerWindow : Window
{
    private readonly WindowInteropService interop = new();
    private readonly MonitorService monitorService = new();
    private readonly AudioDeviceService audioDevice = new();
    private readonly AudioSessionService audioSessions;
    private readonly DispatcherQueueTimer hideTimer;
    private readonly ObservableCollection<AppSessionUiItem> sessions = [];

    private bool isExpanded;
    private bool isPointerOver;
    private bool isUpdatingUi;

    public VolumeMixerWindow()
    {
        InitializeComponent();

        audioSessions = new AudioSessionService(audioDevice);
        SessionsList.ItemsSource = sessions;

        hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.Interval = TimeSpan.FromMilliseconds(3000);
        hideTimer.Tick += (s, e) =>
        {
            if (!isPointerOver)
            {
                hideTimer.Stop();
                AppWindow.Hide();
            }
        };

        audioDevice.MasterVolumeChanged += vol =>
        {
            DispatcherQueue.TryEnqueue(() => SyncFromDevice());
        };

        audioDevice.MasterMuteChanged += mute =>
        {
            DispatcherQueue.TryEnqueue(() => SyncFromDevice());
        };

        ConfigureWindow();
        SyncFromDevice();
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
        ResizeAndPosition(false);
    }

    private void ResizeAndPosition(bool expanded)
    {
        int width = 270;
        int height = expanded ? 220 : 54;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        var settings = App.Current.Runtime.Settings.Current;
        var (x, y) = monitorService.CalculatePosition(
            settings.FlyoutSelectedMonitor,
            settings.Position,
            width,
            height);

        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    public void ShowFlyout()
    {
        SyncFromDevice();
        ResizeAndPosition(isExpanded);
        AppWindow.Show();
        hideTimer.Stop();
        hideTimer.Start();
    }

    public void SyncFromDevice()
    {
        isUpdatingUi = true;
        try
        {
            float vol = audioDevice.MasterVolume;
            bool muted = audioDevice.IsMasterMuted;

            int percent = (int)Math.Round(vol * 100);
            MasterSlider.Value = percent;
            PercentText.Text = $"{percent}%";

            if (muted || percent == 0)
            {
                SpeakerIcon.Glyph = "\uE74F"; // Mute icon
            }
            else if (percent < 33)
            {
                SpeakerIcon.Glyph = "\uE992"; // Low volume
            }
            else if (percent < 66)
            {
                SpeakerIcon.Glyph = "\uE993"; // Mid volume
            }
            else
            {
                SpeakerIcon.Glyph = "\uE995"; // High volume
            }
        }
        finally
        {
            isUpdatingUi = false;
        }
    }

    private void MasterSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (isUpdatingUi) return;

        float newVol = (float)(e.NewValue / 100.0);
        audioDevice.MasterVolume = newVol;
        PercentText.Text = $"{(int)e.NewValue}%";

        if (e.NewValue > 0 && audioDevice.IsMasterMuted)
        {
            audioDevice.IsMasterMuted = false;
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        audioDevice.ToggleMute();
        SyncFromDevice();
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        isExpanded = !isExpanded;
        ChevronIcon.Glyph = isExpanded ? "\uE70D" : "\uE70E"; // ChevronUp vs ChevronDown
        SessionsPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;

        if (isExpanded)
        {
            sessions.Clear();
            var active = audioSessions.GetActiveSessions();
            foreach (var item in active)
            {
                sessions.Add(new AppSessionUiItem
                {
                    ProcessId = item.ProcessId,
                    DisplayName = item.DisplayName,
                    VolumePercent = item.Volume * 100
                });
            }
        }

        ResizeAndPosition(isExpanded);
    }

    private void RootContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        isPointerOver = true;
        hideTimer.Stop();
    }

    private void RootContainer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        isPointerOver = false;
        hideTimer.Start();
    }
}
