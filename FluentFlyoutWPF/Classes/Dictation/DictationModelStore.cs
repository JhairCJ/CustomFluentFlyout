// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>
/// Un modelo de dictado del catálogo: lo que la aplicación ofrece descargar. El
/// archivo es siempre un ggml de whisper.cpp; los multilingües entienden español e
/// inglés y los «.en» solo inglés (más precisos y algo más rápidos en inglés).
/// </summary>
/// <param name="FileName">Nombre del archivo en la carpeta de modelos (la clave estable).</param>
/// <param name="Name">Nombre visible.</param>
/// <param name="Size">Tamaño del archivo, en texto.</param>
/// <param name="Language">Idiomas que entiende.</param>
/// <param name="Recommended">¿Es el que la aplicación recomienda?</param>
public sealed record DictationModelInfo(
    string FileName,
    string Name,
    string Size,
    string Language,
    bool Recommended = false)
{
    /// <summary>Revisión inmutable del repositorio de modelos de Hugging Face.</summary>
    public string Revision { get; init; } = "main";

    /// <summary>SHA-256 esperado; null solo para modelos locales que no son del catálogo.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Dirección de descarga (repositorio ggml de whisper.cpp en Hugging Face).</summary>
    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Revision}/{FileName}";

    /// <summary>¿Solo entiende inglés? (nombre terminado en «.en»)</summary>
    public bool EnglishOnly => FileName.Contains(".en");

    /// <summary>Tamaño aproximado en bytes, para la barra de progreso.</summary>
    public long EstimatedBytes => FileName switch
    {
        "ggml-tiny.bin" or "ggml-tiny.en.bin" => 78_000_000,
        "ggml-base.bin" or "ggml-base.en.bin" => 148_000_000,
        "ggml-small-q5_1.bin" => 190_085_487,
        "ggml-small-q8_0.bin" => 264_464_607,
        "ggml-small.bin" or "ggml-small.en.bin" => 488_000_000,
        "ggml-large-v3-q5_0.bin" => 1_081_000_000,
        "ggml-large-v3-turbo-q5_0.bin" => 574_000_000,
        "ggml-medium-q5_0.bin" => 539_212_467,
        "ggml-medium.bin" => 1_530_000_000,
        _ => 150_000_000,
    };
}

