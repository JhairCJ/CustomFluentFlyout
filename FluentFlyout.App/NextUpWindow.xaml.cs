using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using FluentFlyout.Core;
using FluentFlyout.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace FluentFlyout.App;

public sealed partial class NextUpWindow : Window
{
    private readonly WindowInteropService interop = new();
    private readonly MonitorService monitorService = new();
    private readonly DispatcherQueueTimer hideTimer;

    public NextUpWindow(MediaSnapshot snapshot)
    {
        InitializeComponent();

        hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.Interval = TimeSpan.FromMilliseconds(2000);
        hideTimer.Tick += (s, e) =>
        {
            hideTimer.Stop();
            AppWindow.Hide();
        };

        ConfigureWindow();
        UpdateContent(snapshot);
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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(310, 56));

        // Position according to UserSettings
        var settings = App.Current.Runtime.Settings.Current;
        var (x, y) = monitorService.CalculatePosition(
            settings.FlyoutSelectedMonitor,
            settings.Position,
            310,
            56);
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    public async void UpdateContent(MediaSnapshot snapshot)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown track" : snapshot.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(snapshot.Artist) ? "Unknown artist" : snapshot.Artist;

        if (snapshot.ArtworkBytes != null && snapshot.ArtworkBytes.Length > 0)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(snapshot.ArtworkBytes.AsBuffer());
                stream.Seek(0);

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                ArtworkImage.Source = bitmap;
                ArtworkImage.Visibility = Visibility.Visible;
                FallbackIcon.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ArtworkImage.Visibility = Visibility.Collapsed;
                FallbackIcon.Visibility = Visibility.Visible;
            }
        }
        else
        {
            ArtworkImage.Visibility = Visibility.Collapsed;
            FallbackIcon.Visibility = Visibility.Visible;
        }

        AppWindow.Show();
        hideTimer.Stop();
        hideTimer.Start();
    }
}
