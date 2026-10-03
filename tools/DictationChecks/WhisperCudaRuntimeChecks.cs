using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using FluentFlyoutWPF.Classes.Dictation;

// Disposable fixtures only: no runtime download, native CUDA load or user settings changes.
internal static class WhisperCudaRuntimeChecks
{
    private static readonly Type Runtime = typeof(ExternalAsrTranscriber).Assembly
        .GetType("FluentFlyoutWPF.Classes.Dictation.WhisperCudaRuntime")!;

    public static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "FluentFlyout-WhisperFixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = [1, 2, 3, 4];
            string hash = Convert.ToHexString(SHA256.HashData(data));
            await Copy(data, 4, hash, CancellationToken.None);
            Check(true, "Verified downloads accept matching size and SHA-256");
            Check(await RejectsAsync(() => Copy(data, 4, new string('0', 64), CancellationToken.None)), "Corrupt downloads are rejected");
            Check(await RejectsAsync(() => Copy(data, 5, hash, CancellationToken.None)), "Truncated downloads are rejected");
            Check(await RejectsAsync(() => Copy(data, 3, hash, CancellationToken.None)), "Oversized downloads are rejected");
            Check(await RejectsAsync(() => Copy(data, 4, hash, new CancellationToken(true))), "Cancellation interrupts installation");
            string zip = Path.Combine(root, "fixture.zip");
            string target = Path.Combine(root, "extracted");
            Directory.CreateDirectory(target);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (string name in new[] { "bin/x64/cublas64_13.dll", "bin/x64/cublasLt64_13.dll", "bin/x64/cudart64_13.dll", "LICENSE", "include/header.h", "../../outside.txt" })
                {
                    using var writer = new StreamWriter(archive.CreateEntry("package/" + name).Open());
                    writer.Write("fixture");
                }
            Extract(zip, target, "libcublas", ["cublas64_13.dll", "cublasLt64_13.dll"]);
            Extract(zip, target, "cuda_cudart", ["cudart64_13.dll"]);
            Check((bool)Call("IsComplete", target)!, "DLLs and both NVIDIA licenses are required for a complete installation");
            Check(Directory.GetFiles(target).Length == 5 && !File.Exists(Path.Combine(root, "outside.txt")), "Extraction keeps only runtime DLLs and licenses, ignoring headers and traversal entries");
            File.Delete(Path.Combine(target, "cudart64_13.dll"));
            Check(!(bool)Call("IsComplete", target)!, "Partial extraction never counts as installed");
            Check(Rejects(() => Extract(zip, target, "missing", ["missing.dll"])), "Missing required DLLs reject installation");
            string managed = Path.Combine(root, "whisper-cuda");
            string version = (string)Runtime.GetField("Version")!.GetRawConstantValue()!;
            string package = Path.Combine(managed, version);
            Directory.CreateDirectory(package);
            string neighbor = Path.Combine(managed, "other-engine");
            Directory.CreateDirectory(neighbor);
            string model = Path.Combine(root, "model.bin");
            File.WriteAllText(model, "model fixture");
            Call("DeleteManagedFolder", package, managed);
            Check(!Directory.Exists(package) && Directory.Exists(neighbor) && File.Exists(model), "Removing Whisper dependencies preserves other engines and models");
            Check(Rejects(() => Call("DeleteManagedFolder", managed, managed)), "Removing the runtime root is rejected");
            Check(Rejects(() => Call("DeleteManagedFolder", neighbor, managed)), "Removing another runtime is rejected");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected fixture cleanup path");
            Directory.Delete(root, true);
        }
    }

    private static async Task Copy(byte[] data, long size, string hash, CancellationToken token)
    {
        using var source = new MemoryStream(data);
        using var target = new MemoryStream();
        await (Task)Call("CopyVerifiedAsync", source, target, size, hash, null!, token)!;
    }
    private static void Extract(string zip, string target, string package, string[] names) =>
        Call("ExtractRequired", zip, target, package, names, CancellationToken.None);
    private static object? Call(string method, params object[] args) => Runtime
        .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException or InvalidDataException) { return true; }
    }
    private static async Task<bool> RejectsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { return true; }
    }
    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
