// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>Engine that understands the format of a catalog model.</summary>
public enum DictationModelBackend
{
    Whisper,
    NemoSpeech,
    QwenAsr,
}

/// <summary>A file that is part of a downloadable model.</summary>
public sealed record DictationModelArtifact(
    string RemotePath,
    string LocalPath,
    long EstimatedBytes,
    string? Sha256 = null);

/// <summary>
/// A dictation model from the catalog: what the application offers to download. Whisper,
/// Parakeet and Nemotron use a single file; Qwen uses a folder with its weights and metadata.
/// </summary>
/// <param name="FileName">File or folder name inside models (the stable key).</param>
/// <param name="Name">Nombre visible.</param>
/// <param name="Size">Model size, as text.</param>
/// <param name="Language">Languages it understands.</param>
/// <param name="Recommended">Is this the one the application recommends?</param>
public sealed record DictationModelInfo(
    string FileName,
    string Name,
    string Size,
    string Language,
    bool Recommended = false)
{
    /// <summary>Local backend that must be used for this model.</summary>
    public DictationModelBackend Backend { get; init; } = DictationModelBackend.Whisper;

    /// <summary>Visible requirement of the external runtime, if the model needs one.</summary>
    public string Runtime { get; init; } = "";

    /// <summary>Repositorio inmutable de modelos de Hugging Face.</summary>
    public string Repository { get; init; } = "ggerganov/whisper.cpp";

    /// <summary>Immutable revision of the Hugging Face model repository.</summary>
    public string Revision { get; init; } = "main";

    /// <summary>Remote file name when it differs from the local name.</summary>
    public string RemoteFileName { get; init; } = "";

    /// <summary>Expected SHA-256; null only for local models that are not in the catalog.</summary>
    public string? Sha256 { get; init; }

    /// <summary>This model expects regional language codes, such as "es-ES".</summary>
    public bool UsesLocaleLanguageCodes { get; init; }

    /// <summary>Extra files for a model that occupies a folder.</summary>
    public IReadOnlyList<DictationModelArtifact> Artifacts { get; init; } = [];

    /// <summary>Download URL of the model file on Hugging Face.</summary>
    public string Url =>
        UrlFor(string.IsNullOrWhiteSpace(RemoteFileName) ? FileName : RemoteFileName);

    /// <summary>Builds the URL of a specific file of the pinned repository.</summary>
    public string UrlFor(string remotePath) =>
        $"https://huggingface.co/{Repository}/resolve/{Revision}/{remotePath}";

    /// <summary>Does it only understand English? (name ending in ".en")</summary>
    public bool EnglishOnly => FileName.Contains(".en");

    /// <summary>Files that must be downloaded to make this model ready.</summary>
    public IReadOnlyList<DictationModelArtifact> DownloadArtifacts => Artifacts.Count > 0
        ? Artifacts
        : [new(
            string.IsNullOrWhiteSpace(RemoteFileName) ? FileName : RemoteFileName,
            FileName,
            EstimateSingleFile(FileName),
            Sha256)];

    /// <summary>Approximate total size in bytes, for the progress bar.</summary>
    public long EstimatedBytes => DownloadArtifacts.Sum(artifact => artifact.EstimatedBytes);

    private static long EstimateSingleFile(string fileName) => fileName switch
    {
        "ggml-tiny.bin" or "ggml-tiny.en.bin" => 78_000_000,
        "ggml-base.bin" or "ggml-base.en.bin" => 148_000_000,
        "ggml-small-q5_1.bin" => 190_085_487,
        "ggml-small-q8_0.bin" => 264_464_607,
        "ggml-small.bin" or "ggml-small.en.bin" => 488_000_000,
        "ggml-large-v3-q5_0.bin" => 1_081_000_000,
        "ggml-large-v3-turbo-q5_0.bin" => 574_000_000,
        "ggml-distil-large-v3-multi4.bin" => 1_519_521_155,
        "parakeet-tdt-0.6b-v3.q8_0.gguf" => 713_975_456,
        "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf" => 742_090_464,
        "ggml-medium-q5_0.bin" => 539_212_467,
        "ggml-medium.bin" => 1_530_000_000,
        _ => 150_000_000,
    };
}

