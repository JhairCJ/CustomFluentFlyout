// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

namespace FluentFlyoutWPF.Classes.Dictation;

internal static class NemoSpeechRuntime
{
    private static string ProgramsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
    public static string RootFolder => Path.Combine(ProgramsFolder, "NeMoSpeech");
    public static bool IsInstalled => File.Exists(Path.Combine(RootFolder, "bin", "nemo-speech.exe"))
        && File.Exists(Path.Combine(RootFolder, ".nemo-speech-install"));

    public static async Task UninstallAsync()
    {
        await Task.Run(() => DeleteInstallation(RootFolder, ProgramsFolder));
        string bin = Path.Combine(RootFolder, "bin");
        string? userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        if (userPath != null)
        {
            string cleaned = RemovePathEntry(userPath, bin);
            if (cleaned != userPath) Environment.SetEnvironmentVariable("Path", cleaned, EnvironmentVariableTarget.User);
        }
        string? processPath = Environment.GetEnvironmentVariable("Path");
        if (processPath != null) Environment.SetEnvironmentVariable("Path", RemovePathEntry(processPath, bin));
    }

    internal static void DeleteInstallation(string folder, string programsFolder)
    {
        string target = Path.GetFullPath(folder);
        string expected = Path.GetFullPath(Path.Combine(programsFolder, "NeMoSpeech"));
        if (!string.Equals(target, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("NeMo can only be removed from its standard installation folder");
        if (!Directory.Exists(target)) return;
        if (!File.Exists(Path.Combine(target, ".nemo-speech-install"))
            || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The folder is not a managed NeMo installation");
        Directory.Delete(target, recursive: true);
    }

    internal static string RemovePathEntry(string path, string entry) => string.Join(';', path.Split(';')
        .Where(part => !string.Equals(part.Trim().Trim('"').TrimEnd('\\', '/'),
            entry.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)));
}
