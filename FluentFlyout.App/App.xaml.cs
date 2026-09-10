using FluentFlyout.Platform.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace FluentFlyout.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private WindowRegistry? _windows;
    private AppRuntime? _runtime;
    private AppWindowManager? _windowManager;
    private AppInstance? _instance;

    public App()
    {
        UnhandledException += OnUnhandledException;
        this.InitializeComponent();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log");
            System.IO.File.WriteAllText(logPath, e.Exception.ToString());
        }
        catch { }
    }

    public static new App Current => (App)Application.Current;

    public WindowRegistry Windows => _windows ??= new WindowRegistry();

    public DispatcherQueue UiQueue => DispatcherQueue.GetForCurrentThread();

    public AppRuntime Runtime => _runtime ?? throw new InvalidOperationException("The application runtime has not been initialized.");

    public AppWindowManager WindowManager => _windowManager ??= new AppWindowManager(
        () => _mainWindow,
        () => GetOrCreateSettingsWindow());

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            try
            {
                _instance = AppInstance.FindOrRegisterForKey("unchihugo.FluentFlyout.WinUI3");
                if (!_instance.IsCurrent)
                {
                    _ = _instance.RedirectActivationToAsync(_instance.GetActivatedEventArgs());
                    return;
                }
            }
            catch
            {
                // Unpackaged fallback if AppInstance registration fails
            }

            _mainWindow ??= new MainWindow();
            Windows.Register("main", _mainWindow);

            _runtime ??= new AppRuntime(
                new XmlSettingsStore(),
                new MediaSessionService(),
                new NativeTrayIconService(() => _mainWindow.Hwnd),
                new AppNotificationService(),
                UiQueue);

            _ = StartRuntimeAsync();

            ShowSettings();

            if (_instance != null)
            {
                _instance.Activated -= OnActivated;
                _instance.Activated += OnActivated;
            }
        }
        catch (Exception ex)
        {
            var logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "launch_error.log");
            try
            {
                System.IO.File.WriteAllText(logPath, ex.ToString());
            }
            catch { }

            Program.WriteStartupLog("OnLaunched.Fatal", ex);
            Program.NativeMessageBox(
                $"FluentFlyout failed to start.\n\n{ex.GetType().Name}: {ex.Message}\n\nDetails written to:\n{logPath}");
        }
    }

    private async Task StartRuntimeAsync()
    {
        try
        {
            await Runtime.StartAsync();

            if (Runtime.Settings.Current.TaskbarWidgetEnabled)
            {
                WindowManager.RecreateTaskbarHost();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"FluentFlyout runtime initialization failed: {exception}");
        }
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        UiQueue.TryEnqueue(() =>
        {
            ShowSettings();
        });
    }

    private SettingsWindow GetOrCreateSettingsWindow()
    {
        if (Windows.TryGet("settings", out var existing) && existing is SettingsWindow settings)
            return settings;

        var window = new SettingsWindow();
        Windows.Register("settings", window);
        return window;
    }

    public void ShowSettings(string? page = null)
    {
        var settings = GetOrCreateSettingsWindow();
        settings.ShowPage(page);
        settings.Activate();
    }
}
