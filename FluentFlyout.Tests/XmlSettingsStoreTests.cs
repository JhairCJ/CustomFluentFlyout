using FluentFlyout.Core;
using FluentFlyout.Platform.Windows;
using Xunit;

namespace FluentFlyout.Tests;

public sealed class XmlSettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_PreservesLegacySettingsPathAndValues()
    {
        var root = CreateTestDirectory();
        try
        {
            var store = new XmlSettingsStore(root);
            store.Current.MediaFlyoutEnabled = false;
            store.Current.TaskbarWidgetEnabled = true;
            store.Current.AppLanguage = "es";
            await store.SaveAsync();

            var loaded = new XmlSettingsStore(root);
            await loaded.LoadAsync();

            Assert.Equal(Path.Combine(root, "FluentFlyout", "settings.xml"), loaded.SettingsFilePath);
            Assert.False(loaded.Current.MediaFlyoutEnabled);
            Assert.True(loaded.Current.TaskbarWidgetEnabled);
            Assert.Equal("es", loaded.Current.AppLanguage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Load_FallsBackToBackupAfterPrimaryFileIsCorrupted()
    {
        var root = CreateTestDirectory();
        try
        {
            var store = new XmlSettingsStore(root);
            store.Current.AppLanguage = "en-US";
            await store.SaveAsync();
            store.Current.AppLanguage = "es";
            await store.SaveAsync();

            await File.WriteAllTextAsync(store.SettingsFilePath, "<not-settings>");

            var recovered = new XmlSettingsStore(root);
            await recovered.LoadAsync();

            Assert.Equal("en-US", recovered.Current.AppLanguage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluentFlyoutTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
