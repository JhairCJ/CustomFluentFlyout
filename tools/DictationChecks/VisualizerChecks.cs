using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using NAudio.CoreAudioApi.Interfaces;

// Synthetic samples only: no recorder is started and no audio device is changed.
// Runs as part of the existing --taskbar checks on their STA dispatcher.
internal static class VisualizerChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate void Feed(ReadOnlySpan<byte> samples, AudioClientBufferFlags flags, long device, long clock);
    private static object? Read(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static void Write(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static byte[] Pixels(WriteableBitmap bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }
    private static void Frame(Visualizer engine)
    {
        if (!(bool)Read(engine, "_renderLoopActive")!) Call(engine, "StartRenderLoopCore");
        var clock = (Stopwatch)Read(engine, "_renderStopwatch")!;
        Write(engine, "_lastRenderTime", clock.Elapsed.TotalSeconds - 0.1);
        Call(engine, "RenderFrame");
    }

    internal static void Run()
    {
        bool animated = SettingsManager.Current.TaskbarWidgetAnimated;
        SettingsManager.Current.TaskbarWidgetAnimated = false;
        try
        {
            foreach (bool highRefresh in new[] { false, true })
            {
                bool baseline = true, centered = false, autoHide = false, hasContent = false;
                SolidColorBrush accent = Brushes.Coral;
                using var engine = new Visualizer(new Visualizer.Options
                {
                    Enabled = () => false, HighRefreshRate = () => highRefresh,
                    BarCount = () => 10, Baseline = () => baseline, CenteredBars = () => centered,
                    BaselineAutoHide = () => autoHide, Smoothing = () => 0,
                    CornerRadius = () => 0, AccentBrush = () => accent, SetHasContent = value => hasContent = value
                });
                Write(engine, "_enabledRequested", true);
                Call(engine, "StartRenderLoopCore");
                Check(highRefresh ? Read(engine, "_renderTimer") == null
                    : ((System.Windows.Threading.DispatcherTimer)Read(engine, "_renderTimer")!).Interval
                        == TimeSpan.FromMilliseconds(1000.0 / 30), "The selected FPS mode was changed");

                // Losing capture must not suppress release frames or leave old pixels.
                Array.Fill((float[])Read(engine, "_barValues")!, 0.8f);
                Write(engine, "_isRunning", false);
                Frame(engine);
                Check(((float[])Read(engine, "_barValues")!)[0] < 0.8f,
                    "Bars froze during a capture recovery gap");
                for (int i = 0; i < 20; i++) Frame(engine);
                Check(!(bool)Read(engine, "_renderLoopActive")! && Read(engine, "_renderTimer") == null && hasContent,
                    "A settled pinned baseline kept per-frame callbacks alive");
                byte[] bottomBaseline = Pixels(engine.Bitmap!);

                centered = true;
                Call(engine, "InvalidateAppearance"); Frame(engine);
                Check(!bottomBaseline.SequenceEqual(Pixels(engine.Bitmap!)),
                    "Centering did not repaint during silence");
                engine.ResizeBarList(20); Frame(engine);
                Check(((int[])Read(engine, "_prevBarY")!).Length == 20,
                    "A silent bar-count change did not repaint");
                baseline = false;
                Call(engine, "InvalidateAppearance"); Frame(engine);
                Check(Pixels(engine.Bitmap!).All(value => value == 0),
                    "Disabling the baseline left stale pixels behind");
                baseline = true;
                accent = Brushes.Lime;
                Call(engine, "OnAccentChanged", null, EventArgs.Empty); Frame(engine);
                Check((int)Read(engine, "_drawnArgb")! == 0x00FF00 && !(bool)Read(engine, "_renderLoopActive")!,
                    "An idle accent change did not repaint and return to sleep");

                autoHide = true;
                Write(engine, "_lastAudibleUtc", DateTime.UtcNow);
                Call(engine, "InvalidateAppearance"); Frame(engine);
                Check(hasContent && Read(engine, "_autoHideTimer") != null && !(bool)Read(engine, "_renderLoopActive")!,
                    "The auto-hide grace period requires only one timer, not continuous frames");
                Write(engine, "_lastAudibleUtc", DateTime.UtcNow.AddSeconds(-2));
                Call(engine, "OnAutoHideTick", null, EventArgs.Empty); Frame(engine);
                Check(!hasContent && Read(engine, "_autoHideTimer") == null,
                    "The one-shot auto-hide did not settle");

                // Hidden processing must not advance the sample ring or produce FFT targets.
                Write(engine, "_isRunning", true);
                Write(engine, "_sampleRate", 48000);
                Write(engine, "_bytesPerSample", 4);
                var feed = typeof(Visualizer).GetMethod("OnDataAvailable", Private)!.CreateDelegate<Feed>(engine);
                float[] tone = Enumerable.Range(0, 8192)
                    .Select(i => 0.4f * MathF.Sin(2 * MathF.PI * 440 * i / 48000)).ToArray();
                Call(engine, "SetPresentationActive", false);
                int ringPosition = (int)Read(engine, "_ringPos")!;
                feed(MemoryMarshal.AsBytes(tone.AsSpan()), 0, 0, 0);
                Check((int)Read(engine, "_ringPos")! == ringPosition && !(bool)Read(engine, "_renderLoopActive")!,
                    "A hidden host continued processing samples or rendering");
                Call(engine, "SetPresentationActive", true);
                feed(MemoryMarshal.AsBytes(tone.AsSpan()), 0, 0, 0);
                Check(((float[])Read(engine, "_targetValues")!).Any(value => value > 0),
                    "Audio could not wake an auto-hidden baseline after the host returned");
                Frame(engine);
                Check(hasContent, "Audio targets returned but the auto-hidden content remained invisible");
                Call(engine, "SetPresentationActive", false);
                Call(engine, "SetPresentationActive", true);
                feed(new byte[1024], AudioClientBufferFlags.Silent, 0, 0);
                Check(((float[])Read(engine, "_ring")!).All(value => value == 0),
                    "Samples captured before hiding leaked into the resumed spectrum");

                // Also stop while capture is already down: this used to return early.
                using var cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                Write(engine, "_restartCancellation", cancellation);
                Write(engine, "_isRunning", false);
                engine.Stop();
                Check(token.IsCancellationRequested && !(bool)Read(engine, "_renderLoopActive")!
                    && Read(engine, "_autoHideTimer") == null, "Stopping a recovery failed to cancel pending work");
                engine.Dispose(); engine.Dispose(); engine.Start();
                Call(engine, "RequestRestart", "late callback");
                Check(!(bool)Read(engine, "_enabledRequested")!, "A disposed visualizer accepted a new start");
            }

            // The shared Island preset must retain its fixed 30 FPS contract.
            Check(!Visualizer.Options.Island.HighRefreshRate(), "Island unexpectedly inherited the taskbar FPS toggle");
            using var island = new Visualizer(Visualizer.Options.Island);
            Write(island, "_enabledRequested", true);
            Call(island, "StartRenderLoopCore");
            Check(((System.Windows.Threading.DispatcherTimer)Read(island, "_renderTimer")!).Interval
                == TimeSpan.FromMilliseconds(1000.0 / 30), "Island did not start its existing 30 FPS timer");
            Frame(island);
            Check(!(bool)Read(island, "_renderLoopActive")!, "The shared Island engine could not settle into idle");
        }
        finally { SettingsManager.Current.TaskbarWidgetAnimated = animated; }
        Console.WriteLine("Visualizer checks passed: recovery release, idle invalidation, auto-hide, hidden capture and cancellation.");
    }
}
