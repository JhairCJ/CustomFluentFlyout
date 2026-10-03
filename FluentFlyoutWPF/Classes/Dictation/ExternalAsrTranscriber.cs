// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>
/// Bridge for non-Whisper models using the local NeMo-Speech.cpp or CrispASR
/// HTTP workers, loaded and released through the same interface.
/// </summary>
public sealed class ExternalAsrTranscriber : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient NemoHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private static readonly HttpClient CrispHttp = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private static readonly TimeSpan NemoReadyTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NemoPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _externalLock = new(1, 1);
    private Process? _nemoProcess;
    private Uri? _nemoBaseUri;
    private string? _nemoModelPath;
    private bool _nemoUseGpu;
    private bool _nemoServerUnavailable;
    private string? _nemoUnavailableModelPath;
    private bool _nemoUnavailableUseGpu;
    private Process? _crispProcess;
    private Uri? _crispBaseUri;
    private string? _crispModelPath;
    private string? _crispApiKey;
    private DictationDevice _crispDevice;
    private string? _crispDeviceName;
    private bool _disposed;

    public bool RuntimeLoaded => _nemoProcess is { HasExited: false }
        || _crispProcess is { HasExited: false };

    public bool CrispRuntimeLoaded => _crispProcess is { HasExited: false };

    public string? LoadedDeviceName => CrispRuntimeLoaded ? _crispDeviceName : null;

    public string? LoadedModelPath => _crispProcess is { HasExited: false } ? _crispModelPath
        : _nemoProcess is { HasExited: false } ? _nemoModelPath
        : null;

    public bool UsingGpu => (_nemoProcess is { HasExited: false } && _nemoUseGpu)
        || (_crispProcess is { HasExited: false } && _crispDevice != DictationDevice.Cpu);

    public DictationDevice LoadedDevice => CrispRuntimeLoaded ? _crispDevice
        : UsingGpu ? DictationDevice.DedicatedGpu : DictationDevice.Cpu;

    public bool MatchesLoadedModel(string modelPath, DictationDevice device) =>
        string.Equals(LoadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
        && LoadedDevice == device;

    /// <summary>Loads the model's external worker, if its backend supports preloading.</summary>
    public async Task EnsureLoadedAsync(
        DictationModelInfo model,
        string modelPath,
        DictationDevice device,
        CancellationToken cancellationToken)
    {
        if (model.Backend is not (DictationModelBackend.NemoSpeech or DictationModelBackend.CrispAsr))
            return;

        await _externalLock.WaitAsync(cancellationToken);
        try
        {
            if (model.Backend == DictationModelBackend.CrispAsr)
            {
                await EnsureCrispServerCoreAsync(modelPath, device, cancellationToken);
            }
            else
            {
                await EnsureNemoServerCoreAsync(modelPath, device != DictationDevice.Cpu, cancellationToken);
            }
        }
        finally
        {
            _externalLock.Release();
        }
    }

    public async Task<string> TranscribeAsync(
        DictationModelInfo model,
        string modelPath,
        float[] samples,
        string language,
        DictationDevice device,
        CancellationToken cancellationToken)
    {
        string wavPath = Path.Combine(
            Path.GetTempPath(),
            $"FluentFlyout-dictation-{Guid.NewGuid():N}.wav");

        try
        {
            WriteWaveFile(wavPath, samples);
            return model.Backend switch
            {
                DictationModelBackend.NemoSpeech => await TranscribeWithNemoAsync(
                    modelPath, wavPath, language, model.UsesLocaleLanguageCodes, device != DictationDevice.Cpu, cancellationToken),
                DictationModelBackend.CrispAsr => await TranscribeWithCrispAsrAsync(
                    modelPath, wavPath, language, device, cancellationToken),
                _ => throw new InvalidOperationException(
                    $"El backend externo no admite el modelo {model.FileName}"),
            };
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    /// <summary>
    /// Releases every external worker so their weights do not sit in RAM or VRAM while
    /// dictation is idle.
    /// </summary>
    public async Task ReleaseLoadedResourcesAsync()
    {
        if (_disposed)
        {
            StopNemoServer();
            StopCrispServer();
            return;
        }

        await _externalLock.WaitAsync();
        try
        {
            StopNemoServer();
            StopCrispServer();
        }
        finally
        {
            _externalLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopNemoServer();
        StopCrispServer();
    }

    private async Task<string> TranscribeWithNemoAsync(
        string modelPath,
        string wavPath,
        string language,
        bool usesLocaleLanguageCodes,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        string nemoLanguage = usesLocaleLanguageCodes
            ? language.Trim().ToLowerInvariant() switch
            {
                "es" => "es-ES",
                "en" => "en-US",
                _ => language,
            }
            : language;

        await _externalLock.WaitAsync(cancellationToken);
        try
        {
            if (!_nemoServerUnavailable)
            {
                try
                {
                    await EnsureNemoServerCoreAsync(modelPath, useGpu, cancellationToken);
                    return await TranscribeWithNemoServerCoreAsync(
                        wavPath, nemoLanguage, cancellationToken);
                }
                catch (NemoServerUnavailableException ex)
                {
                    _nemoServerUnavailable = true;
                    _nemoUnavailableModelPath = modelPath;
                    _nemoUnavailableUseGpu = useGpu;
                    Logger.Warn(ex,
                        "NeMo-Speech.cpp no ofrece servidor HTTP; se usará el CLI de una sola transcripción");
                }
            }

            try
            {
                return await TranscribeWithNemoOneShotAsync(
                    modelPath, wavPath, nemoLanguage, useGpu, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A CLI failure does not prove the server is incompatible;
                // try it again next session in case the failure was transient.
                _nemoServerUnavailable = false;
                _nemoUnavailableModelPath = null;
                Logger.Warn(
                    "Falló la transcripción CLI de NeMo-Speech.cpp; " +
                    "se volverá a probar el servidor en el siguiente dictado");
                throw;
            }
        }
        finally
        {
            _externalLock.Release();
        }
    }

    private static async Task<string> TranscribeWithNemoOneShotAsync(
        string modelPath,
        string wavPath,
        string language,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "nemo-speech",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.ArgumentList.Add("transcribe");
        startInfo.ArgumentList.Add(wavPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(useGpu ? "cuda:0" : "cpu");
        if (!string.IsNullOrWhiteSpace(language))
        {
            startInfo.ArgumentList.Add("--language");
            startInfo.ArgumentList.Add(language);
        }

        using Process process = StartProcess(startInfo, "NeMo-Speech.cpp");
        return await ReadProcessOutputAsync(process, "NeMo-Speech.cpp", cancellationToken);
    }

    /// <summary>
    /// Reuses the worker until DictationService releases it under the user's policy.
    /// Integrated graphics use Vulkan; dedicated NVIDIA graphics use CUDA 12.
    /// </summary>
    private async Task<string> TranscribeWithCrispAsrAsync(
        string modelPath,
        string wavPath,
        string language,
        DictationDevice device,
        CancellationToken cancellationToken)
    {
        await _externalLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureCrispServerCoreAsync(modelPath, device, cancellationToken);
            using var form = new MultipartFormDataContent();
            await using FileStream audio = File.OpenRead(wavPath);
            using var file = new StreamContent(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", Path.GetFileName(wavPath));
            form.Add(new StringContent("json"), "response_format");
            if (!string.IsNullOrWhiteSpace(language) && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
                form.Add(new StringContent(language), "language");

            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri(_crispBaseUri!, "v1/audio/transcriptions"))
            {
                Content = form,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _crispApiKey);
            using HttpResponseMessage response = await CrispHttp.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            string text = JsonSerializer.Deserialize<CrispAsrResponse>(body, JsonOptions)?.Text?.Trim() ?? "";
            if (text.Length == 0)
                throw new InvalidOperationException("CrispASR no devolvió texto para este audio");
            Logger.Info($"CrispASR: {Path.GetFileName(modelPath)} ({device}, modelo persistente)");
            return text;
        }
        catch
        {
            // Cancellation must stop native inference, not just the HTTP wait. A broken
            // worker is recreated on the next dictation instead of retaining stale work.
            StopCrispServer();
            throw;
        }
        finally
        {
            _externalLock.Release();
        }
    }

    private async Task EnsureCrispServerCoreAsync(string modelPath, DictationDevice device, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExternalAsrTranscriber));
        if (_crispProcess is { HasExited: false }
            && string.Equals(_crispModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
            && _crispDevice == device)
            return;

        StopCrispServer();
        string backend = device == DictationDevice.DedicatedGpu ? "cuda" : "vulkan";
        string executable = CrispAsrRuntime.ExecutableFor(device) ?? throw new FileNotFoundException(
            $"No se encontró el runtime CrispASR {backend}. Descarga sus dependencias en Ajustes > Dictado > Runtimes.");
        var adapter = device != DictationDevice.Cpu
            ? await CrispAsrRuntime.GetDeviceAsync(device, cancellationToken) : null;
        if (device != DictationDevice.Cpu && adapter == null)
            throw new InvalidOperationException($"No se encontró una gráfica {(device == DictationDevice.DedicatedGpu ? "NVIDIA dedicada" : "integrada")} compatible con {backend}. Comprueba las dependencias CrispASR y el controlador de la GPU o selecciona Procesador en los ajustes del modelo.");
        int port = FindAvailableLoopbackPort();
        Uri baseUri = new($"http://127.0.0.1:{port}/");
        string apiKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["CRISPASR_API_KEYS"] = apiKey;
        startInfo.Environment.Remove("GGML_VK_VISIBLE_DEVICES");
        startInfo.Environment.Remove("CUDA_VISIBLE_DEVICES");
        startInfo.ArgumentList.Add("--server");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--no-warmup");
        if (adapter != null)
        {
            startInfo.ArgumentList.Add("--gpu-backend");
            startInfo.ArgumentList.Add(backend);
            startInfo.ArgumentList.Add("--device");
            startInfo.ArgumentList.Add(adapter.Index.ToString());
        }
        else
        {
            startInfo.ArgumentList.Add("-ng");
        }
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(modelPath);

        Process process = StartProcess(startInfo, "CrispASR");
        _crispProcess = process;
        _crispBaseUri = baseUri;
        _crispModelPath = modelPath;
        _crispApiKey = apiKey;
        _crispDevice = device;
        _crispDeviceName = adapter?.Name;
        Logger.Info($"CrispASR: dispositivo {(adapter == null ? "CPU" : $"{backend} {adapter.Index}: {adapter.Name}")}");
        _ = DrainProcessOutputAsync(process.StandardOutput, "CrispASR stdout");
        _ = DrainProcessOutputAsync(process.StandardError, "CrispASR stderr");
        try
        {
            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyCts.CancelAfter(NemoReadyTimeout);
            while (true)
            {
                readyCts.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException($"CrispASR terminó antes de cargar el modelo (código {process.ExitCode})");
                try
                {
                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(readyCts.Token);
                    pollCts.CancelAfter(TimeSpan.FromSeconds(1));
                    using HttpResponseMessage response = await CrispHttp.GetAsync(new Uri(baseUri, "health"), pollCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var health = JsonSerializer.Deserialize<CrispAsrHealth>(
                            await response.Content.ReadAsStringAsync(pollCts.Token), JsonOptions);
                        if (health?.Status == "ok") break;
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!readyCts.IsCancellationRequested) { }
                await Task.Delay(NemoPollInterval, readyCts.Token);
            }
            Logger.Info($"CrispASR listo ({device}, persistente): {Path.GetFileName(modelPath)}");
        }
        catch
        {
            StopCrispServer();
            throw;
        }
    }

    private async Task EnsureNemoServerCoreAsync(
        string modelPath,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExternalAsrTranscriber));
        if (_nemoServerUnavailable
            && (!string.Equals(_nemoUnavailableModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
                || _nemoUnavailableUseGpu != useGpu))
        {
            _nemoServerUnavailable = false;
            _nemoUnavailableModelPath = null;
        }
        if (_nemoServerUnavailable) return;

        bool matches = _nemoProcess is { HasExited: false }
            && string.Equals(_nemoModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
            && _nemoUseGpu == useGpu;
        if (matches) return;

        if (_nemoProcess != null)
        {
            StopNemoServer();
            _nemoServerUnavailable = false;
        }

        int port = FindAvailableLoopbackPort();
        Uri baseUri = new($"http://127.0.0.1:{port}/");
        var startInfo = new ProcessStartInfo
        {
            FileName = "nemo-speech",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--no-ui");
        startInfo.ArgumentList.Add("--no-warmup");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--asr-model");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("--gpu");
        startInfo.ArgumentList.Add(useGpu ? "0" : "-1");
        // The app serializes dictations; serve batching gains nothing and can lead to an
        // unstable GGML allocation path in this runtime.
        startInfo.ArgumentList.Add("--asr.batching.enabled=false");

        Process process;
        try
        {
            process = StartProcess(startInfo, "NeMo-Speech.cpp");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new NemoServerUnavailableException(
                "No se pudo iniciar el servidor HTTP de NeMo-Speech.cpp", ex);
        }

        _nemoProcess = process;
        _nemoBaseUri = baseUri;
        _nemoModelPath = modelPath;
        _nemoUseGpu = useGpu;
        _ = DrainProcessOutputAsync(process.StandardOutput, "NeMo-Speech stdout");
        _ = DrainProcessOutputAsync(process.StandardError, "NeMo-Speech stderr");

        try
        {
            await WaitForNemoReadyAsync(process, baseUri, cancellationToken);
            Logger.Info($"NeMo-Speech.cpp listo en {baseUri} ({Path.GetFileName(modelPath)})");
        }
        catch (NemoServerUnavailableException)
        {
            StopNemoServer();
            throw;
        }
        catch (OperationCanceledException)
        {
            StopNemoServer();
            throw;
        }
        catch (Exception ex)
        {
            StopNemoServer();
            throw new NemoServerUnavailableException(
                "El servidor HTTP de NeMo-Speech.cpp no llegó a estar listo", ex);
        }
    }

    private static async Task WaitForNemoReadyAsync(
        Process process,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + NemoReadyTimeout;
        Uri readyUri = new(baseUri, "ready");
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new NemoServerUnavailableException(
                    $"NeMo-Speech.cpp terminó antes de estar listo (código {process.ExitCode})");

            try
            {
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCts.CancelAfter(TimeSpan.FromSeconds(1));
                using HttpResponseMessage response = await NemoHttp.GetAsync(readyUri, requestCts.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The server has not opened the listener yet.
            }
            catch (HttpRequestException)
            {
                // The server is still loading the model.
            }

            await Task.Delay(NemoPollInterval, cancellationToken);
        }

        throw new NemoServerUnavailableException(
            $"NeMo-Speech.cpp no estuvo listo en {NemoReadyTimeout.TotalSeconds:0} segundos");
    }

    private async Task<string> TranscribeWithNemoServerCoreAsync(
        string wavPath,
        string language,
        CancellationToken cancellationToken)
    {
        if (_nemoBaseUri == null)
            throw new InvalidOperationException("El servidor de NeMo-Speech.cpp no está iniciado");

        using var form = new MultipartFormDataContent();
        await using FileStream audio = File.OpenRead(wavPath);
        using var file = new StreamContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", Path.GetFileName(wavPath));
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(language) && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            form.Add(new StringContent(language), "language");

        Uri endpoint = new(_nemoBaseUri, "v1/audio/transcriptions");
        using HttpResponseMessage response = await NemoHttp.PostAsync(endpoint, form, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (body.Contains("out of device memory", StringComparison.OrdinalIgnoreCase)
                || body.Contains("cudaMalloc failed", StringComparison.OrdinalIgnoreCase))
            {
                await LogCudaMemorySnapshotAsync();
            }

            throw new InvalidOperationException(
                $"NeMo-Speech.cpp devolvió {(int)response.StatusCode}: {body.Trim()}");
        }

        NemoResponse? result = JsonSerializer.Deserialize<NemoResponse>(body, JsonOptions);
        return result?.Text?.Trim() ?? "";
    }

    private static async Task LogCudaMemorySnapshotAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--query-gpu=name,memory.total,memory.used,memory.free");
        startInfo.ArgumentList.Add("--format=csv,noheader");

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process == null) return;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output = await outputTask;
            string error = await errorTask;
            Logger.Warn(process.ExitCode == 0
                ? $"VRAM tras el fallo de asignación CUDA: {output.Trim()}"
                : $"nvidia-smi terminó con código {process.ExitCode}: {error.Trim()}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
            or OperationCanceledException or IOException)
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }

            Logger.Warn(ex, "No se pudo obtener el estado de VRAM después del fallo de NeMo-Speech.cpp");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static int FindAvailableLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task DrainProcessOutputAsync(StreamReader reader, string source)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                bool diagnostic = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("assert", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("abort", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("out of memory", StringComparison.OrdinalIgnoreCase);
                if (source.EndsWith("stderr", StringComparison.OrdinalIgnoreCase) && diagnostic)
                    Logger.Warn($"{source}: {line}");
                else
                    Logger.Debug($"{source}: {line}");
            }
        }
        catch (ObjectDisposedException)
        {
            // The process was closed when the worker was released.
        }
        catch (IOException)
        {
            // The process may have closed the pipe during cancellation.
        }
    }

    private static Process StartProcess(ProcessStartInfo startInfo, string runtimeName)
    {
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (process.Start()) return process;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"No se encontró el runtime {runtimeName}. Instálalo y vuelve a intentarlo.", ex);
        }

        process.Dispose();
        throw new InvalidOperationException($"No se pudo iniciar el runtime {runtimeName}");
    }

    private static async Task<string> ReadProcessOutputAsync(
        Process process,
        string runtimeName,
        CancellationToken cancellationToken)
    {
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            StopProcess(process);
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(stderr)
                ? $"código de salida {process.ExitCode}"
                : stderr.Trim();
            throw new InvalidOperationException($"{runtimeName} no pudo transcribir el audio: {detail}");
        }

        return stdout.Trim();
    }

    /// <summary>
    /// Writes the captured audio as a 16 kHz mono PCM16 WAV file.
    ///
    /// <para>The payload is encoded straight into one byte buffer and handed to the file
    /// in a single write. Going sample by sample through a BinaryWriter meant one call
    /// per sample - a few hundred thousand of them for a normal phrase - and sat right
    /// between the hotkey coming back and the recognizer getting the file.</para>
    /// </summary>
    private static void WriteWaveFile(string path, float[] samples)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        const int sampleRate = 16_000;
        const int headerSize = 44;
        int bytesPerSample = bitsPerSample / 8;
        int dataSize = checked(samples.Length * bytesPerSample);

        byte[] buffer = new byte[headerSize + dataSize];
        Span<byte> span = buffer;

        // RIFF / WAVE / fmt  / data, little endian, with a 16-byte PCM fmt chunk.
        "RIFF"u8.CopyTo(span);
        BitConverter.TryWriteBytes(span[4..], 36 + dataSize);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BitConverter.TryWriteBytes(span[16..], 16);
        BitConverter.TryWriteBytes(span[20..], (short)1);
        BitConverter.TryWriteBytes(span[22..], channels);
        BitConverter.TryWriteBytes(span[24..], sampleRate);
        BitConverter.TryWriteBytes(span[28..], sampleRate * channels * bytesPerSample);
        BitConverter.TryWriteBytes(span[32..], (short)(channels * bytesPerSample));
        BitConverter.TryWriteBytes(span[34..], bitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BitConverter.TryWriteBytes(span[40..], dataSize);

        Span<byte> payload = span[headerSize..];
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];
            float safeSample = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
            BitConverter.TryWriteBytes(payload[(i * 2)..], (short)MathF.Round(safeSample * short.MaxValue));
        }

        using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            headerSize + dataSize,
            FileOptions.SequentialScan);
        stream.Write(buffer);
    }

    private void StopNemoServer()
    {
        Process? process = _nemoProcess;
        _nemoProcess = null;
        _nemoBaseUri = null;
        _nemoModelPath = null;
        _nemoUseGpu = false;
        if (process != null) StopProcess(process);
    }

    private void StopCrispServer()
    {
        Process? process = _crispProcess;
        _crispProcess = null;
        _crispBaseUri = null;
        _crispModelPath = null;
        _crispApiKey = null;
        _crispDevice = DictationDevice.Cpu;
        _crispDeviceName = null;
        if (process != null) StopProcess(process);
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);
        }
        catch
        {
            // El proceso ya pudo terminar justo al cancelar.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // The temporary audio must not hide the transcription result.
        }
    }

    private sealed record NemoResponse(string? Text = null);
    private sealed record CrispAsrResponse(string? Text = null);
    private sealed record CrispAsrHealth(string? Status = null);

    private sealed class NemoServerUnavailableException(string message, Exception? inner = null)
        : Exception(message, inner);
}
