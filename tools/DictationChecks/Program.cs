using System.Diagnostics;
using System.IO;
using System.Reflection;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.ViewModels;
using NAudio.Wave;

// Integration checks using the installed CrispASR runtime and a local speech fixture.
// No microphone, text injection, UI, or user settings file is touched.
// dotnet run --project tools/DictationChecks -c Release -- <model.gguf> <speech.wav>
if (args.Length != 2) throw new ArgumentException("Expected model.gguf and 16 kHz mono PCM16 speech.wav");
string modelPath = Path.GetFullPath(args[0]);
var model = DictationModelStore.Find(Path.GetFileName(modelPath))
    ?? throw new ArgumentException("Model must be present in the catalog");
if (model.Backend != DictationModelBackend.CrispAsr) throw new ArgumentException("Expected a CrispASR model");
await DictationModelStore.ValidateIntegrityAsync(modelPath, CancellationToken.None);
using var audio = new WaveFileReader(args[1]);
if (audio.WaveFormat.SampleRate != 16_000 || audio.WaveFormat.Channels != 1
    || audio.WaveFormat.BitsPerSample != 16 || audio.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
    throw new ArgumentException("Fixture must be 16 kHz mono PCM16");
var bytes = new byte[checked((int)audio.Length)];
audio.ReadExactly(bytes);
float[] samples = Enumerable.Range(0, bytes.Length / 2)
    .Select(index => BitConverter.ToInt16(bytes, index * 2) / 32768f).ToArray();

// A fresh settings object remains in initialization mode: setting properties here
// does not invoke MainWindow callbacks or write the user's settings.xml.
SettingsManager.Current = new UserSettings
{
    DictationEnabled = true,
    DictationModel = modelPath,
    DictationUseGpu = false,
    DictationKeepModelLoaded = false,
    DictationUnloadDelaySeconds = 15,
};
using var service = new DictationService();
var worker = Field<ExternalAsrTranscriber>(service, "_externalTranscriber")!;
var settings = SettingsManager.Current;
int passes = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS: " + description);
    passes++;
}
int WorkerPid() => Field<Process>(worker, "_crispProcess")?.Id ?? 0;
async Task WaitForPolicy()
{
    await WaitUntil(() => Field<CancellationTokenSource>(service, "_resourcePolicyCts") == null, TimeSpan.FromSeconds(60));
}

typeof(DictationService).GetProperty(nameof(DictationService.Phase))!.SetValue(service, DictationPhase.Listening);
await Task.Run(() => (Task)typeof(DictationService)
    .GetMethod("PrefetchEngineAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null)!);
Check(service.CrispModelLoaded && service.Phase == DictationPhase.Listening,
    "The real prefetch path loads Parakeet Ultra while recording, before transcription");
typeof(DictationService).GetProperty(nameof(DictationService.Phase))!.SetValue(service, DictationPhase.Idle);
int firstPid = WorkerPid();
Check(worker.RuntimeLoaded && service.CrispModelLoaded && !worker.UsingGpu,
    "Parakeet Ultra loads on CPU when acceleration is disabled");
string first = await worker.TranscribeAsync(model, modelPath, samples, "es", false, CancellationToken.None);
string second = await worker.TranscribeAsync(model, modelPath, samples, "es", false, CancellationToken.None);
Check(first.Length > 0 && first == second && WorkerPid() == firstPid,
    "Repeated dictations reuse one loaded model and return consistent text");

service.RefreshResourceTimeout();
await Task.Delay(500);
settings.DictationKeepModelLoaded = true;
service.RefreshResourcePolicy();
await WaitForPolicy();
Check(WorkerPid() == firstPid, "Enabling Keep Model Loaded retains the current process");
Console.WriteLine("Waiting beyond the 15-second inactivity timeout with Keep Model Loaded enabled...");
await Task.Delay(TimeSpan.FromSeconds(16));
Check(WorkerPid() == firstPid && worker.RuntimeLoaded,
    "Keep Model Loaded cancels the timer and retains the model beyond inactivity");

settings.DictationKeepModelLoaded = false;
service.RefreshResourcePolicy();
await WaitForPolicy();
Check(WorkerPid() == firstPid, "Disabling Keep Model Loaded starts a timer instead of releasing immediately");
await Task.Delay(TimeSpan.FromSeconds(8));
await worker.TranscribeAsync(model, modelPath, samples, "es", false, CancellationToken.None);
service.RefreshResourceTimeout();
await Task.Delay(TimeSpan.FromSeconds(8));
Check(WorkerPid() == firstPid && worker.RuntimeLoaded,
    "Another dictation resets inactivity and survives the original release deadline");
await WaitUntil(() => !worker.RuntimeLoaded, TimeSpan.FromSeconds(10));
Check(!service.CrispModelLoaded && !ProcessExists(firstPid),
    "Inactivity releases the worker process and its model weights");

await worker.TranscribeAsync(model, modelPath, samples, "es", false, CancellationToken.None);
int reloadedPid = WorkerPid();
Check(reloadedPid != firstPid, "A dictation after release creates a fresh worker");
using (var cancelled = new CancellationTokenSource())
{
    Task<string> pending = worker.TranscribeAsync(model, modelPath, samples, "es", false, cancelled.Token);
    cancelled.CancelAfter(20);
    try { await pending; throw new InvalidOperationException("Expected cancellation"); }
    catch (OperationCanceledException) { }
}
Check(!worker.RuntimeLoaded && !ProcessExists(reloadedPid),
    "Cancellation terminates native inference and discards the worker");
await worker.TranscribeAsync(model, modelPath, samples, "es", false, CancellationToken.None);
Check(worker.RuntimeLoaded, "The worker recovers after cancellation");
int disabledPid = WorkerPid();
settings.DictationEnabled = false;
service.RefreshSettings();
await WaitForPolicy();
Check(!worker.RuntimeLoaded && !ProcessExists(disabledPid), "Disabling dictation releases the model");

settings.DictationEnabled = true;
settings.DictationKeepModelLoaded = true;
settings.DictationUseGpu = true;
service.RefreshAccelerationSettings();
await WaitForPolicy();
Check(worker.RuntimeLoaded && service.CrispUsingGpu && worker.MatchesLoadedModel(modelPath, true),
    "Enabling acceleration preloads a persistent Vulkan worker without restarting the app");
int vulkanPid = WorkerPid();
string vulkanFirst = await worker.TranscribeAsync(model, modelPath, samples, "es", true, CancellationToken.None);
string vulkanSecond = await worker.TranscribeAsync(model, modelPath, samples, "es", true, CancellationToken.None);
Check(vulkanFirst.Length > 0 && vulkanSecond.Length > 0 && WorkerPid() == vulkanPid,
    "Vulkan dictations reuse the same loaded process");
settings.DictationUseGpu = false;
service.RefreshAccelerationSettings();
await WaitForPolicy();
Check(worker.RuntimeLoaded && !service.CrispUsingGpu && WorkerPid() != vulkanPid && !ProcessExists(vulkanPid),
    "Disabling acceleration replaces Vulkan with a persistent CPU worker");
int disposedPid = WorkerPid();
service.Dispose();
Check(!ProcessExists(disposedPid), "App disposal leaves no CrispASR worker behind");
Console.WriteLine($"Passed {passes} integration checks.");

static T? Field<T>(object instance, string name) where T : class =>
    (T?)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

static bool ProcessExists(int pid)
{
    try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
{
    var clock = Stopwatch.StartNew();
    while (!predicate())
    {
        if (clock.Elapsed > timeout) throw new TimeoutException("Integration check timed out");
        await Task.Delay(50);
    }
}
