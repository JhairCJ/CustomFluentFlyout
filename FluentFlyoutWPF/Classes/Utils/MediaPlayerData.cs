// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace FluentFlyout.Classes.Utils;

public static partial class MediaPlayerData
{
    private class CachedMediaPlayerInfo
    {
        public required string Title { get; set; }
        public ImageSource? Icon { get; set; }
        public int ProcessId { get; set; }
    }
    // cache for media player info to avoid redundant process lookups
    private static readonly ConcurrentDictionary<string, CachedMediaPlayerInfo> mediaPlayerCache = [];

    // id variants of media players where the key is the mediaPlayerId and the value is the mediaPlayerCache key
    private static readonly ConcurrentDictionary<string, string> mediaPlayerIdVariants = [];

    private sealed record ProcessSnapshot(string Name, string Path, string Title, int ProcessId, bool HasWindow);
    private static ProcessSnapshot[]? cachedProcesses;
    private static readonly object processCacheGate = new();
    private static DateTime lastCacheTime = DateTime.MinValue;
    private const int CACHE_DURATION_SECONDS = 5;

    public static (string, ImageSource?) GetAndCacheMediaPlayerData(string mediaPlayerId)
    {
        if (mediaPlayerCache.TryGetValue(mediaPlayerId, out var cachedInfo)
            || mediaPlayerIdVariants.TryGetValue(mediaPlayerId, out var variantKey)
            && mediaPlayerCache.TryGetValue(variantKey, out cachedInfo))
        {
            return (cachedInfo.Title, cachedInfo.Icon);
        }

        string mediaTitle = mediaPlayerId;
        ImageSource? mediaIcon = null;

        // get sanitized media title name
        string[] mediaSessionIdVariants = mediaPlayerId.Split('.');

        // remove common non-informative substrings
        var variants = mediaSessionIdVariants.Select(variant =>
            variant.Replace("com", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("github", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("exe", "", StringComparison.OrdinalIgnoreCase)
                   .Trim()
        ).Where(variant => !string.IsNullOrWhiteSpace(variant)).ToList();

        // add original id to the end of the array to ensure at least one variant
        variants.Add(mediaPlayerId);
        if (variants.Any(v => v.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase) || v.EndsWith("MSEdge", StringComparison.OrdinalIgnoreCase)))
            variants.Add("msedge");

        // Cache immutable metadata, not Process objects whose handles can be disposed
        // by another reader. Exact process names win over a path substring match.
        var processData = GetProcessSnapshots()
            .Where(p => (p.HasWindow || variants.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                && (variants.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                    || variants.Any(v => p.Path.Contains(v, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(p => variants.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (processData == null)
        {
            var (shellTitle, shellIcon) = ResolveViaAppsFolder(mediaPlayerId);
            if (shellTitle == null) return (mediaTitle, mediaIcon);

            mediaPlayerCache[mediaPlayerId] = new CachedMediaPlayerInfo
            {
                Title = shellTitle,
                Icon = shellIcon,
                ProcessId = -1 // no process match, -1 avoids colliding with real PIDs
            };

            return (shellTitle, shellIcon);
        }

        mediaTitle = !string.IsNullOrWhiteSpace(processData.Title) ? processData.Title : mediaPlayerId;

        // check cache again because we have the sanitized title
        if (mediaPlayerCache.TryGetValue(mediaTitle, out cachedInfo) && cachedInfo.ProcessId == processData.ProcessId)
        {
            // map the original id to the sanitized title for future lookups
            mediaPlayerIdVariants[mediaPlayerId] = mediaTitle;
            return (cachedInfo.Title, cachedInfo.Icon);
        }

        mediaIcon = GetIconFromPath(processData.Path);

        mediaPlayerCache[mediaPlayerId] = new CachedMediaPlayerInfo
        {
            Title = mediaTitle,
            Icon = mediaIcon,
            ProcessId = processData.ProcessId
        };

        return (mediaTitle, mediaIcon);
    }

    private static ProcessSnapshot[] GetProcessSnapshots(bool refresh = false)
    {
        lock (processCacheGate)
        {
            if (!refresh && cachedProcesses != null && (DateTime.UtcNow - lastCacheTime).TotalSeconds < CACHE_DURATION_SECONDS)
                return cachedProcesses;
            var snapshots = new List<ProcessSnapshot>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        bool hasWindow = process.MainWindowHandle != IntPtr.Zero;
                        if (!hasWindow) continue;
                        var module = process.MainModule;
                        if (module == null) continue;
                        string title = module.FileVersionInfo.FileDescription ?? process.MainWindowTitle;
                        snapshots.Add(new(process.ProcessName, module.FileName, title, process.Id, hasWindow));
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            cachedProcesses = snapshots.ToArray();
            lastCacheTime = DateTime.UtcNow;
            return cachedProcesses;
        }
    }

    /// <summary>
    /// Extracts the associated icon for a given process ID. Returns null if the process is inaccessible.
    /// </summary>
    public static ImageSource? GetAndCacheProcessIcon(int processId, string title)
    {
        try
        {
            if (title == "System sounds") return null;

            // search in cache
            foreach (var item in mediaPlayerCache.Values)
            {
                if (item.ProcessId == processId)
                {
                    return item.Icon;
                }
            }

            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            if (path == null) return null;

            // store in cache for future lookups
            var icon = GetIconFromPath(path);
            if (icon != null)
            {
                mediaPlayerCache[title] = new CachedMediaPlayerInfo
                {
                    Title = title,
                    Icon = icon,
                    ProcessId = processId
                };
            }

            return icon;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Icono asociado a un ejecutable. Se expone para reutilizarlo desde el cajón
    /// de aplicaciones del Island (misma extracción que usa el flyout de medios).
    /// </summary>
    public static ImageSource? GetExecutableIcon(string exePath) => GetIconFromPath(exePath);

    private static ImageSource? GetIconFromPath(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();

            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves an AppUserModelId through the shell Apps folder ("shell:AppsFolder"),
    /// the same way the native media flyout does. Handles apps whose session id
    /// doesn't match their process path, e.g. Firefox-based browsers (#949).
    /// </summary>
    private static (string? Title, ImageSource? Icon) ResolveViaAppsFolder(string appUserModelId)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return (null, null);

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic? item = shell.NameSpace("shell:AppsFolder")?.ParseName(appUserModelId);
            if (item == null) return (null, null);

            string name = item.Name;
            if (string.IsNullOrWhiteSpace(name)) return (null, null);

            // desktop apps expose their start menu shortcut target, so the icon can
            // be extracted the same way as everywhere else; packaged apps don't
            // have one and keep a null icon
            var targetPath = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
            ImageSource? icon = targetPath != null ? GetIconFromPath(targetPath) : null;

            return (name, icon);
        }
        catch
        {
            // id is not registered in the apps folder, nothing we can do
            return (null, null);
        }
    }
}