// Only settings, theme detection and logging are replaced. Color extraction and
// the independent widget/global accent state run from their production sources.
namespace FluentFlyout.Classes.Settings
{
    internal static class SettingsManager
    {
        public static TestSettings Current { get; } = new();
    }

    internal sealed class TestSettings
    {
        public bool UseAlbumArtAsAccentColor { get; set; } = true;
        public uint AlbumAccentDesaturationThreshold { get; set; } = 65;
        public uint AlbumAccentDesaturationAmount { get; set; }
    }
}

namespace Wpf.Ui.Appearance
{
    internal enum ApplicationTheme { Light, Dark }
    internal enum SystemTheme { Light, Dark }
    internal static class ApplicationThemeManager
    {
        public static ApplicationTheme Theme { get; set; } = ApplicationTheme.Dark;
        public static ApplicationTheme GetAppTheme() => Theme;
        public static SystemTheme GetSystemTheme() => SystemTheme.Dark;
    }
}

namespace NLog
{
    internal sealed class Logger
    {
        public void Error(Exception exception, string message) { }
        public void Debug(Exception exception, string message) { }
    }

    internal static class LogManager
    {
        public static Logger GetCurrentClassLogger() => new();
    }
}
