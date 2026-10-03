using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.ViewModels;
using NAudio.Wave;

// Integration checks using the installed CrispASR runtime and a local speech fixture.
// No microphone, text injection, UI, or user settings file is touched.
// dotnet run --project tools/DictationChecks -c Release -- <model.gguf> <speech.wav>
if (args is ["--whisper-benchmark", var whisperPath, var whisperAudio, var whisperDevice])
{
    await WhisperInferenceChecks.Run(whisperPath, whisperAudio, whisperDevice == "gpu");
    return;
}
if (args is ["--whisper-runtime"])
{
    await WhisperCudaRuntimeChecks.Run();
    return;
}
if (args is ["--runtime-removal"])
{
    RuntimeRemovalChecks.Run();
    return;
}
if (args is ["--ui"])
{
    DictationUiChecks.Run();
    return;
}
bool cudaSwitch = args.Length == 3 && args[0] == "--cuda-switch";
if (cudaSwitch) args = args[1..3];
bool benchmark = args.Length is 3 or 4 or 5 && args[0] == "--benchmark";
DictationDevice[] benchmarkDevices = benchmark && args.Length >= 4
    ? [Enum.Parse<DictationDevice>(args[3])] : [DictationDevice.Cpu, DictationDevice.IntegratedGpu, DictationDevice.DedicatedGpu];
DictationCudaVersion benchmarkCudaVersion = benchmark && args.Length == 5
    ? Enum.Parse<DictationCudaVersion>("Cuda" + args[4]) : DictationCudaVersion.Cuda12;