/// <summary>
/// Carpeta y catálogo de modelos del dictado (spec 006 RF-8). Todo lo que el modelo
/// necesita para funcionar es un archivo <c>.bin</c> en disco: esta clase lo localiza,
/// lo descarga una sola vez y lo borra. La inferencia no toca la red nunca.
/// </summary>
public static class DictationModelStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim VadDownloadLock = new(1, 1);

    /// <summary>
    /// Integridad ya comprobada en esta sesión: ruta → tamaño y fecha del archivo. Sin esto,
    /// cargar un modelo volvía a leer y hashear el archivo entero —574 MB un «large», 1,5 GB
    /// un «medium»— en el primer dictado de cada arranque. Si el archivo cambia de tamaño o de
    /// fecha, se vuelve a comprobar.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (long Length, DateTime LastWrite)> VerifiedModels =
        new(StringComparer.OrdinalIgnoreCase);

    private const string VadFileName = "ggml-silero-v6.2.0.bin";
    private const string VadRevision = "6c641e5ffec145595714a49d532297bb8e368c28";
    private const string VadSha256 = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987";
    private const long VadEstimatedBytes = 885_098;
    private const string VadUrl =
        $"https://huggingface.co/sandrohanea/whisper.net/resolve/{VadRevision}/vad/{VadFileName}";

    /// <summary>Los modelos viven junto a los ajustes, en la carpeta del usuario.</summary>
    public static string ModelsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout",
        "models");

    /// <summary>El VAD se guarda separado para que nunca aparezca como modelo Whisper elegible.</summary>
    public static string VadModelPath => Path.Combine(ModelsFolder, "vad", VadFileName);

    /// <summary>Catálogo: tamaños oficiales del repositorio de whisper.cpp.</summary>
    public static IReadOnlyList<DictationModelInfo> Catalog { get; } =
    [
        // Deliberadamente descendente por peso del archivo: los modelos cuantizados
        // pueden tener más parámetros que otro archivo que aparece debajo.
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

    /// <summary>Ruta completa de un modelo del catálogo o de un archivo suelto de la carpeta.</summary>
    public static string PathOf(string fileName) => Path.Combine(ModelsFolder, fileName);

    public static bool IsInstalled(string fileName) => File.Exists(PathOf(fileName));

    /// <summary>
    /// Ruta del modelo ACTIVO, ya resuelta: vale tanto un archivo del catálogo como un
    /// modelo añadido a mano (una ruta absoluta guardada en los ajustes). Devuelve null
    /// si el ajuste está vacío o el archivo ya no está, y el dictado avisa con eso.
    /// </summary>
    public static string? ResolveActivePath(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        string path = Path.IsPathRooted(configured) ? configured : PathOf(configured);
        return File.Exists(path) ? path : null;
    }

    /// <summary>¿Es un modelo solo-inglés? (el nombre del archivo manda, venga de donde venga)</summary>
    public static bool IsEnglishOnly(string path) =>
        Path.GetFileName(path).Contains(".en", StringComparison.OrdinalIgnoreCase);

    /// <summary>Los <c>.bin</c> que hay en la carpeta, además de los del catálogo (añadidos a mano).</summary>
    public static IReadOnlyList<string> InstalledFiles()
    {
        try
        {
            if (!Directory.Exists(ModelsFolder)) return [];
            return [.. Directory.EnumerateFiles(ModelsFolder, "*.bin")
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
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

    /// <summary>
    /// Verifica un modelo catalogado antes de cargarlo. Los modelos añadidos a mano no
    /// tienen un hash de confianza y se dejan pasar para conservar esa funcionalidad.
    /// </summary>
    public static async Task ValidateIntegrityAsync(string path, CancellationToken cancellationToken = default)
    {
        DictationModelInfo? model = Find(Path.GetFileName(path));
        if (model?.Sha256 is not { Length: > 0 } expectedHash) return;

        (long Length, DateTime LastWrite) stamp;
        try
        {
            var file = new FileInfo(path);
            stamp = (file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception ex)
        {
            // Sin tamaño ni fecha no se puede saltar la comprobación: se hace entera.
            Logger.Warn(ex, $"No se pudo leer el estado del modelo {path}; se verificará completo");
            stamp = default;
        }

        if (stamp != default && VerifiedModels.TryGetValue(path, out var verified) && verified == stamp) return;

        string actualHash = await ComputeSha256Async(path, cancellationToken);
        EnsureExpectedHash(path, expectedHash, actualHash);
        if (stamp != default) VerifiedModels[path] = stamp;
    }

    /// <summary>
    /// Garantiza que el modelo Silero VAD esté disponible y sea exactamente el archivo
    /// fijado por la aplicación. Se descarga solo la primera vez que se activa el dictado.
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
                    string actualHash = await ComputeSha256Async(path, cancellationToken);
                    EnsureExpectedHash(path, VadSha256, actualHash);
                    return path;
                }
                catch (InvalidDataException ex)
                {
                    Logger.Warn(ex, "El modelo Silero VAD no coincide con el SHA-256 esperado; se volverá a descargar");
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
    /// Descarga el modelo con progreso (0..1) y lo deja en la carpeta. Se escribe primero
    /// un <c>.part</c> y se renombra al terminar: una descarga cortada nunca deja un
    /// archivo a medias que whisper intente cargar.
    /// </summary>
    public static async Task DownloadAsync(DictationModelInfo model, IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        string finalPath = PathOf(model.FileName);

        Logger.Info($"Descargando modelo de dictado {model.FileName}");
        await DownloadFileAsync(model.Url, finalPath, model.EstimatedBytes, model.Sha256, progress,
            cancellationToken);
        progress?.Report(1);
        Logger.Info($"Modelo de dictado listo: {finalPath}");
    }

    /// <summary>Borra un modelo descargado (nunca toca un archivo añadido a mano fuera de la carpeta).</summary>
    public static void Delete(string fileName)
    {
        string path = PathOf(fileName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"No se pudo borrar el modelo {fileName}");
        }
    }

    /// <summary>Copia un modelo elegido por el usuario a la carpeta de modelos y devuelve su ruta.</summary>
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
        // Hugging Face agradece un agente identificable y no exige clave para estos archivos.
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

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? estimatedBytes;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = File.Create(partPath))
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                byte[] buffer = new byte[81_920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    progress?.Report(total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0);
                }
            }

            string actualHash = Convert.ToHexString(hash.GetHashAndReset());
            EnsureExpectedHash(finalPath, expectedHash, actualHash);
            File.Move(partPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
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
