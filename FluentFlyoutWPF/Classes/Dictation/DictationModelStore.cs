// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Net.Http;

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
    /// <summary>Dirección de descarga (repositorio ggml de whisper.cpp en Hugging Face).</summary>
    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}";

    /// <summary>¿Solo entiende inglés? (nombre terminado en «.en»)</summary>
    public bool EnglishOnly => FileName.Contains(".en");

    /// <summary>Tamaño aproximado en bytes, para la barra de progreso.</summary>
    public long EstimatedBytes => FileName switch
    {
        "ggml-tiny.bin" or "ggml-tiny.en.bin" => 78_000_000,
        "ggml-base.bin" or "ggml-base.en.bin" => 148_000_000,
        "ggml-small.bin" or "ggml-small.en.bin" => 488_000_000,
        "ggml-large-v3-turbo-q5_0.bin" => 574_000_000,
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

    /// <summary>Los modelos viven junto a los ajustes, en la carpeta del usuario.</summary>
    public static string ModelsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout",
        "models");

    /// <summary>Catálogo: tamaños oficiales del repositorio de whisper.cpp.</summary>
    public static IReadOnlyList<DictationModelInfo> Catalog { get; } =
    [
        new("ggml-tiny.bin", "Whisper Tiny", "75 MB", "Español, inglés y 97 idiomas más"),
        new("ggml-tiny.en.bin", "Whisper Tiny (English)", "75 MB", "Solo inglés"),
        new("ggml-base.bin", "Whisper Base", "142 MB", "Español, inglés y 97 idiomas más", Recommended: true),
        new("ggml-base.en.bin", "Whisper Base (English)", "142 MB", "Solo inglés"),
        new("ggml-small.bin", "Whisper Small", "466 MB", "Español, inglés y 97 idiomas más"),
        new("ggml-large-v3-turbo-q5_0.bin", "Whisper Large v3 Turbo (cuantizado)", "547 MB", "Español, inglés y 97 idiomas más"),
        new("ggml-medium.bin", "Whisper Medium", "1,5 GB", "Español, inglés y 97 idiomas más"),
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
    /// Descarga el modelo con progreso (0..1) y lo deja en la carpeta. Se escribe primero
    /// un <c>.part</c> y se renombra al terminar: una descarga cortada nunca deja un
    /// archivo a medias que whisper intente cargar.
    /// </summary>
    public static async Task DownloadAsync(DictationModelInfo model, IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsFolder);
        string finalPath = PathOf(model.FileName);
        string partPath = finalPath + ".part";

        Logger.Info($"Descargando modelo de dictado {model.FileName}");
        using var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? model.EstimatedBytes;

        await using (var file = File.Create(partPath))
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            byte[] buffer = new byte[81_920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                progress?.Report(total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0);
            }
        }
        File.Move(partPath, finalPath, overwrite: true);
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFlyout/1.0 (+https://github.com/unchihugo/FluentFlyout)");
        return client;
    }
}
