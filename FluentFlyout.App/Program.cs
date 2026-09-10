using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FluentFlyout.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteStartupLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        try
        {
            WriteStartupLog("Main", $"starting unpackaged WinUI host ({RuntimeInformation.ProcessArchitecture})");
            ComWrappersSupportInitialize();
            Application.Start(static _ =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                if (queue != null)
                    SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));

                new App();
            });
            return 0;
        }
        catch (Exception exception)
        {
            WriteStartupLog("Main.Fatal", exception);
            NativeMessageBox(exception.ToString());
            return 1;
        }
    }

    private static void ComWrappersSupportInitialize()
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
        }
        catch (Exception exception)
        {
            WriteStartupLog("ComWrappers", exception);
            throw;
        }
    }

    internal static void WriteStartupLog(string stage, Exception? exception) =>
        WriteStartupLog(stage, exception?.ToString() ?? "unknown error");

    internal static void WriteStartupLog(string stage, string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluentFlyout");
            Directory.CreateDirectory(directory);
            var line = $"[{DateTimeOffset.Now:O}] {stage}: {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "startup.log"), line);
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"), line);
        }
        catch
        {
        }
    }

    internal static void NativeMessageBox(string text)
    {
        try
        {
            MessageBoxW(0, text.Length > 2000 ? text[..2000] : text, "FluentFlyout failed to start", 0x00000010);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
