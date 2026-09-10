using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using FluentFlyout.Core;
using FluentFlyout.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace FluentFlyout.App;

public sealed partial class MainWindow : Window
{
    private readonly WindowInteropService interop = new();
    private readonly MonitorService monitorService = new();
    private readonly DispatcherQueueTimer hideTimer;
    private bool isPointerOver;

    public MainWindow()
    {
        InitializeComponent();

        hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.Tick += (s, e) =>
        {
            if (!isPointerOver && !App.Current.Runtime.Settings.Current.MediaFlyoutAlwaysDisplay)
            {
                hideTimer.Stop();
                AppWindow.Hide();
            }
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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(330, 110));
    }

    public void ShowFlyout(int durationMs = 3000)
    {
        UpdatePosition();
        AppWindow.Show();

        hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, durationMs));
        hideTimer.Stop();
        if (!App.Current.Runtime.Settings.Current.MediaFlyoutAlwaysDisplay)
        {
            hideTimer.Start();
        }
    }

    public void UpdatePosition()
    {
        var settings = App.Current.Runtime.Settings.Current;
        int width = settings.CompactLayout ? 310 : 330;
        int height = settings.SeekbarEnabled ? 140 : 106;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        var (x, y) = monitorService.CalculatePosition(
            settings.FlyoutSelectedMonitor,
            settings.Position,
            width,
            height);

        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    public async void UpdateMedia(MediaSnapshot snapshot)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "Unknown track" : snapshot.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(snapshot.Artist) ? "Unknown artist" : snapshot.Artist;

        // Play/Pause icon
        PlayPauseIcon.Glyph = snapshot.PlaybackState == MediaPlaybackState.Playing ? "\uE769" : "\uE768";

        // Artwork
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

                if (App.Current.Runtime.Settings.Current.MediaFlyoutBackgroundBlur > 0)
                {
                    BackgroundBlurImage.Source = bitmap;
                    BackgroundBlurImage.Visibility = Visibility.Visible;
                }
                else
                {
                    BackgroundBlurImage.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                ArtworkImage.Visibility = Visibility.Collapsed;
                FallbackIcon.Visibility = Visibility.Visible;
                BackgroundBlurImage.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            ArtworkImage.Visibility = Visibility.Collapsed;
            FallbackIcon.Visibility = Visibility.Visible;
            BackgroundBlurImage.Visibility = Visibility.Collapsed;
        }

        // Timeline
        if (App.Current.Runtime.Settings.Current.SeekbarEnabled && snapshot.Duration.TotalSeconds > 0)
        {
            SeekbarPanel.Visibility = Visibility.Visible;
            TimelineSlider.Maximum = snapshot.Duration.TotalSeconds;
            TimelineSlider.Value = Math.Clamp(snapshot.Position.TotalSeconds, 0, snapshot.Duration.TotalSeconds);
            CurrentTimeText.Text = DurationFormatting.Format(snapshot.Position);
            TotalDurationText.Text = DurationFormatting.Format(snapshot.Duration);
        }
        else
        {
            SeekbarPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void RootContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        isPointerOver = true;
        hideTimer.Stop();
    }

    private void RootContainer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        isPointerOver = false;
        if (!App.Current.Runtime.Settings.Current.MediaFlyoutAlwaysDisplay)
        {
            hideTimer.Start();
        }
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        await App.Current.Runtime.Media.PlayPauseAsync();
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        await App.Current.Runtime.Media.PreviousAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await App.Current.Runtime.Media.NextAsync();
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggle repeat visual
        RepeatIcon.Opacity = RepeatIcon.Opacity > 0.8 ? 0.5 : 1.0;
    }

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggle shuffle visual
        ShuffleIcon.Opacity = ShuffleIcon.Opacity > 0.8 ? 0.5 : 1.0;
    }

    private async void TimelineSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Math.Abs(e.NewValue - e.OldValue) > 1.0)
        {
            var target = TimeSpan.FromSeconds(e.NewValue);
            CurrentTimeText.Text = DurationFormatting.Format(target);
            await App.Current.Runtime.Media.SeekAsync(target);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        hideTimer.Stop();
        AppWindow.Hide();
    }
}