if (benchmark) args = args[1..3];
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
if (cudaSwitch)
{
    settings.DictationKeepModelLoaded = true;
    int previousPid = 0;
    string? previousText = null;
    foreach (var version in new[] { DictationCudaVersion.Cuda12, DictationCudaVersion.Cuda13, DictationCudaVersion.Cuda12 })
    {
        settings.SetDictationDevice(modelPath, DictationDevice.DedicatedGpu, version);
        service.RefreshAccelerationSettings();
        await WaitUntil(() => Field<CancellationTokenSource>(service, "_resourcePolicyCts") == null, TimeSpan.FromSeconds(60));
        var process = Field<Process>(worker, "_crispProcess") ?? throw new InvalidOperationException("CUDA worker did not load");
        if (service.LoadedCudaVersion != version || !worker.MatchesLoadedModel(modelPath, DictationDevice.DedicatedGpu, version)
            || process.Id == previousPid || (previousPid != 0 && ProcessExists(previousPid)))
            throw new InvalidOperationException("CUDA version change did not replace the native worker correctly");
        Console.WriteLine($"PASS: {version} preference loads a distinct worker and releases its predecessor");
        string text = await worker.TranscribeAsync(model, modelPath, samples, "en", DictationDevice.DedicatedGpu, CancellationToken.None, version);
        if (string.IsNullOrWhiteSpace(text) || (previousText != null && text != previousText))
            throw new InvalidOperationException("Changing CUDA versions changed the transcription");
        Console.WriteLine($"PASS: {version} returns the same transcript after changing the selected runtime");
        previousPid = process.Id;
        previousText = text;
    }
    service.Dispose();
    if (ProcessExists(previousPid)) throw new InvalidOperationException("The last CUDA worker was not released");
    Console.WriteLine("PASS: CUDA version switching leaves no workers behind");
    return;
}
if (benchmark)
{
    foreach (var device in benchmarkDevices)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await worker.ReleaseLoadedResourcesAsync();
        var clock = Stopwatch.StartNew();
        await worker.EnsureLoadedAsync(model, modelPath, device, deadline.Token, benchmarkCudaVersion);
        Console.WriteLine($"BENCH {device} {benchmarkCudaVersion}: loaded in {clock.Elapsed.TotalSeconds:F2}s; adapter={worker.LoadedDeviceName ?? "CPU"}");
        if (device == DictationDevice.DedicatedGpu)
        {
            var process = Field<Process>(worker, "_crispProcess")!;
            string runtimeFolder = Path.GetDirectoryName(process.StartInfo.FileName)!;
            var modules = process.Modules.Cast<ProcessModule>().Where(module =>
                module.ModuleName.StartsWith("cublas", StringComparison.OrdinalIgnoreCase)
                || module.ModuleName.StartsWith("cudart", StringComparison.OrdinalIgnoreCase)
                || module.ModuleName.Equals("ggml-cuda.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            int major = benchmarkCudaVersion == DictationCudaVersion.Cuda13 ? 13 : 12;
            if (!modules.Any(module => module.ModuleName == $"cublas64_{major}.dll")
                || modules.Any(module => !string.Equals(Path.GetDirectoryName(module.FileName), runtimeFolder, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The worker did not load its selected package's CUDA libraries");
            Console.WriteLine("NATIVE_MODULES " + string.Join("; ", modules.Select(module => module.FileName)));
        }
        foreach (float[] clip in new[] { samples[..Math.Min(samples.Length, 13 * 16000)], samples })
        {
            string? firstText = null;
            for (int repeat = 0; repeat < 2; repeat++)
            {
                clock.Restart();
                string text = await worker.TranscribeAsync(model, modelPath, clip, "en", device, deadline.Token, benchmarkCudaVersion);
                Console.WriteLine($"BENCH {device} {benchmarkCudaVersion}: audio={clip.Length / 16000.0:F2}s request={repeat + 1} elapsed={clock.Elapsed.TotalSeconds:F3}s text={text}");
                if (string.IsNullOrWhiteSpace(text) || (firstText != null && firstText != text))
                    throw new InvalidOperationException("Benchmark returned empty or inconsistent text");
                firstText = text;
            }
        }
    }
    await worker.ReleaseLoadedResourcesAsync();
    Console.WriteLine("PASS: Paragraph benchmark completed; all workers released");
    return;
}
int passes = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS: " + description);
    passes++;
}

const string whisperModel = "ggml-large-v3-turbo-q5_0.bin";
settings.DictationUseGpu = true;
Check(settings.GetDictationDevice(modelPath) == DictationDevice.IntegratedGpu
    && settings.GetDictationDevice(whisperModel) == DictationDevice.DedicatedGpu,
    "Existing acceleration settings migrate to the GPU type supported by each model");
settings.SetDictationDevice(modelPath, DictationDevice.Cpu);
settings.SetDictationDevice(whisperModel, DictationDevice.DedicatedGpu);
var serializer = new XmlSerializer(typeof(UserSettings));
using var saved = new StringWriter();
serializer.Serialize(saved, settings);
using var input = new StringReader(saved.ToString());
var restored = (UserSettings)serializer.Deserialize(input)!;
Check(restored.GetDictationDevice(model.FileName) == DictationDevice.Cpu
    && restored.GetDictationDevice(whisperModel) == DictationDevice.DedicatedGpu,
    "Device choices survive XML serialization independently and match model names or paths");
settings.SetDictationDevice(modelPath, DictationDevice.DedicatedGpu);
Check(settings.GetDictationDevice(modelPath) == DictationDevice.DedicatedGpu,
    "Parakeet Ultra supports a per-model dedicated CUDA GPU preference");
settings.SetDictationDevice(modelPath, DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13);
using var cudaSaved = new StringWriter();
serializer.Serialize(cudaSaved, settings);
using var cudaInput = new StringReader(cudaSaved.ToString());
var cudaRestored = (UserSettings)serializer.Deserialize(cudaInput)!;
Check(cudaRestored.GetDictationCudaVersion(model.FileName) == DictationCudaVersion.Cuda13
    && cudaRestored.GetDictationDevice(model.FileName) == DictationDevice.DedicatedGpu,
    "CUDA 13 selection survives XML serialization per model");
using var legacyInput = new StringReader(cudaSaved.ToString().Replace("<CudaVersion>Cuda13</CudaVersion>", ""));
var legacyRestored = (UserSettings)serializer.Deserialize(legacyInput)!;
Check(legacyRestored.GetDictationCudaVersion(modelPath) == DictationCudaVersion.Cuda12,
    "Existing settings without a CUDA version retain CUDA 12");
settings.SetDictationDevice(modelPath, DictationDevice.Cpu);
Check(settings.GetDictationCudaVersion(modelPath) == DictationCudaVersion.Cuda13,
    "Changing to CPU preserves the model's CUDA version preference");
bool cuda13Installed = (bool)typeof(ExternalAsrTranscriber).Assembly
    .GetType("FluentFlyoutWPF.Classes.Dictation.CrispAsrRuntime")!
    .GetMethod("IsInstalledFor")!.Invoke(null, [DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13])!;
if (!cuda13Installed)
{
    bool rejectedMissing = false;
    try { await worker.EnsureLoadedAsync(model, modelPath, DictationDevice.DedicatedGpu,
        CancellationToken.None, DictationCudaVersion.Cuda13); }
    catch (FileNotFoundException ex) { rejectedMissing = ex.Message.Contains("Cuda13"); }
    Check(rejectedMissing, "Missing CUDA 13 reports its package rather than falling back to CUDA 12 or CPU");
}
settings.SetDictationDevice(modelPath, DictationDevice.Cpu, DictationCudaVersion.Cuda12);
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
bool removedDuringDictation = false;
try { await service.RunRuntimeMaintenanceAsync(() => { removedDuringDictation = true; return Task.CompletedTask; }); }
catch (InvalidOperationException) { }
Check(!removedDuringDictation && worker.RuntimeLoaded, "Runtime removal is rejected while dictating");
typeof(DictationService).GetProperty(nameof(DictationService.Phase))!.SetValue(service, DictationPhase.Idle);
int firstPid = WorkerPid();
Check(worker.RuntimeLoaded && service.CrispModelLoaded && !worker.UsingGpu,
    "Parakeet Ultra loads on CPU when acceleration is disabled");
string first = await worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.Cpu, CancellationToken.None);
string second = await worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.Cpu, CancellationToken.None);
Check(first.Length > 0 && first == second && WorkerPid() == firstPid,
    "Repeated dictations reuse one loaded model and return consistent text");

service.RefreshResourceTimeout();
await Task.Delay(500);
settings.DictationKeepModelLoaded = true;
service.RefreshResourcePolicy();
await WaitForPolicy();
Check(WorkerPid() == firstPid, "Enabling Keep Model Loaded retains the current process");
await service.RunRuntimeMaintenanceAsync(async () =>
{
    Check(!worker.RuntimeLoaded && !ProcessExists(firstPid), "Runtime maintenance releases native DLLs before removing files");
    service.RefreshResourcePolicy();
    service.Start();
    await Task.Delay(150);
    Check(!service.Active && !worker.RuntimeLoaded, "Maintenance blocks recording and preloading until file operations finish");
});
await WaitForPolicy();
firstPid = WorkerPid();
Check(worker.RuntimeLoaded, "Keep Model Loaded resumes after runtime maintenance");
Console.WriteLine("Waiting beyond the 15-second inactivity timeout with Keep Model Loaded enabled...");
await Task.Delay(TimeSpan.FromSeconds(16));
Check(WorkerPid() == firstPid && worker.RuntimeLoaded,
    "Keep Model Loaded cancels the timer and retains the model beyond inactivity");

settings.DictationKeepModelLoaded = false;
service.RefreshResourcePolicy();
await WaitForPolicy();
Check(WorkerPid() == firstPid, "Disabling Keep Model Loaded starts a timer instead of releasing immediately");
await Task.Delay(TimeSpan.FromSeconds(8));
await worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.Cpu, CancellationToken.None);
service.RefreshResourceTimeout();
await Task.Delay(TimeSpan.FromSeconds(8));
Check(WorkerPid() == firstPid && worker.RuntimeLoaded,
    "Another dictation resets inactivity and survives the original release deadline");
