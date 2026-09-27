// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>
/// Puente para modelos que no son Whisper GGML. Qwen usa el paquete oficial qwen-asr y
/// Parakeet usa el servidor HTTP local de NeMo-Speech.cpp cuando está disponible; ambos
/// workers se cargan y liberan mediante la misma interfaz.
/// </summary>
public sealed class ExternalAsrTranscriber : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient NemoHttp = new()
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
    private Process? _qwenProcess;
    private string? _qwenModelPath;
    private bool _qwenUseGpu;
    private Process? _nemoProcess;
    private Uri? _nemoBaseUri;
    private string? _nemoModelPath;
    private bool _nemoUseGpu;
    private bool _nemoServerUnavailable;
    private string? _nemoUnavailableModelPath;
    private bool _nemoUnavailableUseGpu;
    private bool _disposed;

    public bool RuntimeLoaded => _qwenProcess is { HasExited: false }
        || _nemoProcess is { HasExited: false };

    public bool UsingGpu => (_qwenProcess is { HasExited: false } && _qwenUseGpu)
        || (_nemoProcess is { HasExited: false } && _nemoUseGpu);

    /// <summary>Carga el worker externo del modelo, si su backend admite precarga.</summary>
    public async Task EnsureLoadedAsync(
        DictationModelInfo model,
        string modelPath,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        if (model.Backend is not (DictationModelBackend.NemoSpeech or DictationModelBackend.QwenAsr))
            return;

        await _externalLock.WaitAsync(cancellationToken);
        try
        {
            if (model.Backend == DictationModelBackend.QwenAsr)
            {
                await EnsureQwenProcessCoreAsync(modelPath, useGpu, cancellationToken);
            }
            else
            {
                await EnsureNemoServerCoreAsync(modelPath, useGpu, cancellationToken);
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
        bool useGpu,
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
                    modelPath, wavPath, language, useGpu, cancellationToken),
                DictationModelBackend.QwenAsr => await TranscribeWithQwenAsync(
                    modelPath, wavPath, language, useGpu, cancellationToken),
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
    /// Libera todos los workers externos para no mantener sus pesos en RAM o VRAM mientras
    /// el dictado está inactivo.
    /// </summary>
    public async Task ReleaseLoadedResourcesAsync()
    {
        if (_disposed)
        {
            StopQwenProcess();
            StopNemoServer();
            return;
        }

        await _externalLock.WaitAsync();
        try
        {
            StopQwenProcess();
            StopNemoServer();
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
        StopQwenProcess();
        StopNemoServer();
    }

    private async Task<string> TranscribeWithNemoAsync(
        string modelPath,
        string wavPath,
        string language,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        await _externalLock.WaitAsync(cancellationToken);
        try
        {
            if (!_nemoServerUnavailable)
            {
                try
                {
                    await EnsureNemoServerCoreAsync(modelPath, useGpu, cancellationToken);
                    return await TranscribeWithNemoServerCoreAsync(
                        wavPath, language, cancellationToken);
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

            return await TranscribeWithNemoOneShotAsync(
                modelPath, wavPath, useGpu, cancellationToken);
        }
        finally
        {
            _externalLock.Release();
        }
    }

    private static async Task<string> TranscribeWithNemoOneShotAsync(
        string modelPath,
        string wavPath,
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

        using Process process = StartProcess(startInfo, "NeMo-Speech.cpp");
        return await ReadProcessOutputAsync(process, cancellationToken);
    }

    private async Task<string> TranscribeWithQwenAsync(
        string modelPath,
        string wavPath,
        string language,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        await _externalLock.WaitAsync(cancellationToken);
        try
        {
            Process process = await EnsureQwenProcessCoreAsync(modelPath, useGpu, cancellationToken);
            var request = new QwenRequest(wavPath, QwenLanguage(language));
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
                await process.StandardInput.FlushAsync(cancellationToken);

                string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                    throw new InvalidOperationException("Qwen3-ASR no devolvió ningún resultado");

                QwenResponse? response = JsonSerializer.Deserialize<QwenResponse>(line, JsonOptions);
                if (response?.Ok != true)
                    throw new InvalidOperationException(response?.Error ?? "Qwen3-ASR no pudo transcribir el audio");
                return response.Text?.Trim() ?? "";
            }
            catch
            {
                // Si se cancela o se rompe el protocolo, se descarta el worker para que la
                // siguiente sesión no lea una respuesta vieja.
                StopQwenProcess();
                throw;
            }
        }
        finally
        {
            _externalLock.Release();
        }
    }

    private async Task<Process> EnsureQwenProcessCoreAsync(
        string modelPath,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExternalAsrTranscriber));
        if (_qwenProcess is { HasExited: false }
            && string.Equals(_qwenModelPath, modelPath, StringComparison.OrdinalIgnoreCase)
            && _qwenUseGpu == useGpu)
            return _qwenProcess;

        StopQwenProcess();
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "Dictation",
            "qwen_asr_runner.py");
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("No se encontró el runner de Qwen3-ASR", scriptPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(useGpu ? "cuda:0" : "cpu");

        Process process = StartProcess(startInfo, "Qwen3-ASR");
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) Logger.Debug($"Qwen3-ASR: {args.Data}");
        };
        process.BeginErrorReadLine();

        string? readyLine;
        try
        {
            readyLine = await process.StandardOutput.ReadLineAsync(cancellationToken);
        }
        catch
        {
            StopProcess(process);
            throw;
        }

        QwenResponse? ready;
        try
        {
            ready = string.IsNullOrWhiteSpace(readyLine)
                ? null
                : JsonSerializer.Deserialize<QwenResponse>(readyLine, JsonOptions);
        }
        catch
        {
            StopProcess(process);
            throw;
        }
        if (ready?.Ready != true)
        {
            string error = ready?.Error ?? "el runner no terminó de cargar el modelo";
            StopProcess(process);
            throw new InvalidOperationException(
                $"No se pudo iniciar Qwen3-ASR: {error}. Instala Python 3.12 y qwen-asr.");
        }

        _qwenProcess = process;
        _qwenModelPath = modelPath;
        _qwenUseGpu = useGpu;
        return process;
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
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--asr-model");
        startInfo.ArgumentList.Add(modelPath);
        startInfo.ArgumentList.Add("--gpu");
        startInfo.ArgumentList.Add(useGpu ? "0" : "-1");

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
                // El servidor aún no ha abierto el listener.
            }
            catch (HttpRequestException)
            {
                // El servidor aún está cargando el modelo.
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
            throw new InvalidOperationException(
                $"NeMo-Speech.cpp devolvió {(int)response.StatusCode}: {body.Trim()}");

        NemoResponse? result = JsonSerializer.Deserialize<NemoResponse>(body, JsonOptions);
        return result?.Text?.Trim() ?? "";
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
                if (!string.IsNullOrWhiteSpace(line)) Logger.Debug($"{source}: {line}");
            }
        }
        catch (ObjectDisposedException)
        {
            // El proceso se cerró al liberar el worker.
        }
        catch (IOException)
        {
            // El proceso pudo cerrar el pipe durante la cancelación.
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
            throw new InvalidOperationException($"NeMo-Speech.cpp no pudo transcribir el audio: {detail}");
        }

        return stdout.Trim();
    }

    private static string? QwenLanguage(string language) => language.Trim().ToLowerInvariant() switch
    {
        "es" => "Spanish",
        "en" => "English",
        _ => null,
    };

    private static void WriteWaveFile(string path, float[] samples)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        const int sampleRate = 16_000;
        int bytesPerSample = bitsPerSample / 8;
        int dataSize = checked(samples.Length * bytesPerSample);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample);
        writer.Write((short)(channels * bytesPerSample));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        foreach (float sample in samples)
        {
            float safeSample = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
            writer.Write((short)Math.Round(safeSample * short.MaxValue));
        }
    }

    private void StopQwenProcess()
    {
        Process? process = _qwenProcess;
        _qwenProcess = null;
        _qwenModelPath = null;
        _qwenUseGpu = false;
        if (process != null) StopProcess(process);
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
            // El audio temporal no debe ocultar el resultado de la transcripción.
        }
    }

    private sealed record QwenRequest(string Audio, string? Language);
    private sealed record QwenResponse(bool Ok = false, string? Text = null, string? Error = null, bool Ready = false);
    private sealed record NemoResponse(string? Text = null);

    private sealed class NemoServerUnavailableException(string message, Exception? inner = null)
        : Exception(message, inner);
}
