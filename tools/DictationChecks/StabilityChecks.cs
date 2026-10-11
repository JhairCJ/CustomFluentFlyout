using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Serialization;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.ViewModels;
using NAudio.Wave;

internal static class StabilityChecks
{
    internal static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > 3000) throw new TimeoutException("Check deadline"); await Task.Delay(2); }
    }
    private static UserSettings Settings(bool keep = false) => new()
    { DictationEnabled = true, DictationHotkey = "Ctrl+Shift", DictationToggleMode = false, DictationKeepModelLoaded = keep };

    internal static async Task Capture()
    {
        var devices = new List<Device>();
        var capture = new DictationCapture(() =>
        {
            Check(devices.Count == 0 || devices[^1].Disposed == 1, "Reopened a live device");
            var device = new Device(); devices.Add(device); return device;
        }, TimeSpan.FromSeconds(5));
        for (int i = 0; i < 100; i++)
        {
            await capture.StartAsync();
            devices[^1].Emit();
            var stop = capture.StopAsync();
            await Until(() => devices[^1].StopCalls > 0);
            Check(!stop.IsCompleted && devices[^1].Disposed == 0, "Disposed before RecordingStopped");
            if (i > 0) devices[i - 1].EmitStale();
            devices[^1].Confirm();
            using var recording = await stop;
            Check(recording?.Length == 32 && devices[^1].Disposed == 1, "Audio ownership or single disposal failed");
        }
        await capture.DisposeAsync();

        var quarantined = new Device(); int opens = 0;
        capture = new(() => { opens++; return quarantined; }, TimeSpan.FromMilliseconds(25));
        await capture.StartAsync();
        try { await capture.StopAsync(); throw new Exception("Missing timeout"); } catch (TimeoutException) { }
        Check(quarantined.Disposed == 0 && capture.HasDevice, "Timeout freed active buffers");
        try { await capture.StartAsync(); throw new Exception("Quarantine allowed an open"); }
        catch (InvalidOperationException) { }
        Check(opens == 1, "Quarantine accumulated devices");
        quarantined.Confirm();
        await Until(() => !capture.HasDevice);
        Check(quarantined.Disposed == 1, "Late stop confirmation did not release exactly once");
        await capture.DisposeAsync();

        var failing = new Device { ThrowStop = true };
        capture = new(() => failing, TimeSpan.FromSeconds(5));
        await capture.StartAsync();
        failing.Emit();
        var failedStop = capture.StopAsync();
        await Until(() => failing.StopCalls > 0);
        Check(failing.Disposed == 0, "Stop exception freed unsafe buffers");
        failing.Confirm(new IOException("Driver error"));
        using var errorRecording = await failedStop;
        Check(capture.LastDeviceError != null && errorRecording?.Length == 32, "Driver error lost its valid PCM or error context");
        Check(failing.Disposed == 1, "Failed capture was not released after confirmation");
        await capture.DisposeAsync();

        // Watchdog and user stop share the same operation gate; stopping invalidates reopen.
        var watchdogDevice = new Device(); int watchdogOpens = 0;
        capture = new(() => { watchdogOpens++; return watchdogDevice; });
        await capture.StartAsync(); watchdogDevice.Emit();
        typeof(DictationCapture).GetField("_lastDataTick", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(capture, 0L);
        var revive = (Task)typeof(DictationCapture).GetMethod("ReviveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(capture, null)!;
        await Until(() => watchdogDevice.StopCalls > 0);
        var racingStop = capture.StopAsync(); watchdogDevice.Confirm();
        await revive;
        using var saved = await racingStop;
        Check(watchdogOpens == 1 && saved?.Length == 32, "Watchdog resurrected a stopped session or lost PCM");
        await capture.DisposeAsync();
        Console.WriteLine("Capture checks passed: 100 delayed stops, stale callback, driver error, quarantine/late confirmation, watchdog cancellation.");
    }

    internal static async Task CaptureDevice()
    {
        if (WaveIn.DeviceCount == 0) { Console.WriteLine("Real microphone check unavailable: no input devices."); return; }
        var capture = new DictationCapture();
        long bytes = 0;
        for (int i = 0; i < 100; i++)
        {
            await Task.Run(capture.StartAsync);
            await Task.Delay(i == 0 ? 300 : 20);
            using var audio = await capture.StopAsync();
            bytes += audio?.Length ?? 0;
            Check(!capture.HasDevice, "Real device did not drain and release");
        }
        await capture.DisposeAsync();
        Console.WriteLine($"Real microphone checks passed: 100 start/stop cycles, {bytes} PCM bytes received and deleted; no recognition or text injection.");
    }

    internal static async Task Input()
    {
        SettingsManager.Current = Settings();
        using var opening = new ManualResetEventSlim();
        var device = new Device { OpenGate = opening, AutoStop = true };
        var service = new DictationService(new(() => device), () => "test-model", () => Task.CompletedTask)
        { InputSender = inputs => (uint)inputs.Length, ModifiersHeld = () => false };
        var clock = Stopwatch.StartNew();
        Check(!service.HandleKey(0xA2, true), "Prefix modifier must pass");
        Check(service.HandleKey(0xA0, true), "Completing modifier belongs to dictation");
        Check(clock.ElapsedMilliseconds < 50, "Device open ran inside the input callback");
        await Until(() => device.StartCalls == 1);
        Check(service.HandleKey(0x1B, true), "Escape must cancel an opening session");
        Check(service.HandleKey(0x1B, false), "Escape release must be paired");
        service.HandleKey(0xA0, false); service.HandleKey(0xA2, false);
        await Task.Delay(2000); // Device is deliberately stuck; input calls must remain fast.
        clock.Restart(); service.HandleKey(0x41, true); service.HandleKey(0x41, false);
        Check(clock.ElapsedMilliseconds < 50, "Blocked open stopped the keyboard");
        opening.Set(); await service.DrainCommandsAsync();
        Check(service.Phase != DictationPhase.Listening && device.Disposed == 1, "Cancelled opening reactivated recording");
        Check(!service.HandleKey(0xA2, true, injected: true), "Injected input activated dictation");
        service.Dispose(); await service.Completion;

        // A release or disable during opening must never publish Listening or preload engines.
        foreach (bool disable in new[] { false, true })
        {
            SettingsManager.Current = Settings();
            using var gate = new ManualResetEventSlim();
            var pendingDevice = new Device { OpenGate = gate, AutoStop = true };
            int listening = 0, prefetched = 0;
            service = new DictationService(new(() => pendingDevice), () => "test-model",
                () => { prefetched++; return Task.CompletedTask; });
            service.Changed += () => { if (service.Phase == DictationPhase.Listening) listening++; };
            service.HandleKey(0xA2, true); service.HandleKey(0xA0, true);
            await Until(() => pendingDevice.StartCalls == 1);
            if (disable) { SettingsManager.Current.DictationEnabled = false; service.RefreshSettings(); }
            service.HandleKey(0xA0, false); service.HandleKey(0xA2, false);
            gate.Set(); await service.DrainCommandsAsync();
            Check(listening == 0 && prefetched == 0 && pendingDevice.Disposed == 1,
                "Released/disabled opening published a stale session");
            service.Dispose(); await service.Completion;
        }
        SettingsManager.Current = Settings();

        // Every command traverses the actual service FIFO, with no mic/models/input.
        int opened = 0;
        service = new DictationService(new(() => { opened++; return new Device { AutoStop = true }; }),
            () => "test-model", () => Task.CompletedTask) { InputSender = _ => 0, ModifiersHeld = () => false };
        for (int i = 0; i < 100; i++)
        {
            service.HandleKey(0xA2, true); service.HandleKey(0xA0, true);
            service.HandleKey(0xA0, false); service.HandleKey(0xA2, false);
        }
        await service.DrainCommandsAsync();
        Check(!service.Active, "Rapid gestures left an active microphone");
        SettingsManager.Current.DictationEnabled = false; service.RefreshSettings();
        await service.DrainCommandsAsync(); service.Dispose(); await service.Completion;

        // The native pump is alive while this caller (a potential WPF UI thread) is blocked.
        using var input = new KeyboardInputThread((_, _, _) => IntPtr.Zero);
        await input.Ready.WaitAsync(TimeSpan.FromSeconds(3));
        Thread.Sleep(2000);
        Check(input.IsAlive, "Input pump required the WPF Dispatcher");
        input.Dispose(); input.Dispose(); await input.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        int installs = 0, removals = 0, resets = 0;
        FluentFlyout.Classes.NativeMethods.LowLevelKeyboardProc? callback = null;
        using var boundary = new KeyboardInputThread((_, _, _) => throw new IOException("Routing failure"),
            () => resets++, install: proc => { callback = proc; installs++; return new IntPtr(1); },
            remove: _ => removals++, next: (_, _, _, _) => new IntPtr(7));
        await boundary.Ready;
        await boundary.InvokeAsync(() =>
        {
            var elapsed = Stopwatch.StartNew();
            Check(callback!(0, IntPtr.Zero, IntPtr.Zero) == new IntPtr(7), "Exception did not pass input to the next hook");
            Check(resets == 1 && elapsed.ElapsedMilliseconds < 50, "Exception recovery blocked input or failed to invalidate gesture");
        });
        boundary.Dispose(); boundary.Dispose(); await boundary.Completion;
        Check(installs == 1 && removals == 1 && resets == 2, "Hook/delegate ownership teardown was not idempotent");
        await GestureModes();
        await Insertion();
        await Teardown();
        Console.WriteLine($"Input checks passed: blocked opening, Escape, injected input, FIFO 100 gestures ({opened} opens), independent native pump, repeated teardown.");
    }

    private static async Task GestureModes()
    {
        foreach (bool toggle in new[] { false, true })
        foreach (bool hold in new[] { false, true })
        {
            SettingsManager.Current = Settings(); SettingsManager.Current.DictationToggleMode = toggle;
            var service = new DictationService(new(() => new Device { AutoStop = true }), () => "test-model", () => Task.CompletedTask)
            { InputSender = inputs => (uint)inputs.Length, ModifiersHeld = () => false };
            service.HandleKey(0xA0, true); service.HandleKey(0xA2, true);
            await service.DrainCommandsAsync();
            if (hold) await Task.Delay(400);
            service.HandleKey(0xA2, false); service.HandleKey(0xA0, false);
            await service.DrainCommandsAsync();
            if (toggle && !hold)
            {
                Check(service.HandsFree, "Short toggle gesture did not latch hands-free");
                service.HandleKey(0xA0, true); service.HandleKey(0xA2, true);
                service.HandleKey(0x43, true); service.HandleKey(0x43, false);
                service.HandleKey(0xA0, false); service.HandleKey(0xA2, false);
                await service.DrainCommandsAsync();
                Check(service.HandsFree, "Foreign shortcut ended hands-free dictation");
                service.HandleKey(0xA2, true); service.HandleKey(0xA0, true);
                service.HandleKey(0xA2, false); service.HandleKey(0xA0, false);
                await service.DrainCommandsAsync();
            }
            Check(!service.Active, "Hold threshold/toggle stop left a live session");
            service.Dispose(); await service.Completion;
        }
    }

    private static async Task Insertion()
    {
        SettingsManager.Current = Settings();
        int sent = 0, recovered = 0;
        var service = new DictationService(new(() => new Device { AutoStop = true }))
        { InputSender = inputs => { sent += inputs.Length; return (uint)inputs.Length; },
            ModifiersHeld = () => false, InputStateAvailable = () => true, RecoveryWriter = _ => recovered++ };
        var cts = new CancellationTokenSource();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(DictationService).GetField("_transcriptionCts", flags)!.SetValue(service, cts);
        var write = typeof(DictationService).GetMethod("WriteDictatedTextAsync", flags)!;
        await (Task)write.Invoke(service, ["Synthetic test text", cts.Token, cts])!;
        Check(sent > 0 && recovered == 1, "Successful synthetic insertion lacked recovery copy");
        sent = recovered = 0;
        service.ModifiersHeld = () => true;
        var clock = Stopwatch.StartNew();
        await (Task)write.Invoke(service, ["Synthetic test text", cts.Token, cts])!;
        Check(clock.ElapsedMilliseconds is >= 5000 and < 6500 && sent == 0 && recovered == 1,
            "Held-key five-second deadline or recovery copy failed");
        var sendText = typeof(DictationService).GetMethod("SendText", flags)!;
        var batches = new List<FluentFlyout.Classes.NativeMethods.INPUT[]>();
        service.InputSender = inputs => { batches.Add(inputs); return batches.Count == 1 ? 1u : (uint)inputs.Length; };
        sendText.Invoke(service, ["X"]);
        Check(batches.Count == 2 && batches[1].Length == 1 && batches[1][0].ki.wScan == 'X'
            && (batches[1][0].ki.dwFlags & FluentFlyout.Classes.NativeMethods.KEYEVENTF_KEYUP) != 0,
            "Partial Unicode delivery did not repair exactly its synthetic down");
        batches.Clear(); service.HandleKey(0x0D, true);
        sendText.Invoke(service, ["\n"]);
        Check(batches.Count == 1, "Partial text delivery released a physically held Enter");
        service.HandleKey(0x0D, false);
        service.InputSender = _ => 0; service.ModifiersHeld = () => false; recovered = 0;
        await (Task)write.Invoke(service, ["Denied", cts.Token, cts])!;
        Check(recovered == 1, "Denied text delivery lost its recovery copy");
        service.ModifiersHeld = () => false; recovered = 0;
        cts.Cancel(); await (Task)write.Invoke(service, ["Cancelled", cts.Token, cts])!;
        Check(sent == 0 && recovered == 0, "Cancelled session inserted text or wrote recovery late");
        service.Dispose(); await service.Completion;
    }

    private static async Task Teardown()
    {
        SettingsManager.Current = Settings();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new Device { AutoStop = true };
        var service = new DictationService(new(() => device), () => "test-model", async () =>
        { running.TrySetResult(); await finish.Task; });
        service.Start(); await service.DrainCommandsAsync(); await running.Task;
        service.Dispose(); service.Dispose(); await Task.Delay(100);
        Check(!service.Completion.IsCompleted && device.Disposed == 1,
            "Teardown released engine resources before a live prefetch ended, or failed to close capture");
        finish.SetResult(); await service.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }

    internal static async Task Diagnostics(string[] args)
    {
        SettingsManager.Current = Settings();
        var preferences = new XmlSerializer(typeof(UserSettings));
        using var before = new StringWriter(); preferences.Serialize(before, SettingsManager.Current);
        App.ConfigureRendering(args);
        using var after = new StringWriter(); preferences.Serialize(after, SettingsManager.Current);
        Check(before.ToString() == after.ToString(), "Rendering mode changed saved animation or recognition preferences");
        bool software = System.Windows.Media.RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly;
        Check(software == args.Contains("--software-rendering"), "Rendering flag did not apply before window creation");
        string folder = Path.Combine(Path.GetTempPath(), "FluentFlyoutStability-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(folder, $"session-old-{i}.jsonl"), "{}");
            using (var log = new BoundedSessionDiagnostics(folder, software, 4096))
            {
                var resources = log.Recent.Single(entry => entry.Stage == DiagnosticStage.Resources).Resources;
                Check(resources is { PrivateBytes: > 0, CommitLimitBytes: > 0, AvailableCommitBytes: > 0 },
                    "Resource diagnostics did not capture process and system memory");
                for (int i = 0; i < 10000; i++) { log.Mark(DiagnosticStage.Phase, i); if (i % 64 == 0) log.Flush(); }
                Check(log.Recent.Length == 64 && log.Recent[^1].Generation == 9999, "Recent milestones are not bounded/last wins");
                log.Flush();
            }
            Check(Directory.GetFiles(folder).Length == 10, "Diagnostics retention exceeded ten sessions");
            Check(Directory.GetFiles(folder).All(file => new FileInfo(file).Length <= 4096), "Diagnostics byte cap failed");
            Check(Directory.GetFiles(folder).Any(file => File.ReadLines(file).FirstOrDefault()?.Contains("\"BuildId\"") == true),
                "Diagnostics did not identify the build used during the session");
            Check(Directory.GetFiles(folder).Any(file => File.ReadLines(file).Any(line => line.Contains("\"Generation\":9999"))),
                "Bounded diagnostics lost the latest context after rolling its file");
            string blocker = Path.Combine(folder, "blocked"); File.WriteAllText(blocker, "");
            using var unavailable = new BoundedSessionDiagnostics(Path.Combine(blocker, "invalid"), software);
            unavailable.Mark(DiagnosticStage.Fatal); unavailable.Flush();
            Check(SessionDiagnostics.IsRenderFailure(new COMException("Test", unchecked((int)0x88980406)))
                && !SessionDiagnostics.IsRenderFailure(new IOException()), "Fatal rendering classification failed");

            string source = Path.Combine(folder, "share-source");
            string diagnostics = Path.Combine(source, "diagnostics");
            Directory.CreateDirectory(diagnostics);
            for (int i = 0; i < 12; i++)
            {
                string file = Path.Combine(source, $"logs.{i}.txt");
                File.WriteAllText(file, $"log {i}");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(i - 10));
            }
            for (int i = 0; i < 12; i++)
            {
                string file = Path.Combine(diagnostics, $"session-{i}.jsonl");
                File.WriteAllText(file, $"session {i}");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(i - 10));
            }
            File.WriteAllText(Path.Combine(source, "settings.xml"), "private settings");
            File.WriteAllText(Path.Combine(source, "transcription.txt"), "private text");
            string large = Path.Combine(source, "logs.large.txt");
            File.WriteAllBytes(large, new byte[2 * 1024 * 1024 + 10]);
            File.SetLastWriteTimeUtc(large, DateTime.UtcNow);
            string package = DiagnosticPackage.Create(source, Path.Combine(folder, "share-output"), DateTime.Now);
            using (var archive = ZipFile.OpenRead(package))
            {
                Check(archive.Entries.Count == 21 && archive.Entries.Any(entry => entry.Name == "README.txt"),
                    "Diagnostic package did not contain the selected logs and session files");
                Check(archive.Entries.All(entry => !entry.Name.Contains("settings") && !entry.Name.Contains("transcription")),
                    "Diagnostic package included private application data");
                Check(archive.Entries.Single(entry => entry.Name == "logs.large.txt").Length == 2 * 1024 * 1024,
                    "Diagnostic package failed to cap a large log");
            }
        }
        finally { Directory.Delete(folder, true); }

        SettingsManager.Current = Settings(); int captures = 0, preload = 0;
        var service = new DictationService(new(() => { captures++; return new Device(); }),
            () => "test-model", load: _ => { preload++; return Task.CompletedTask; });
        service.Preload(); await Task.Delay(100);
        Check(captures == 0 && preload == 0 && !service.RuntimeLoaded, "Automatic startup opened the mic or loaded engines");
        service.Dispose(); await service.Completion;
        SettingsManager.Current = Settings(keep: true);
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service = new DictationService(new(() => { captures++; return new Device(); }),
            () => "test-model", load: async token => { preload++; await loading.Task.WaitAsync(token); });
        service.Preload(); await Until(() => preload == 1);
        Check(captures == 0 && !service.HandleKey(0x41, true), "Keep-loaded preload interfered with capture or typing");
        service.Dispose(); await service.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(captures == 0, "Teardown opened capture");
        SettingsManager.Current = Settings(keep: true);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service = new DictationService(new(() => { captures++; return new Device(); }),
            () => "test-model", load: _ => { failed.SetResult(); throw new IOException("Synthetic preload failure"); });
        service.Preload(); await failed.Task; await Task.Delay(50);
        Check(captures == 0 && !service.HandleKey(0x41, true), "Failed preload opened capture or swallowed typing");
        service.Dispose(); await service.Completion;
        Console.WriteLine($"Diagnostics checks passed: 64 milestones, 10 sessions, byte cap, failed writes, fatal classification, startup spies; software={software}.");
    }

    internal sealed class Device : IDictationCaptureDevice
    {
        private EventHandler<WaveInEventArgs>? _data, _oldData;
        public event EventHandler<WaveInEventArgs>? DataAvailable
        { add { _data += value; _oldData = value; } remove => _data -= value; }
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        internal int StartCalls, StopCalls, Disposed;
        internal bool Confirmed, ThrowStop, AutoStop;
        internal ManualResetEventSlim? OpenGate;
        public void StartRecording() { Interlocked.Increment(ref StartCalls); OpenGate?.Wait(); }
        public void StopRecording()
        {
            Interlocked.Increment(ref StopCalls);
            if (ThrowStop) throw new IOException("Driver request failure");
            if (AutoStop) Confirm();
        }
        internal void Emit() => _data?.Invoke(this, new(new byte[32], 32));
        internal void EmitStale() => _oldData?.Invoke(this, new(new byte[32], 32));
        internal void Confirm(Exception? error = null) { Confirmed = true; RecordingStopped?.Invoke(this, new(error)); }
        public void Dispose() { Check(Confirmed, "Disposal preceded confirmed recording stop"); Check(++Disposed == 1, "Double disposal"); }
    }
}
