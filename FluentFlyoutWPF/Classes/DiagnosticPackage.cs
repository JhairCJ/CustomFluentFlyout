using System.IO;
using System.IO.Compression;
using System.Text;

namespace FluentFlyoutWPF.Classes;

internal static class DiagnosticPackage
{
    private const int MaxFileBytes = 2 * 1024 * 1024;

    internal static string Create(string logsDirectory, string outputDirectory, DateTime now)
    {
        var files = Recent(logsDirectory, "logs.*.txt", 10)
            .Select(file => (file, name: "logs/" + file.Name))
            .Concat(Recent(Path.Combine(logsDirectory, "diagnostics"), "session-*.jsonl", 10)
                .Select(file => (file, name: "diagnostics/" + file.Name)))
            .ToArray();
        if (files.Length == 0) throw new FileNotFoundException("No FluentFlyout diagnostics were found.");

        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, $"FluentFlyout-diagnostics-{now:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}.zip");
        using var archive = new ZipArchive(new FileStream(path, FileMode.CreateNew, FileAccess.Write), ZipArchiveMode.Create);
        var included = new List<string>();
        foreach (var (file, name) in files)
        {
            try
            {
                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                bool truncated = source.Length > MaxFileBytes;
                if (truncated) source.Seek(-MaxFileBytes, SeekOrigin.End);
                using var destination = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                source.CopyTo(destination);
                included.Add(name + (truncated ? " (last 2 MiB)" : ""));
            }
            catch (IOException) { /* A log may rotate while the package is being prepared. */ }
            catch (UnauthorizedAccessException) { /* Keep the other available files. */ }
        }
        if (included.Count == 0) throw new IOException("The diagnostic files could not be read.");
        using (var writer = new StreamWriter(archive.CreateEntry("README.txt").Open(), Encoding.UTF8))
        {
            writer.WriteLine("FluentFlyout diagnostic package");
            writer.WriteLine("Review the files before sharing. Logs can contain local paths and error details.");
            writer.WriteLine("Settings, models, audio and transcription files are excluded.");
            writer.WriteLine("Included files:");
            foreach (string name in included) writer.WriteLine(name);
        }
        return path;
    }

    private static IEnumerable<FileInfo> Recent(string directory, string pattern, int count) =>
        Directory.Exists(directory)
            ? new DirectoryInfo(directory).GetFiles(pattern).OrderByDescending(file => file.LastWriteTimeUtc).Take(count)
            : [];
}