await WaitUntil(() => !worker.RuntimeLoaded, TimeSpan.FromSeconds(10));
Check(!service.CrispModelLoaded && !ProcessExists(firstPid),
    "Inactivity releases the worker process and its model weights");

await worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.Cpu, CancellationToken.None);
int reloadedPid = WorkerPid();
Check(reloadedPid != firstPid, "A dictation after release creates a fresh worker");
using (var cancelled = new CancellationTokenSource())
{
    Task<string> pending = worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.Cpu, cancelled.Token);
    cancelled.CancelAfter(20);
    try { await pending; throw new InvalidOperationException("Expected cancellation"); }
    catch (OperationCanceledException) { }
}
Check(!worker.RuntimeLoaded && !ProcessExists(reloadedPid),
    "Cancellation terminates native inference and discards the worker");
await worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.Cpu, CancellationToken.None);
Check(worker.RuntimeLoaded, "The worker recovers after cancellation");
int disabledPid = WorkerPid();
settings.DictationEnabled = false;
service.RefreshSettings();
await WaitForPolicy();
Check(!worker.RuntimeLoaded && !ProcessExists(disabledPid), "Disabling dictation releases the model");

settings.DictationEnabled = true;
settings.DictationKeepModelLoaded = true;
settings.SetDictationDevice(modelPath, DictationDevice.IntegratedGpu);
service.RefreshAccelerationSettings();
await WaitForPolicy();
Check(worker.RuntimeLoaded && service.CrispUsingGpu && worker.MatchesLoadedModel(modelPath, DictationDevice.IntegratedGpu),
    "Enabling acceleration preloads a persistent Vulkan worker without restarting the app");
