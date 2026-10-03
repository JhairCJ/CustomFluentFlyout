using System.IO;
using System.Reflection;
using FluentFlyoutWPF.Classes.Dictation;

// Exercise deletion on disposable package folders, never on the user's installations.
internal static class RuntimeRemovalChecks
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "FluentFlyout-RuntimeRemoval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string packages = Path.Combine(root, "crispasr");
            string vulkan = Path.Combine(packages, "0.8.40");
            string cuda12 = Path.Combine(packages, "0.8.40-cuda");
            string cuda13 = Path.Combine(packages, "0.8.40-cuda13");
            foreach (string folder in new[] { vulkan, cuda12, cuda13 })
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "crispasr.exe"), "disposable fixture");
            }
            string model = Path.Combine(root, "model.gguf");
            File.WriteAllText(model, "preserved model fixture");
            Type crisp = RuntimeType("CrispAsrRuntime");
            Call(crisp, "DeletePackageFolder", cuda13, packages);
            Check(!Directory.Exists(cuda13) && Directory.Exists(cuda12) && Directory.Exists(vulkan) && File.Exists(model),
                "Removing CUDA 13 preserves CUDA 12, Vulkan and speech models");
            Call(crisp, "DeletePackageFolder", cuda13, packages);
            Check(Rejects(() => Call(crisp, "DeletePackageFolder", packages, packages)), "Removing the whole runtime root is rejected");
            Check(Rejects(() => Call(crisp, "DeletePackageFolder", root, packages)), "Removal outside the managed runtime folder is rejected");
            Call(crisp, "DeletePackageFolder", vulkan, packages);
            Check(!Directory.Exists(vulkan) && Directory.Exists(cuda12), "Removing Vulkan preserves the dedicated CUDA runtime");
            string programs = Path.Combine(root, "Programs");
            string nemo = Path.Combine(programs, "NeMoSpeech");
            Directory.CreateDirectory(nemo);
            Type nemoType = RuntimeType("NemoSpeechRuntime");
            Check(Rejects(() => Call(nemoType, "DeleteInstallation", nemo, programs)), "NeMo removal requires its installation marker");
            File.WriteAllText(Path.Combine(nemo, ".nemo-speech-install"), "0.1.0 windows x86_64 cuda");
            Check(Rejects(() => Call(nemoType, "DeleteInstallation", root, programs)), "NeMo cannot remove an arbitrary folder");
            Call(nemoType, "DeleteInstallation", nemo, programs);
            Check(!Directory.Exists(nemo) && Directory.Exists(cuda12) && File.Exists(model), "NeMo removal keeps other runtimes and speech models");
            string cleaned = (string)Call(nemoType, "RemovePathEntry", @"C:\Windows;""C:\NeMoSpeech\bin\"";C:\Tools", @"c:\nemospeech\bin")!;
            Check(cleaned == @"C:\Windows;C:\Tools", "NeMo PATH cleanup removes only its exact quoted entry");
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected fixture cleanup path");
            Directory.Delete(root, recursive: true);
        }
    }

    private static Type RuntimeType(string name) => typeof(ExternalAsrTranscriber).Assembly
        .GetType("FluentFlyoutWPF.Classes.Dictation." + name)!;
    private static object? Call(Type type, string method, params object[] args) => type
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException) { return true; }
    }
    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
