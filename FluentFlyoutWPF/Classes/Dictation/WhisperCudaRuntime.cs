// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>NVIDIA redistributables for Whisper; no compiler, SDK or global PATH changes.</summary>
internal static class WhisperCudaRuntime
{
    public const string Version = "13.4.1";
    public const long DownloadBytes = 426_356_852;
    private const string Marker = ".remove-on-restart";
    private static readonly string[] RequiredFiles = ["cublasLt64_13.dll", "cublas64_13.dll", "cudart64_13.dll"];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly List<nint> Handles = [];
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Package[] Packages =
    [
        new("libcublas", "13.7.0.27", 423_620_712,
            "fff93984ee8a85dd8568e4b9000f9e3ef7153f0f73b176756bcbad3c6622d1f0",
            ["cublas64_13.dll", "cublasLt64_13.dll"]),
        new("cuda_cudart", "13.4.49", 2_736_140,
            "e6663f3d3e8949eedc2d5ab92c7c5b9fa3f2a222086d91c42bfc5a38bf2b0225",
            ["cudart64_13.dll"]),
    ];

    private sealed record Package(string Name, string Version, long Bytes, string Sha256, string[] Files)
    {
        public string Url => $"https://developer.download.nvidia.com/compute/cuda/redist/{Name}/windows-x86_64/{Name}-windows-x86_64-{Version}-archive.zip";
    }

    public static string RootFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentFlyout", "runtimes", "whisper-cuda");
    public static string Folder => Path.Combine(RootFolder, Version);
    public static bool RemovalPending => File.Exists(Path.Combine(Folder, Marker));
    public static bool IsInstalled => !RemovalPending && IsComplete(Folder);
    public static bool IsInstalling { get; private set; }
    public static bool IsSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    internal static bool IsComplete(string folder) => RequiredFiles.All(name =>
        File.Exists(Path.Combine(folder, name)) && new FileInfo(Path.Combine(folder, name)).Length > 0)
        && File.Exists(Path.Combine(folder, "libcublas-LICENSE.txt"))
        && File.Exists(Path.Combine(folder, "cuda_cudart-LICENSE.txt"));

    public static async Task InstallAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Whisper CUDA requires Windows x64.");
        await Gate.WaitAsync(cancellationToken);
        string staging = Path.Combine(RootFolder, Version + ".installing");
        try
        {
            IsInstalling = true;
            if (RemovalPending) throw new IOException("Restart the app to finish removing Whisper CUDA first.");
            if (IsInstalled) { progress?.Report(1); return; }
            DeleteManagedFolder(staging, RootFolder);
            Directory.CreateDirectory(staging);
            long completed = 0;
            foreach (var package in Packages)
            {
                string zip = Path.Combine(staging, package.Name + ".zip");
                using var response = await Http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var destination = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    long packageStart = completed;
                    await CopyVerifiedAsync(source, destination, package.Bytes, package.Sha256,
                        new Progress<double>(value => progress?.Report(0.9 * (packageStart + value * package.Bytes) / DownloadBytes)),
                        cancellationToken);
                }
                await Task.Run(() => ExtractRequired(zip, staging, package.Name, package.Files, cancellationToken), cancellationToken);
                File.Delete(zip);
                completed += package.Bytes;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsComplete(staging)) throw new InvalidDataException("The NVIDIA runtime is incomplete.");
            DeleteManagedFolder(Folder, RootFolder);
            Directory.Move(staging, Folder);
            progress?.Report(1);
        }
        finally
        {
            try { DeleteManagedFolder(staging, RootFolder); }
            finally { IsInstalling = false; Gate.Release(); }
        }
    }

    internal static async Task CopyVerifiedAsync(Stream source, Stream destination, long bytes, string sha256,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81_920];
        long received = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            received += count;
            if (received > bytes) throw new InvalidDataException("NVIDIA package exceeded its expected size.");
            hash.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            progress?.Report(received / (double)bytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (received != bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The NVIDIA download failed its size/SHA-256 verification. Please retry.");
    }

    // Extract an allowlist, rather than NVIDIA headers, import libraries and developer tools.
    internal static void ExtractRequired(string zip, string folder, string packageName, string[] files, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zip);
        foreach (string name in files.Append("LICENSE"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = archive.Entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length != 1 || entries[0].Length == 0)
                throw new InvalidDataException($"NVIDIA package does not contain exactly one {name}.");
            string output = name == "LICENSE" ? packageName + "-LICENSE.txt" : name;
            entries[0].ExtractToFile(Path.Combine(folder, output), overwrite: false);
        }
    }

    // CUDA/Whisper libraries live for the process lifetime. Never free them underneath Whisper.
    public static bool TryPrepare()
    {
        lock (Handles)
        {
            if (Handles.Count == RequiredFiles.Length) return !RemovalPending;
            if (!IsSupported || !IsInstalled) return false;
            try
            {
                foreach (string file in RequiredFiles.Skip(Handles.Count))
                {
                    nint handle = LoadLibraryEx(Path.Combine(Folder, file), 0, 0x00000100 | 0x00001000);
                    if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    Handles.Add(handle);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Whisper CUDA libraries could not be loaded; using CPU");
                return false;
            }
        }
    }

    public static async Task UninstallAsync()
    {
        await Gate.WaitAsync();
        try
        {
            lock (Handles)
            {
                if (Handles.Count > 0)
                {
                    File.WriteAllText(Path.Combine(Folder, Marker), "Remove before the next native library load.");
                    return;
                }
            }
            DeleteManagedFolder(Folder, RootFolder);
        }
        finally { Gate.Release(); }
    }

    public static void CompletePendingRemoval()
    {
        lock (Handles)
        {
            if (Handles.Count == 0 && RemovalPending)
            {
                try { DeleteManagedFolder(Folder, RootFolder); }
                catch (IOException ex) { Logger.Warn(ex, "Pending Whisper CUDA removal will be retried next start"); }
                catch (UnauthorizedAccessException ex) { Logger.Warn(ex, "Pending Whisper CUDA removal denied"); }
            }
        }
    }

    internal static void DeleteManagedFolder(string folder, string rootFolder)
    {
        string target = Path.GetFullPath(folder);
        string root = Path.GetFullPath(rootFolder).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase)
            || (Path.GetFileName(target) != Version && Path.GetFileName(target) != Version + ".installing"))
            throw new IOException("Cannot remove files outside the managed Whisper CUDA package.");
        if (!Directory.Exists(target)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0
            || Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories)
                .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Cannot remove a linked runtime folder.");
        Directory.Delete(target, recursive: true);
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
}
