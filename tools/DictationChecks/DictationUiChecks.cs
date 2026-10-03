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
        Invoke(page, "UpdateCrispAsrRuntimeStatus");
        var models = PrepareSections(page);
        var runtimes = (CardExpander)page.FindName("DictationRuntimesCard");
        Check(!runtimes.IsExpanded && ReferenceEquals(models.Parent, runtimes.Parent),
            "Runtimes have their own collapsed section beside speech models");
        string folder = Path.Combine(Path.GetTempPath(), "FluentFlyout-DictationUiChecks");
        Directory.CreateDirectory(folder);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        MicaWPFServiceUtility.ThemeService.ChangeTheme(MicaWPF.Core.Enums.WindowsTheme.Dark);
        page.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        Render(page, 1000, Path.Combine(folder, "models-en-dark.png"));
        Check(models.ActualWidth > 900, "The model section uses the full available width");
        runtimes.IsExpanded = true;
        Render(page, 1000, Path.Combine(folder, "runtimes-en-dark.png"));
        var expandedContent = (Border)runtimes.Template.FindName("ContentPresenterBorder", runtimes);
        Check(expandedContent.Visibility == Visibility.Visible
            && expandedContent.RenderTransform is TranslateTransform { Y: >= -0.1 },
            "Expanding Runtimes reveals its management controls after the native animation");
        Check(!Descendants(models).Contains(page.FindName("CrispAsrCuda13RuntimeButton")),
            "Runtime controls are outside the speech model list");
        var gear = Gear(page);
        Check(gear != null && gear.Visibility == Visibility.Visible, "The active model has a visible gear button");
        OpenMenu(page, gear!);
        var menu = gear!.ContextMenu!;
        var choices = menu.Items.OfType<MenuItem>().Where(item => item.IsCheckable).ToArray();
        Check(choices.Length == 4 && choices[1].Header.ToString()!.Contains("Integrated")
            && choices[2].Header.ToString()!.Contains("Dedicated"),
            "Parakeet Ultra's menu offers CPU, integrated Vulkan and dedicated NVIDIA CUDA 12 and CUDA 13");
        PumpUntil(() => choices.Skip(1).All(item => item.IsEnabled || item.Header.ToString()!.Contains("unavailable")
            || item.Header.ToString()!.Contains("Could not")));
        Check(choices[1].IsEnabled && choices[1].Header.ToString()!.Contains("Intel"),
            "The menu discovers the actual integrated Vulkan adapter");
        if (choices[2].IsEnabled)
        {
            Check(choices[2].Header.ToString()!.Contains("RTX 3050 Ti")
                && choices[2].Header.ToString()!.Contains("CUDA"),
                "The menu discovers the dedicated RTX adapter with its CUDA label");
            choices[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(SettingsManager.Current.GetDictationDevice(SettingsManager.Current.DictationModel) == DictationDevice.DedicatedGpu,
                "Choosing the dedicated GPU from the real menu saves the Parakeet preference");
        }
        else Check(choices[2].Header.ToString()!.Contains("CUDA 12"),
            "A missing CUDA package disables the dedicated option and explains which dependencies to download");
        bool cuda13Installed = (bool)typeof(ExternalAsrTranscriber).Assembly
            .GetType("FluentFlyoutWPF.Classes.Dictation.CrispAsrRuntime")!
            .GetMethod("IsInstalledFor")!.Invoke(null, [DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13])!;
        Check(choices[3].Header.ToString()!.Contains("CUDA 13") && choices[3].IsEnabled == cuda13Installed,
            "CUDA 13 remains a separate choice requiring its installed package");
        Check(SettingsManager.Current.GetDictationCudaVersion(SettingsManager.Current.DictationModel) == DictationCudaVersion.Cuda12,
            "Opening runtime management preserves the selected CUDA 12 runtime");
        var labels = Descendants(runtimes).OfType<System.Windows.Controls.TextBlock>().Select(label => label.Text).ToArray();
        Check(labels.Any(text => text.Contains("38 MB")) && labels.Any(text => text.Contains("727 MB"))
            && labels.Any(text => text.Contains("511 MB")) && labels.Any(text => text.Contains("426 MB")), "Each runtime row shows its own download size");
        foreach (string prefix in new[] { "WhisperCudaRuntime", "NemoRuntime", "CrispAsrRuntime", "CrispAsrCudaRuntime", "CrispAsrCuda13Runtime" })
        {
            var download = (System.Windows.Controls.Button)page.FindName(prefix + "Button");
            var remove = (System.Windows.Controls.Button)page.FindName(prefix + "RemoveButton");
            Check(download.Visibility != remove.Visibility,
                prefix + " offers exactly one action: download or uninstall");
        }
        Check(((System.Windows.Controls.Button)page.FindName("CrispAsrCuda13RuntimeRemoveButton")).Visibility
            == (cuda13Installed ? Visibility.Visible : Visibility.Collapsed),
            "The downloaded CUDA 13 package can be uninstalled independently");
        typeof(DictationPage).GetField("_runtimeOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, true);
        Invoke(page, "UpdateRuntimeControls");
        Check(!((System.Windows.Controls.Button)page.FindName("CrispAsrCudaRuntimeRemoveButton")).IsEnabled,
            "Runtime actions are disabled while another package operation is running");
        typeof(DictationPage).GetField("_runtimeOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, false);
        Invoke(page, "UpdateRuntimeControls");
        SettingsManager.Current.SetDictationDevice("parakeet-ultra-q4_k.gguf", DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13);
        Invoke(page, "ApplyRemovedRuntimePreferences", DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13);
        Check(SettingsManager.Current.GetDictationCudaVersion("parakeet-ultra-q4_k.gguf") == DictationCudaVersion.Cuda12,
            "Removing selected CUDA 13 falls back to installed CUDA 12");
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
        Invoke(page, "UpdateCrispAsrRuntimeStatus");
        models = PrepareSections(page);
        ((CardExpander)page.FindName("DictationRuntimesCard")).IsExpanded = true;
        page.Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));
        Render(page, 680, Path.Combine(folder, "models-es-light-narrow.png"));
        Check(Gear(page)?.ActualWidth >= 36, "The gear remains usable at a narrower settings width");
        Console.WriteLine("WPF renders: " + folder);
        app.Shutdown();
    }

    private static FrameworkElement PrepareSections(DictationPage page)
    {
        var models = (FrameworkElement)page.FindName("DictationModelsCard");
        var runtimes = (FrameworkElement)page.FindName("DictationRuntimesCard");
        var parent = (Panel)models.Parent;
        parent.Children.Remove(models);
        parent.Children.Remove(runtimes);
        var sections = new StackPanel();
        sections.Children.Add(runtimes);
        sections.Children.Add(models);
        page.Content = sections;
        return models;
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
        // The shipped CardExpander template animates for 333 ms; capture its settled state.
        DateTime settled = DateTime.UtcNow.AddMilliseconds(450);
        PumpUntil(() => DateTime.UtcNow >= settled);
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
