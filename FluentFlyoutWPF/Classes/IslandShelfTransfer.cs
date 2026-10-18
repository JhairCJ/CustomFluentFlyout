using System.IO;
using System.Text.Json;
using FluentFlyoutWPF.Models;

namespace FluentFlyoutWPF.Classes;

/// <summary>Durable intent before each native move; recovery never deletes user content.</summary>
internal static class IslandShelfTransfer
{
    internal sealed record Intent(string Source, string Target, string Origin, bool IsFolder, bool Returning);
    internal sealed record Result(IslandShelfItem? Item, string? Error);

    internal static Result Move(string source, string folder, string origin, bool returning, string shelfFolder)
    {
        try
        {
            source = Path.GetFullPath(source);
            if (!returning) origin = source;
            folder = Path.GetFullPath(folder);
            bool isFolder = Directory.Exists(source);
            if (!isFolder && !File.Exists(source)) return new(null, "IslandShelfSourceGone");
            if (isFolder && (folder + Path.DirectorySeparatorChar).StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return new(null, "IslandShelfInvalidDestination");
            if (returning && !Directory.Exists(folder)) return new(null, "IslandShelfOriginGone");
            Directory.CreateDirectory(folder);
            string name = Path.GetFileName(returning ? origin : source);
            if (name.Length == 0) return new(null, "IslandShelfInvalidDestination");
            string target = Path.Combine(folder, name);
            for (int index = 2; File.Exists(target) || Directory.Exists(target); index++)
                target = Path.Combine(folder, $"{(isFolder ? name : Path.GetFileNameWithoutExtension(name))} ({index}){(isFolder ? "" : Path.GetExtension(name))}");
            string journal = Path.Combine(shelfFolder, ".operations");
            Directory.CreateDirectory(journal);
            string intentPath = Path.Combine(journal, Guid.NewGuid().ToString("N") + ".json");
            using (var stream = new FileStream(intentPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new Intent(source, target, origin, isFolder, returning));
                stream.Flush(true);
            }
            // Native moves do not overwrite. Unsupported cross-volume directory moves fail intact.
            try
            {
                if (isFolder) Directory.Move(source, target);
                else File.Move(source, target);
            }
            catch
            {
                if ((File.Exists(source) || Directory.Exists(source)) && !File.Exists(target) && !Directory.Exists(target)) File.Delete(intentPath);
                throw;
            }
            return new(new IslandShelfItem { Path = target, Origin = origin, IsFolder = isFolder }, null);
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Island shelf native move failed");
            return new(null, returning ? "IslandShelfReturnFailed" : "IslandShelfParkFailed");
        }
    }

    internal static List<(string Journal, Intent Operation, bool SourceExists, bool TargetExists)> Pending(string folder)
    {
        var result = new List<(string, Intent, bool, bool)>();
        string journal = Path.Combine(folder, ".operations");
        if (!Directory.Exists(journal)) return result;
        foreach (string path in Directory.EnumerateFiles(journal, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var operation = JsonSerializer.Deserialize<Intent>(File.ReadAllText(path));
                if (operation == null) continue;
                // A journal can describe external originals, but the parked side must belong to this shelf.
                string parked = Path.GetFullPath(operation.Returning ? operation.Source : operation.Target);
                string prefix = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!parked.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add((path, operation, File.Exists(operation.Source) || Directory.Exists(operation.Source),
                    File.Exists(operation.Target) || Directory.Exists(operation.Target)));
            }
            catch (Exception ex) { NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Island shelf recovery journal unreadable"); }
        }
        return result;
    }

    internal static void RetirePersistedJournals(string folder, IslandShelfItem[] parkedItems)
    {
        if (!FluentFlyout.Classes.Settings.SettingsManager.ShelfStateIsPersisted(parkedItems)) return;
        var parked = parkedItems.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var group in Pending(folder).GroupBy(entry => entry.Operation.Returning ? entry.Operation.Source : entry.Operation.Target, StringComparer.OrdinalIgnoreCase))
        {
            var latest = group.First();
            if (latest.SourceExists == latest.TargetExists) continue; // ambiguous or missing: retain evidence
            bool exists = latest.Operation.Returning ? latest.SourceExists : latest.TargetExists;
            bool reflected = exists
                ? parked.TryGetValue(group.Key, out var item) && item.Origin == latest.Operation.Origin && item.IsFolder == latest.Operation.IsFolder
                : !parked.ContainsKey(group.Key);
            if (!reflected) continue;
            foreach (var entry in group)
            {
                try { File.Delete(entry.Journal); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
