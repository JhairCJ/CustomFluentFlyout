// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>
/// CrispASR runtime (CrispStrobe/CrispASR): the whisper.cpp fork that understands every
/// GGUF - it reads "general.architecture" and picks the backend on its own (parakeet,
/// canary, granite, cohere...), which is how Parakeet Ultra runs here.
///
/// <para>The Windows build published for Vulkan is downloaded straight from the GitHub
/// release (37.9 MB) and unpacked into the user's folder. It is the variant offered
/// because it needs neither CUDA nor cuBLAS: the Vulkan driver is already in the machine
/// and works on GPUs where the CUDA libraries are missing.</para>
///
/// <para>The app uses CPU or Vulkan according to the acceleration setting, in a persistent
/// loopback worker. Model lifetime follows Keep Model Loaded and Release After Inactivity.</para>
/// </summary>
internal static class CrispAsrRuntime
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    /// <summary>Pinned release: the asset name and its hash never move under a tag.</summary>
    public const string Version = "0.8.40";

    private const string AssetUrl =
        "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.40/crispasr-windows-x86_64-vulkan.zip";
    private const string AssetSha256 = "d78135b46d7881aec909aa398def315427ad784a268979accd18fa569a3b5ce9";
    private const long AssetBytes = 37_857_390;

    /// <summary>
    /// Files that must all be together for the binary to start. Checking only the .exe
    /// would hand over a half extracted folder that fails at launch time.
    /// </summary>
    private static readonly string[] RequiredFiles =
    [
        "crispasr.exe",
        "crispasr.dll",
        "whisper.dll",
        "ggml.dll",
        "ggml-cpu.dll",
        "ggml-base.dll",
        "ggml-vulkan.dll",
    ];

    public static bool IsInstalling { get; private set; }

    /// <summary>Folder that holds the runtime, versioned so two versions can coexist.</summary>
    public static string RootFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentFlyout",
        "runtimes",
        "crispasr",
        Version);

    /// <summary>Full path of the binary, or null when the runtime is not installed.</summary>
    public static string? ExecutablePath
    {
        get
        {
            string folder = RootFolder;
            return RequiredFiles.All(name => File.Exists(Path.Combine(folder, name)))
                ? Path.Combine(folder, "crispasr.exe")
                : null;
        }
    }

    public static bool IsInstalled => ExecutablePath != null;

    private static readonly object DeviceGate = new();
    private static Task<CrispGpuDevice?>? _integratedDeviceTask;

    public sealed record CrispGpuDevice(int Index, string Name);

    /// <summary>Uses ggml's own device IDs and igpu classification, never assumes device 0.</summary>
    public static Task<CrispGpuDevice?> GetIntegratedDeviceAsync(CancellationToken cancellationToken = default)
    {
        string? executable = ExecutablePath;
        if (executable == null) return Task.FromResult<CrispGpuDevice?>(null);
        lock (DeviceGate)
        {
            if (_integratedDeviceTask == null || _integratedDeviceTask.IsFaulted || _integratedDeviceTask.IsCanceled)
                _integratedDeviceTask = DiscoverIntegratedDeviceAsync(executable);
            return _integratedDeviceTask.WaitAsync(cancellationToken);
        }
    }

    private static async Task<CrispGpuDevice?> DiscoverIntegratedDeviceAsync(string executable)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--diagnostics");
        // Device enumeration must see the same set that the inference worker will see.
        process.StartInfo.Environment.Remove("GGML_VK_VISIBLE_DEVICES");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        process.Start();
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout + "\n" + await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("CrispASR could not enumerate Vulkan devices");
            Match match = Regex.Match(output,
                @"(?m)^\s*\[\d+\]\s+igpu\s+name=Vulkan(?<id>\d+)\s+desc=(?<name>.*?)\s+mem=");
            return match.Success ? new(int.Parse(match.Groups["id"].Value), match.Groups["name"].Value) : null;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>
    /// Downloads the pinned zip, checks its SHA-256 and unpacks it into
    /// <see cref="RootFolder"/>. The progress covers the whole operation: 0..0.9 is the
    /// download and the rest is the extraction, which for 38 MB is not instantaneous.
    /// </summary>
    public static async Task InstallAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InstallLock.WaitAsync(cancellationToken);
        try
        {
            IsInstalling = true;
            string folder = RootFolder;
            string zipPath = Path.Combine(
                Path.GetTempPath(),
                $"FluentFlyout-crispasr-{Version}.zip");

            try
            {
                Logger.Info($"Descargando el runtime CrispASR {Version} (Vulkan)");
                await DownloadZipAsync(
                    zipPath,
                    new Progress<double>(value => progress?.Report(value * 0.9)),
                    cancellationToken);

                progress?.Report(0.92);
                ExtractTo(zipPath, folder, new Progress<double>(value =>
                    progress?.Report(0.92 + (value * 0.08))));
            }
            finally
            {
                TryDelete(zipPath);
            }

            if (ExecutablePath == null)
                throw new IOException(
                    $"La descompresión de CrispASR no dejó {RequiredFiles.Length} ficheros donde se esperaba");

            progress?.Report(1);
            Logger.Info($"Runtime CrispASR {Version} instalado en {folder}");
        }
        finally
        {
            IsInstalling = false;
            InstallLock.Release();
        }
    }

    /// <summary>Deletes the runtime folder (used when the install has to be redone).</summary>
    public static void Uninstall()
    {
        try
        {
            string parent = Path.GetDirectoryName(RootFolder) ?? "";
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo borrar el runtime CrispASR; puede estar en uso");
        }
    }

    private static async Task DownloadZipAsync(
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, AssetUrl),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? AssetBytes;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var file = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            byte[] buffer = new byte[81_920];
            long done = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read <= 0) break;

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                progress?.Report(Math.Clamp(done / (double)Math.Max(total, 1), 0, 1));
            }
        }

        string actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actual, AssetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"El runtime CrispASR no coincide con su SHA-256. Esperado {AssetSha256}; obtenido {actual}.");
    }

    /// <summary>
    /// Unpacks the zip flattening its single top level folder, so the .exe keeps the DLLs
    /// next to it. A folder left over from a previous version is removed first: a partial
    /// folder is exactly what would make the binary fail to start later.
    /// </summary>
    private static void ExtractTo(string zipPath, string targetFolder, IProgress<double>? progress)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (Directory.Exists(targetFolder)) Directory.Delete(targetFolder, recursive: true);
        Directory.CreateDirectory(targetFolder);

        string root = Path.GetFullPath(targetFolder) + Path.DirectorySeparatorChar;
        long totalBytes = archive.Entries.Sum(entry => entry.Length);
        long done = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            string destination = Path.GetFullPath(Path.Combine(targetFolder, Flatten(entry.FullName)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Entrada fuera de la carpeta: {entry.FullName}");

            string? directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            entry.ExtractToFile(destination, overwrite: true);

            done += entry.Length;
            progress?.Report(totalBytes > 0 ? Math.Clamp(done / (double)totalBytes, 0, 1) : 0);
        }
    }

    /// <summary>Drops the first path segment ("crispasr-windows-x86_64-vulkan/...").</summary>
    private static string Flatten(string entryPath)
    {
        int separator = entryPath.IndexOf('/');
        return separator >= 0 ? entryPath[(separator + 1)..].Replace('/', Path.DirectorySeparatorChar)
            : entryPath;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentFlyout/1.0 (+https://github.com/JhairCJ/CustomFluentFlyout)");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"No se pudo borrar el zip temporal de CrispASR: {path}");
        }
    }
}
