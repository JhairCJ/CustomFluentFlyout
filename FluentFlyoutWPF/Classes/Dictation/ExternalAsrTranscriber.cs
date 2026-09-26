// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>
/// Puente para modelos que no son Whisper GGML: Parakeet usa el CLI nativo de NVIDIA y
/// Qwen usa el paquete oficial qwen-asr en un proceso Python persistente.
/// </summary>
public sealed class ExternalAsrTranscriber : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _qwenLock = new(1, 1);
    private Process? _qwenProcess;
    private string? _qwenModelPath;
    private bool _qwenUseGpu;
    private bool _disposed;

    public bool RuntimeLoaded => _qwenProcess is { HasExited: false };
    public bool UsingGpu => RuntimeLoaded && _qwenUseGpu;

    public async Task PreloadAsync(
        DictationModelInfo model,
        string modelPath,
        bool useGpu,
        CancellationToken cancellationToken)
    {
        if (model.Backend != DictationModelBackend.QwenAsr) return;

        await _qwenLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureQwenProcessCoreAsync(modelPath, useGpu, cancellationToken);
        }
        finally
        {
            _qwenLock.Release();
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
                    modelPath, wavPath, useGpu, cancellationToken),
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopQwenProcess();
        _qwenLock.Dispose();
    }

    private static async Task<string> TranscribeWithNemoAsync(
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
        await _qwenLock.WaitAsync(cancellationToken);
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
            _qwenLock.Release();
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
}
