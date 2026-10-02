using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.Pages;
using FluentFlyoutWPF.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using MicaWPF.Core.Services;

// Render the real WPF section and exercise its real gear/menu handlers without
// opening the settings window or changing the user's saved settings.
internal static class DictationUiChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnDispatcher(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("WPF dictation UI check failed", failure);
    }

    private static void RunOnDispatcher()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new MicaWPF.Styles.ThemeDictionary());
        app.Resources.MergedDictionaries.Add(new MicaWPF.Styles.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/FluentFlyout;component/Resources/Localization/Dictionary-en-US.xaml"),
        });
        app.Resources["BoolToVisibleCollapsedConverter"] = new FluentFlyoutWPF.Classes.Converters.BoolToVisibleCollapsedConverter();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        SettingsManager.Current = new UserSettings
        {
            DictationModel = "parakeet-ultra-q4_k.gguf",
            DictationUseGpu = true,
        };
        var page = new DictationPage();
        var models = (FrameworkElement)page.FindName("DictationModelsCard");
        ((Panel)models.Parent).Children.Remove(models);
        page.Content = models;
        string folder = Path.Combine(Path.GetTempPath(), "FluentFlyout-DictationUiChecks");
        Directory.CreateDirectory(folder);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        MicaWPFServiceUtility.ThemeService.ChangeTheme(MicaWPF.Core.Enums.WindowsTheme.Dark);
        page.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        Render(page, 1000, Path.Combine(folder, "models-en-dark.png"));
        Check(models.ActualWidth > 900, "The model section uses the full available width");
        var gear = Gear(page);
        Check(gear != null && gear.Visibility == Visibility.Visible, "The active model has a visible gear button");
        OpenMenu(page, gear!);
        var menu = gear!.ContextMenu!;
        var choices = menu.Items.OfType<MenuItem>().Where(item => item.IsCheckable).ToArray();
        Check(choices.Length == 2 && choices[1].Header.ToString()!.Contains("Integrated"),
            "Parakeet Ultra's menu offers CPU and integrated GPU, with no dedicated option");
        PumpUntil(() => choices[1].IsEnabled || choices[1].Header.ToString()!.Contains("unavailable")
            || choices[1].Header.ToString()!.Contains("Could not"));
        Check(choices[1].IsEnabled && choices[1].Header.ToString()!.Contains("Intel"),
            "The menu discovers the actual integrated Vulkan adapter");
        choices[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(SettingsManager.Current.GetDictationDevice(SettingsManager.Current.DictationModel) == DictationDevice.Cpu,
            "Choosing CPU from the real menu changes the active model's preference");

        SettingsManager.Current.DictationModel = "ggml-large-v3-turbo-q5_0.bin";
        Invoke(page, "RefreshModels");
        Render(page, 1000, Path.Combine(folder, "models-whisper-en-dark.png"));
        gear = Gear(page);
        OpenMenu(page, gear!);
        choices = gear!.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.IsCheckable).ToArray();
        Check(choices.Length == 2 && choices[1].Header.ToString()!.Contains("NVIDIA"),
            "Whisper Turbo's menu offers CPU and dedicated NVIDIA GPU");
        choices[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(SettingsManager.Current.GetDictationDevice("parakeet-ultra-q4_k.gguf") == DictationDevice.Cpu,
            "Configuring Whisper leaves Parakeet Ultra's CPU choice intact");

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/FluentFlyout;component/Resources/Localization/Dictionary-es.xaml"),
        });
        ApplicationThemeManager.Apply(ApplicationTheme.Light);
        MicaWPFServiceUtility.ThemeService.ChangeTheme(MicaWPF.Core.Enums.WindowsTheme.Light);
        SettingsManager.Current.DictationModel = "parakeet-ultra-q4_k.gguf";
        // A fresh disconnected page resolves the new application's resources just as
        // navigating to this page does. It is not an Application-owned live window.
        page = new DictationPage();
        models = (FrameworkElement)page.FindName("DictationModelsCard");
        ((Panel)models.Parent).Children.Remove(models);
        page.Content = models;
        page.Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));
        Render(page, 680, Path.Combine(folder, "models-es-light-narrow.png"));
        Check(Gear(page)?.ActualWidth >= 36, "The gear remains usable at a narrower settings width");
        Console.WriteLine("WPF renders: " + folder);
        app.Shutdown();
    }

    private static void OpenMenu(DictationPage page, System.Windows.Controls.Button button)
    {
        Invoke(page, "ModelSettings_Click", button, new RoutedEventArgs());
        // Inspect the menu offscreen; no interactive native window is needed.
        button.ContextMenu!.IsOpen = false;
    }

    private static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static System.Windows.Controls.Button? Gear(DependencyObject root) => Descendants(root)
        .OfType<System.Windows.Controls.Button>()
        .FirstOrDefault(button => button.Visibility == Visibility.Visible
            && button.Content is SymbolIcon { Symbol: SymbolRegular.Settings24 });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Render(FrameworkElement element, int width, string path)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        element.Width = width;
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        timer.Tick += (_, _) => { if (predicate() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!predicate()) throw new TimeoutException("Integrated adapter discovery timed out");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
