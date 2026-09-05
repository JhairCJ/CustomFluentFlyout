// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.IO;
using Windows.Storage;

namespace FluentFlyoutWPF.Classes.Utils
{
    internal class FileSystemHelper
    {
        public static string GetLogsPath()
        {
            string path;

            // check %appData%\FluentFlyout first
            try
            {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FluentFlyout");
                if (Directory.Exists(path))
                    return path;

            }
            catch { }

            // if that doesn't exist, check the packaged app cache location
            try
            {
                path = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path,
                    "Roaming",
                    "FluentFlyout");
                if (Directory.Exists(path))
                    return path;
            }
            catch { }

            // if neither of those exist, return hardcoded path
            // %localAppData%\Packages\unchihugo.FluentFlyout_69b7b6qge1ahj\LocalCache\Roaming\FluentFlyout
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                "unchihugo.FluentFlyout_69b7b6qge1ahj",
                "LocalCache",
                "Roaming",
                "FluentFlyout"
            );
        }
    }
}