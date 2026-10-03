using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.ViewModels;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

// Runs real Whisper inference with the app's runtime selection and processor settings.
// No microphone, text injection, saved preferences or GPU crash marker is touched.
internal static class WhisperInferenceChecks
{
    public static async Task Run(string modelPath, string audioPath, bool gpu)
    {
        modelPath = Path.GetFullPath(modelPath);
        await DictationModelStore.ValidateIntegrityAsync(modelPath, CancellationToken.None);
        using var audio = new WaveFileReader(audioPath);
        if (audio.WaveFormat.SampleRate != 16_000 || audio.WaveFormat.Channels != 1
            || audio.WaveFormat.BitsPerSample != 16 || audio.WaveFormat.Encoding != WaveFormatEncoding.Pcm)
            throw new ArgumentException("Expected 16 kHz mono PCM16 audio.");
        byte[] bytes = new byte[checked((int)audio.Length)];
        audio.ReadExactly(bytes);
        float[] samples = Enumerable.Range(0, bytes.Length / 2)
            .Select(index => BitConverter.ToInt16(bytes, index * 2) / 32768f).ToArray();
        SettingsManager.Current = new UserSettings
        {
            DictationModel = modelPath,
            DictationUseGpu = gpu,
            DictationKeepModelLoaded = false,
        };
        SettingsManager.Current.SetDictationDevice(modelPath, gpu ? DictationDevice.DedicatedGpu : DictationDevice.Cpu);
        typeof(DictationService).GetMethod("ApplyRuntimeLibraryOrder", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [gpu]);
        LogProvider.AddConsoleLogging(WhisperLogLevel.Info);
        var clock = Stopwatch.StartNew();
        using var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = gpu, GpuDevice = 0 });
        double loadSeconds = clock.Elapsed.TotalSeconds;
        if (gpu && RuntimeOptions.LoadedLibrary != RuntimeLibrary.Cuda)
            throw new InvalidOperationException("CUDA silently fell back to CPU.");
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().ToArray();
        if (modules.Any(module => module.FileName.Contains("NVIDIA GPU Computing Toolkit", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The test loaded a library from the CUDA Toolkit.");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluentFlyout", "runtimes", "whisper-cuda", "13.4.1");
        string[] dependencies = ["cublas64_13.dll", "cublasLt64_13.dll", "cudart64_13.dll"];
        var nativeModules = modules.Where(module => dependencies.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (gpu && (nativeModules.Length != dependencies.Length || nativeModules.Any(module =>
            !string.Equals(Path.GetDirectoryName(module.FileName), root, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Whisper did not use its downloaded NVIDIA dependencies.");
        Console.WriteLine("MODULES " + JsonSerializer.Serialize(nativeModules.Select(module => module.FileName)));
        Console.WriteLine("LOAD " + JsonSerializer.Serialize(new { gpu, loadSeconds, runtime = RuntimeOptions.LoadedLibrary.ToString(), info = WhisperFactory.GetRuntimeInfo() }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var clip in new[] { samples[..Math.Min(samples.Length, 13 * 16000)], samples })
        {
            string? previous = null;
            for (int repeat = 1; repeat <= 2; repeat++)
            {
                using var processor = (WhisperProcessor)typeof(DictationService)
                    .GetMethod("BuildProcessor", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [factory, "en"])!;
                var text = new StringBuilder();
                clock.Restart();
                await foreach (var segment in processor.ProcessAsync(clip, deadline.Token)) text.Append(segment.Text);
                double seconds = clock.Elapsed.TotalSeconds;
                string transcript = text.ToString().Trim();
                if (string.IsNullOrWhiteSpace(transcript) || (previous != null && transcript != previous))
                    throw new InvalidOperationException("Whisper returned an empty or inconsistent transcript.");
                Console.WriteLine("INFERENCE " + JsonSerializer.Serialize(new { gpu, audioSeconds = clip.Length / 16000d, repeat, seconds, transcript }));
                previous = transcript;
            }
        }
        Console.WriteLine("PASS: Real Whisper transcription completed without Toolkit modules.");
    }
}
