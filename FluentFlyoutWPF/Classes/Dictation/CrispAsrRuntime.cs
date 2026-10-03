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
/// <para>Separate pinned Windows packages: Vulkan for CPU/integrated graphics, and
/// CUDA 12 for dedicated NVIDIA GPUs. CUDA bundles its matching runtime DLLs, so
/// the user does not need a CUDA Toolkit installation.</para>
///
/// <para>The app uses the selected device in a persistent
/// loopback worker. Model lifetime follows Keep Model Loaded and Release After Inactivity.</para>
/// </summary>
internal static class CrispAsrRuntime
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    /// <summary>Pinned release: the asset name and its hash never move under a tag.</summary>
    public const string Version = "0.8.40";

    private sealed record Package(string Backend, string Sha256, long Bytes, string[] Files)
    {
        public string Url => $"https://github.com/CrispStrobe/CrispASR/releases/download/v{Version}/crispasr-windows-x86_64-{Backend}.zip";
        // Keep the existing Vulkan location so installed CPU/iGPU runtimes survive.
        public string Folder => Backend == "vulkan" ? RootFolder
            : Path.Combine(Path.GetDirectoryName(RootFolder)!, $"{Version}-{Backend}");
        public string? Executable => Files.All(name => File.Exists(Path.Combine(Folder, name)))
            ? Path.Combine(Folder, "crispasr.exe") : null;
    }

    /// <summary>
    /// Files that must all be together for the binary to start. Checking only the .exe
    /// would hand over a half extracted folder that fails at launch time.
    /// </summary>
    private static readonly Package Vulkan = new("vulkan",
        "d78135b46d7881aec909aa398def315427ad784a268979accd18fa569a3b5ce9", 37_857_390,
    [
        "crispasr.exe",
        "crispasr.dll",
        "whisper.dll",
        "ggml.dll",
        "ggml-cpu.dll",
        "ggml-base.dll",
        "ggml-vulkan.dll",
    ]);

    private static readonly Package Cuda = new("cuda",
        "5bca3b6095f6167b43d81491201d1365b93b8dccc772e4823e80c26bdd7f6ec8", 726_799_695,
    [
        "crispasr.exe", "crispasr.dll", "whisper.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll",
        "ggml-cuda.dll", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll",
    ]);

    private static Package ForDevice(DictationDevice device) =>
        device == DictationDevice.DedicatedGpu ? Cuda : Vulkan;

    public static bool IsInstalling { get; private set; }

    /// <summary>Folder that holds the runtime, versioned so two versions can coexist.</summary>
    public static string RootFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentFlyout",
        "runtimes",
        "crispasr",
        Version);

    /// <summary>Full path of the binary, or null when the runtime is not installed.</summary>
    public static string? ExecutablePath => Vulkan.Executable;

    public static string? ExecutableFor(DictationDevice device) => device == DictationDevice.Cpu
        ? Vulkan.Executable ?? Cuda.Executable : ForDevice(device).Executable;

    public static bool IsInstalledFor(DictationDevice device) => ExecutableFor(device) != null;

    public static bool IsInstalled => ExecutablePath != null;

    private static readonly object DeviceGate = new();
    private static readonly Dictionary<string, Task<IReadOnlyList<CrispGpuDevice>>> DeviceTasks = [];

    public sealed record CrispGpuDevice(int Index, string Name, DictationDevice Device);

    /// <summary>Each backend has its own device IDs: Vulkan1 may be CUDA0.</summary>
    public static async Task<CrispGpuDevice?> GetDeviceAsync(
        DictationDevice device, CancellationToken cancellationToken = default) =>
        (await GetDevicesAsync(device, cancellationToken)).FirstOrDefault(adapter => adapter.Device == device);

    public static Task<IReadOnlyList<CrispGpuDevice>> GetDevicesAsync(
        DictationDevice device, CancellationToken cancellationToken = default)
    {
        Package package = ForDevice(device);
        string? executable = package.Executable;
        if (executable == null) return Task.FromResult<IReadOnlyList<CrispGpuDevice>>([]);
        lock (DeviceGate)
        {
            if (!DeviceTasks.TryGetValue(package.Backend, out var task) || task.IsFaulted || task.IsCanceled
                || (task.IsCompletedSuccessfully && task.Result.Count == 0))
                DeviceTasks[package.Backend] = task = DiscoverDevicesAsync(executable, package.Backend);
            return task.WaitAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<CrispGpuDevice>> DiscoverDevicesAsync(string executable, string backend)
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
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            },
        };
        process.StartInfo.ArgumentList.Add("--diagnostics");
        // Device enumeration must see the same set that the inference worker will see.
        process.StartInfo.Environment.Remove("GGML_VK_VISIBLE_DEVICES");
        process.StartInfo.Environment.Remove("CUDA_VISIBLE_DEVICES");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        process.Start();
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout + "\n" + await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"CrispASR could not enumerate {backend} devices");
            return ParseDevices(output, backend);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    internal static IReadOnlyList<CrispGpuDevice> ParseDevices(string output, string backend) =>
        Regex.Matches(output,
            @"(?m)^\s*\[\d+\]\s+(?<type>igpu|gpu)\s+name="
                + (backend == "cuda" ? "CUDA" : "Vulkan")
                + @"(?<id>\d+)\s+desc=(?<name>.*?)\s+mem=")
        .Select(match => new CrispGpuDevice(int.Parse(match.Groups["id"].Value),
            match.Groups["name"].Value, match.Groups["type"].Value == "igpu"
                ? DictationDevice.IntegratedGpu : DictationDevice.DedicatedGpu)).ToArray();

    /// <summary>
    /// Downloads the pinned zip, checks its SHA-256 and unpacks it into
    /// <see cref="RootFolder"/>. The progress covers the whole operation: 0..0.9 is the
    /// download and the rest is the extraction, which for 38 MB is not instantaneous.
    /// </summary>
    public static Task InstallAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        InstallAsync(DictationDevice.IntegratedGpu, progress, cancellationToken);

    public static async Task InstallAsync(
        DictationDevice device,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InstallLock.WaitAsync(cancellationToken);
        try
        {
            IsInstalling = true;
            Package package = ForDevice(device);
            string folder = package.Folder;
            string zipPath = Path.Combine(
                Path.GetTempPath(),
                $"FluentFlyout-crispasr-{Version}-{package.Backend}.zip");

            try
            {
                Logger.Info($"Descargando el runtime CrispASR {Version} ({package.Backend})");
                await DownloadZipAsync(
                    package,
                    zipPath,
                    new Progress<double>(value => progress?.Report(value * 0.9)),
                    cancellationToken);

                progress?.Report(0.92);
                var extractionProgress = new Progress<double>(value => progress?.Report(0.92 + (value * 0.08)));
                await Task.Run(() => ExtractTo(zipPath, folder, cancellationToken, extractionProgress), cancellationToken);
            }
            finally
            {
                TryDelete(zipPath);
            }

            if (package.Executable == null)
                throw new IOException(
                    $"La descompresión de CrispASR no dejó {package.Files.Length} ficheros donde se esperaba");

            progress?.Report(1);
            lock (DeviceGate) DeviceTasks.Remove(package.Backend);
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
        Package package,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, package.Url),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? package.Bytes;
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
        if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"El runtime CrispASR no coincide con su SHA-256. Esperado {package.Sha256}; obtenido {actual}.");
    }

    /// <summary>
    /// Unpacks the zip flattening its single top level folder, so the .exe keeps the DLLs
    /// next to it. A folder left over from a previous version is removed first: a partial
    /// folder is exactly what would make the binary fail to start later.
    /// </summary>
    private static void ExtractTo(string zipPath, string targetFolder, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (Directory.Exists(targetFolder)) Directory.Delete(targetFolder, recursive: true);
        Directory.CreateDirectory(targetFolder);

        string root = Path.GetFullPath(targetFolder) + Path.DirectorySeparatorChar;
        long totalBytes = archive.Entries.Sum(entry => entry.Length);
        long done = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