int vulkanPid = WorkerPid();
var vulkanProcess = Field<Process>(worker, "_crispProcess")!;
int deviceArgument = vulkanProcess.StartInfo.ArgumentList.IndexOf("--device");
Check(deviceArgument >= 0 && !string.IsNullOrWhiteSpace(service.LoadedDeviceName),
    "The Vulkan worker receives an explicit integrated GPU ID and exposes its actual adapter name");
string vulkanFirst = await worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.IntegratedGpu, CancellationToken.None);
string vulkanSecond = await worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.IntegratedGpu, CancellationToken.None);
Check(vulkanFirst.Length > 0 && vulkanSecond.Length > 0 && WorkerPid() == vulkanPid,
    "Vulkan dictations reuse the same loaded process");
settings.SetDictationDevice(modelPath, DictationDevice.DedicatedGpu);
Check(!worker.MatchesLoadedModel(modelPath, DictationDevice.DedicatedGpu),
    "An integrated worker cannot satisfy a dedicated GPU request");
service.RefreshAccelerationSettings();
await WaitForPolicy();
int dedicatedPid = WorkerPid();
var dedicatedProcess = Field<Process>(worker, "_crispProcess")!;
deviceArgument = dedicatedProcess.StartInfo.ArgumentList.IndexOf("--device");
Check(worker.RuntimeLoaded && service.LoadedDevice == DictationDevice.DedicatedGpu
    && dedicatedPid != vulkanPid && !ProcessExists(vulkanPid)
    && deviceArgument >= 0
    && dedicatedProcess.StartInfo.ArgumentList.Contains("cuda")
    && dedicatedProcess.StartInfo.FileName != vulkanProcess.StartInfo.FileName
    && service.LoadedDeviceName?.Contains("RTX 3050 Ti") == true,
    "Switching from integrated Vulkan to dedicated CUDA replaces the process and uses the NVIDIA package");
Check(worker.MatchesLoadedModel(modelPath, DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda12)
    && !worker.MatchesLoadedModel(modelPath, DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13),
    "A loaded CUDA 12 worker cannot satisfy a CUDA 13 request");
var watch = Stopwatch.StartNew();
string dedicatedFirst = await worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.DedicatedGpu, CancellationToken.None);
Console.WriteLine($"Dedicated CUDA first inference: {watch.Elapsed.TotalSeconds:F2}s for {samples.Length / 16000.0:F2}s audio: {dedicatedFirst}");
watch.Restart();
string dedicatedSecond = await worker.TranscribeAsync(model, modelPath, samples, "es", DictationDevice.DedicatedGpu, CancellationToken.None);
Console.WriteLine($"Dedicated CUDA warm inference: {watch.Elapsed.TotalSeconds:F2}s: {dedicatedSecond}");
Check(dedicatedFirst.Length > 0 && dedicatedFirst == dedicatedSecond && WorkerPid() == dedicatedPid,
    "Dedicated CUDA returns consistent text and reuses the loaded RTX worker");
settings.SetDictationDevice(modelPath, DictationDevice.Cpu);
service.RefreshAccelerationSettings();
await WaitForPolicy();
Check(worker.RuntimeLoaded && !service.CrispUsingGpu && WorkerPid() != dedicatedPid && !ProcessExists(dedicatedPid),
    "Disabling acceleration replaces dedicated CUDA with a persistent CPU worker");
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