/// <summary>
/// Dictation model folder and catalog (spec 006 RF-8). Whisper, Parakeet and Nemotron
/// use local files; Qwen uses a local folder with all its weights and metadata.
/// Inference never touches the network.
/// </summary>
public static class DictationModelStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim VadDownloadLock = new(1, 1);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DownloadRetryDelay = TimeSpan.FromSeconds(2);
    private const int MaxDownloadAttempts = 3;
    private static readonly ConcurrentDictionary<string, byte> ActiveDownloads = new(
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Tells the page that a download started or stopped being active.</summary>
    public static event Action<string>? DownloadStateChanged;

    /// <summary>
    /// Integrity already checked in this session: path -> file size and timestamp. Without
    /// this, loading a model re-read and re-hashed the whole file - 574 MB for a "large",
    /// 1.5 GB for a "medium" - on the first dictation of every startup. If the file changes
    /// size or date, it is checked again.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (long Length, DateTime LastWrite)> VerifiedModels =
        new(StringComparer.OrdinalIgnoreCase);

    private const string VadFileName = "ggml-silero-v6.2.0.bin";
    private const string VadRevision = "6c641e5ffec145595714a49d532297bb8e368c28";
    private const string VadSha256 = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987";
    private const long VadEstimatedBytes = 885_098;
    private const string VadUrl =
        $"https://huggingface.co/sandrohanea/whisper.net/resolve/{VadRevision}/vad/{VadFileName}";

    /// <summary>Models live next to the settings, in the user's folder.</summary>
    public static string ModelsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout",
        "models");

    public static bool IsDownloading(string fileName) => ActiveDownloads.ContainsKey(fileName);

    /// <summary>The VAD is stored apart so it never shows up as a selectable Whisper model.</summary>
    public static string VadModelPath => Path.Combine(ModelsFolder, "vad", VadFileName);

    /// <summary>Catalog: weights pinned to concrete Hugging Face revisions.</summary>
    public static IReadOnlyList<DictationModelInfo> Catalog { get; } =
    [
        // Deliberately descending by file size: quantized models can have more parameters
        // than another file listed below them.
        new("qwen3-asr-0.6b", "Qwen3-ASR 0.6B", "1,8 GB",
            "Inglés, español y 28 idiomas más")
        {
            Backend = DictationModelBackend.QwenAsr,
            Runtime = "Requiere Python 3.12 + qwen-asr",
            Repository = "Qwen/Qwen3-ASR-0.6B",
            Revision = "5eb144179a02acc5e5ba31e748d22b0cf3e303b0",
            Artifacts =
            [
                new("config.json", "config.json", 6_193),
                new("generation_config.json", "generation_config.json", 142),
                new("preprocessor_config.json", "preprocessor_config.json", 330),
                new("tokenizer_config.json", "tokenizer_config.json", 12_487),
                new("chat_template.json", "chat_template.json", 1_161),
                new("merges.txt", "merges.txt", 1_671_853),
                new("vocab.json", "vocab.json", 2_776_833),
                new("model.safetensors", "model.safetensors", 1_876_091_704,
                    "79d6cbd4c98c7bbffe9db2edac07f56cd6637d0d5944b27f6c2b8353840323ea"),
            ],
        },
        new("nemotron-3.5-asr-streaming-0.6b.q8_0.gguf", "NVIDIA Nemotron 3.5 ASR 0.6B (GGUF)", "708 MB",
            "Español, inglés y otros idiomas")
        {
            Backend = DictationModelBackend.NemoSpeech,
            Runtime = "Requiere NeMo-Speech.cpp",
            Repository = "nvidia/nemotron-3.5-asr-streaming-0.6b",
            RemoteFileName = "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf",
            Revision = "ea30d66debe3740a08b573244286791d423d6b3e",
            Sha256 = "3fc991d3badad7277c11030a7519832cddaf2057aafed6d4b25147e953a070b1",
            UsesLocaleLanguageCodes = true,
        },
        new("parakeet-tdt-0.6b-v3.q8_0.gguf", "NVIDIA Parakeet TDT v3 (GGUF)", "681 MB",
            "Inglés, español y 23 idiomas más")
        {
            Backend = DictationModelBackend.NemoSpeech,
            Runtime = "Requiere NeMo-Speech.cpp",
            Repository = "nvidia/parakeet-tdt-0.6b-v3",
            RemoteFileName = "parakeet-tdt-0.6b-v3.q8_0.gguf",
            Revision = "541d1f99c6b0c3cd0b11a95167540bb8edefd82b",
            Sha256 = "e3880d0aaaaf2c308ea2c35016b2b895c423eb3fda924c1b463d1c19b7f4d32e",
        },
        // This multilingual distilled variant publishes the ggml directly in the author's
        // repository; the local name avoids depending on "ggml-model.bin".
        new("ggml-distil-large-v3-multi4.bin", "Whisper Large v3 destilado (4 idiomas)", "1,5 GB",
            "Inglés, español, francés y alemán")
        {
            Repository = "bofenghuang/whisper-large-v3-distil-multi4-v0.2",
            RemoteFileName = "ggml-model.bin",
            Revision = "c208e2df8510baad5d37e1e74c5c1824a929bc08",
            Sha256 = "acb16925173055f2096ed9c71db1525c13b529d56828f0bb6b916504f2740296",
        },
        new("ggml-medium.bin", "Whisper Medium", "1,5 GB", "Español, inglés y 97 idiomas más")
        {
            Revision = "80da2d8bfee42b0e836fc3a9890373e5defc00a6",
            Sha256 = "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
        },
        new("ggml-large-v3-q5_0.bin", "Whisper Large v3 (cuantizado)", "1,1 GB", "Español, inglés y 97 idiomas más")
        {
            Revision = "362722b3fdcd2300b58a8286933ead1c48619667",
            Sha256 = "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1",
        },
        new("ggml-large-v3-turbo-q5_0.bin", "Whisper Large v3 Turbo (cuantizado)", "547 MB", "Español, inglés y 97 idiomas más")
        {
            Revision = "98aa99a0a9db05ae2342309f5096248665f7cba3",
            Sha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
        },
        new("ggml-medium-q5_0.bin", "Whisper Medium (cuantizado Q5)", "539 MB", "Español, inglés y 97 idiomas más")
        {
            Revision = "f281eb45af861ab5e5297d23694b7d46e090c02c",
            Sha256 = "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f",
        },
        new("ggml-small.bin", "Whisper Small", "466 MB", "Español, inglés y 97 idiomas más")
        {
            Revision = "80da2d8bfee42b0e836fc3a9890373e5defc00a6",
            Sha256 = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
        },
        new("ggml-small-q8_0.bin", "Whisper Small (cuantizado Q8)", "264 MB", "Español, inglés y 97 idiomas más")
        {
            Revision = "0b364b566045a405be7225ee1e415a073e04da77",
            Sha256 = "49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f",
        },
        new("ggml-small-q5_1.bin", "Whisper Small (cuantizado Q5)", "190 MB", "Español, inglés y 97 idiomas más", Recommended: true)
        {
            Revision = "f281eb45af861ab5e5297d23694b7d46e090c02c",
            Sha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb",
        },
        new("ggml-base.bin", "Whisper Base", "142 MB", "Español, inglés y 97 idiomas más")
        {
            Revision = "80da2d8bfee42b0e836fc3a9890373e5defc00a6",
            Sha256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        },
        new("ggml-base.en.bin", "Whisper Base (English)", "142 MB", "Solo inglés")
        {
            Revision = "80da2d8bfee42b0e836fc3a9890373e5defc00a6",
            Sha256 = "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002",
        },
        new("ggml-tiny.bin", "Whisper Tiny", "75 MB", "Español, inglés y 97 idiomas más")
        {
            Revision = "80da2d8bfee42b0e836fc3a9890373e5defc00a6",
            Sha256 = "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
        },
        new("ggml-tiny.en.bin", "Whisper Tiny (English)", "75 MB", "Solo inglés")
        {
            Revision = "80da2d8bfee42b0e836fc3a9890373e5defc00a6",
            Sha256 = "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f",
        },
    ];

    /// <summary>Full path of a catalog model or of a loose file in the folder.</summary>
    public static string PathOf(string fileName) => Path.Combine(ModelsFolder, fileName);

    public static bool IsInstalled(string fileName)
    {
        DictationModelInfo? model = Find(fileName);
        if (model != null)
        {
            string root = PathOf(model.FileName);
            return model.Artifacts.Count > 0
                ? Directory.Exists(root) && model.DownloadArtifacts.All(artifact =>
                    File.Exists(Path.Combine(root, artifact.LocalPath)))
                : File.Exists(root);
        }

        return File.Exists(PathOf(fileName));
    }

    /// <summary>
    /// Path of the ACTIVE model, already resolved: it covers both a catalog file and a
    /// hand-added model (an absolute path saved in the settings). Returns null when the
    /// setting is empty or the file is gone, and dictation reports that.
    /// </summary>
    public static string? ResolveActivePath(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        string path = Path.IsPathRooted(configured) ? configured : PathOf(configured);
        DictationModelInfo? catalogModel = Find(Path.GetFileName(path));
        if (catalogModel?.Artifacts.Count > 0 && !IsInstalled(catalogModel.FileName)) return null;
        return File.Exists(path) || Directory.Exists(path) ? path : null;
    }

    /// <summary>Is it an English-only model? (the file name decides, wherever it came from)</summary>
    public static bool IsEnglishOnly(string path) =>
        Path.GetFileName(path).Contains(".en", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file models present in the folder (hand-added).</summary>
    public static IReadOnlyList<string> InstalledFiles()
    {
        try
        {
            if (!Directory.Exists(ModelsFolder)) return [];
            return [.. Directory.EnumerateFiles(ModelsFolder)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Where(name => string.Equals(Path.GetExtension(name), ".bin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(name), ".gguf", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo leer la carpeta de modelos de dictado");
            return [];
        }
    }

    public static DictationModelInfo? Find(string fileName) =>
        Catalog.FirstOrDefault(model => string.Equals(model.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Verifies the artifacts of a cataloged model before loading it.</summary>
    public static async Task ValidateIntegrityAsync(string path, CancellationToken cancellationToken = default)
    {
        DictationModelInfo? model = Find(Path.GetFileName(path));
        if (model == null) return;

        foreach (DictationModelArtifact artifact in model.DownloadArtifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Sha256)) continue;
            string artifactPath = model.Artifacts.Count > 0
                ? Path.Combine(path, artifact.LocalPath)
                : path;
            await ValidateFileIntegrityAsync(artifactPath, artifact.Sha256, cancellationToken);
        }
    }

    private static async Task ValidateFileIntegrityAsync(
        string path,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        (long Length, DateTime LastWrite) stamp;
        try
        {
            var file = new FileInfo(path);
            stamp = (file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception ex)
        {
            // Without size or timestamp the check cannot be skipped: it runs in full.
            Logger.Warn(ex, $"No se pudo leer el estado del modelo {path}; se verificará completo");
            stamp = default;
        }

        if (stamp != default && VerifiedModels.TryGetValue(path, out var verified) && verified == stamp) return;

        string actualHash = await ComputeSha256Async(path, cancellationToken);
        EnsureExpectedHash(path, expectedHash, actualHash);
        if (stamp != default) VerifiedModels[path] = stamp;
    }

    /// <summary>
    /// Guarantees that the Silero VAD model is available and is exactly the file pinned
    /// by the application. It is downloaded only the first time dictation runs, and the
    /// hash is only recomputed when the file itself changes (same length, same write
    /// time), so a dictation session never re-reads it.
    /// </summary>
    public static async Task<string> EnsureVadModelAsync(CancellationToken cancellationToken = default)
    {
        string path = VadModelPath;
        await VadDownloadLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                try
                {
                    await ValidateFileIntegrityAsync(path, VadSha256, cancellationToken);
                    return path;
                }
                catch (InvalidDataException ex)
                {
                    Logger.Warn(ex, "The Silero VAD model does not match the expected SHA-256; it will be downloaded again");
                    TryDelete(path);
                }
            }

            await DownloadFileAsync(
                VadUrl,
                path,
                VadEstimatedBytes,
                VadSha256,
                progress: null,
                cancellationToken);
            return path;
        }
        finally
        {
            VadDownloadLock.Release();
        }
    }

    /// <summary>
    /// Downloads the model with progress (0..1) and leaves it in the folder. It writes a
    /// <c>.part</c> first and renames it at the end: an interrupted download never leaves
    /// a half file for whisper to try loading.
    /// </summary>
    public static async Task DownloadAsync(DictationModelInfo model, IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!ActiveDownloads.TryAdd(model.FileName, 0))
            throw new InvalidOperationException("Este modelo ya se está descargando");

        try
        {
            Logger.Info($"Descargando modelo de dictado {model.FileName}");
            IReadOnlyList<DictationModelArtifact> artifacts = model.DownloadArtifacts;
            long totalEstimatedBytes = Math.Max(1, model.EstimatedBytes);
            long completedEstimatedBytes = 0;
            string modelRoot = PathOf(model.FileName);
            if (model.Artifacts.Count > 0) Directory.CreateDirectory(modelRoot);

            foreach (DictationModelArtifact artifact in artifacts)
            {
                string finalPath = model.Artifacts.Count > 0
                    ? Path.Combine(modelRoot, artifact.LocalPath)
                    : modelRoot;

                // Finished artifacts are not downloaded again when retrying a composite model. The
                // .part file means that artifact still needs
                // continuar o reiniciarse.
                if (File.Exists(finalPath) && !File.Exists(finalPath + ".part"))
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(artifact.Sha256))
                            await ValidateFileIntegrityAsync(finalPath, artifact.Sha256, cancellationToken);
                    }
                    catch (InvalidDataException ex)
                    {
                        Logger.Warn(ex, $"El artefacto local {finalPath} no coincide; se volverá a descargar");
                        TryDelete(finalPath);
                    }

                    if (File.Exists(finalPath))
                    {
                        completedEstimatedBytes += artifact.EstimatedBytes;
                        progress?.Report(Math.Clamp(
                            completedEstimatedBytes / (double)totalEstimatedBytes,
                            0,
                            1));
                        continue;
                    }
                }

                var artifactProgress = progress == null
                    ? null
                    : new Progress<double>(value => progress.Report(Math.Clamp(
                        (completedEstimatedBytes + (long)(value * artifact.EstimatedBytes))
                            / (double)totalEstimatedBytes,
                        0,
                        1)));

                await DownloadFileAsync(
                    model.UrlFor(artifact.RemotePath),
                    finalPath,
                    artifact.EstimatedBytes,
                    artifact.Sha256,
                    artifactProgress,
                    cancellationToken);
                completedEstimatedBytes += artifact.EstimatedBytes;
            }

            progress?.Report(1);
            Logger.Info($"Modelo de dictado listo: {modelRoot}");
        }
        finally
        {
            ActiveDownloads.TryRemove(model.FileName, out _);
            NotifyDownloadStateChanged(model.FileName);
        }
    }

    private static void NotifyDownloadStateChanged(string fileName)
    {
        try
        {
            DownloadStateChanged?.Invoke(fileName);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Un observador de descargas de modelos falló");
        }
    }

    /// <summary>Deletes a downloaded model (never touches a hand-added file outside the folder).</summary>
    public static void Delete(string fileName)
    {
        string path = PathOf(fileName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"No se pudo borrar el modelo {fileName}");
        }
    }

    /// <summary>Copies a user-chosen model into the models folder and returns its path.</summary>
    public static async Task<string> AddLocalAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsFolder);
        string target = PathOf(Path.GetFileName(sourcePath));
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            await using var source = File.OpenRead(sourcePath);
            await using var destination = File.Create(target);
            await source.CopyToAsync(destination, cancellationToken);
        }
        return target;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // Hugging Face appreciates an identifiable agent and needs no key for these files.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFlyout/1.0 (+https://github.com/JhairCJ/CustomFluentFlyout)");
        return client;
    }

    private static async Task DownloadFileAsync(
        string url,
        string finalPath,
        long estimatedBytes,
        string? expectedHash,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string partPath = finalPath + ".part";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadFileAttemptAsync(
                    url,
                    finalPath,
                    partPath,
                    estimatedBytes,
                    expectedHash,
                    progress,
                    cancellationToken);
                return;
            }
            catch (InvalidDataException)
            {
                // A file that arrived complete but does not match cannot be safely resumed: it is
                // deleted so the next attempt starts clean.
                TryDelete(partPath);
                throw;
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested
                && attempt < MaxDownloadAttempts
                && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                Logger.Warn(ex,
                    $"Descarga interrumpida para {Path.GetFileName(finalPath)}; "
                    + $"reintento {attempt + 1}/{MaxDownloadAttempts}");
                await Task.Delay(DownloadRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task DownloadFileAttemptAsync(
        string url,
        string finalPath,
        string partPath,
        long estimatedBytes,
        string? expectedHash,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        long existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
            request.Headers.Range = new RangeHeaderValue(existingLength, null);

        // HttpClient.Timeout is infinite because models can weigh gigabytes. The token is
        // reset after every block: it only limits the time without receiving data, which
        // is the case that used to leave the row stuck forever.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(DownloadIdleTimeout);
        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            attemptCts.Token);

        if (existingLength > 0
            && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // A 416 also shows up when the partial is already complete: Range starts right after
            // the last byte. Check it locally before deleting and restarting a large
            // download that has already finished.
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                string partialHash = await ComputeSha256Async(partPath, cancellationToken);
                if (string.Equals(partialHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(partPath, finalPath, overwrite: true);
                    progress?.Report(1);
                    return;
                }
            }

            TryDelete(partPath);
            throw new HttpRequestException("El servidor rechazó continuar el archivo parcial");
        }

        response.EnsureSuccessStatusCode();
        bool append = existingLength > 0
            && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (append
            && response.Content.Headers.ContentRange?.From is long rangeStart
            && rangeStart != existingLength)
        {
            TryDelete(partPath);
            throw new HttpRequestException("El servidor devolvió un rango distinto al solicitado");
        }

        // If the server ignores Range and answers 200, the partial file is safely restarted
        // instead of concatenating two copies of the content.
        long contentLength = response.Content.Headers.ContentLength ?? 0;
        long total = response.Content.Headers.ContentRange?.Length
            ?? (contentLength > 0 && append ? existingLength + contentLength : contentLength);
        if (total <= 0) total = estimatedBytes;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        if (append)
        {
            await using var partial = new FileStream(
                partPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] partialBuffer = new byte[81_920];
            int partialRead;
            while ((partialRead = await partial.ReadAsync(partialBuffer.AsMemory(), cancellationToken)) > 0)
            {
                hash.AppendData(partialBuffer, 0, partialRead);
                done += partialRead;
            }
        }

        progress?.Report(total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0);
        await using (var file = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await using var source = await response.Content.ReadAsStreamAsync(attemptCts.Token);
            byte[] buffer = new byte[81_920];
            int read;
            while (true)
            {
                attemptCts.CancelAfter(DownloadIdleTimeout);
                read = await source.ReadAsync(buffer.AsMemory(), attemptCts.Token);
                if (read <= 0) break;

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), attemptCts.Token);
                done += read;
                progress?.Report(total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0);
            }
        }

        string actualHash = Convert.ToHexString(hash.GetHashAndReset());
        EnsureExpectedHash(partPath, expectedHash, actualHash);
        File.Move(partPath, finalPath, overwrite: true);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81_920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            hash.AppendData(buffer, 0, read);

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void EnsureExpectedHash(string path, string? expectedHash, string actualHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return;
        if (string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase)) return;

        throw new InvalidDataException(
            $"El modelo {Path.GetFileName(path)} no coincide con su SHA-256 esperado. "
            + $"Esperado: {expectedHash}; obtenido: {actualHash}.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"No se pudo limpiar el archivo temporal del modelo: {path}");
        }
    }
}
