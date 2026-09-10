using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFlyout.App.Pages;

public sealed partial class AdvancedPage : Page
{
    private bool initialized;

    public AdvancedPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        FullscreenToggle.IsOn = settings.DisableIfFullscreen;
        initialized = true;
    }

    private async void FullscreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        var settings = App.Current.Runtime.Settings.Current;
        settings.DisableIfFullscreen = FullscreenToggle.IsOn;
        await App.Current.Runtime.Settings.SaveAsync();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = System.IO.Path.Combine(folder, "fluentflyout-settings-backup.xml");
            await App.Current.Runtime.Settings.ExportAsync(path);

            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "Exported successfully";
            StatusInfoBar.Message = $"Settings exported to {path}";
            StatusInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Export failed";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = System.IO.Path.Combine(folder, "fluentflyout-settings-backup.xml");
            if (System.IO.File.Exists(path))
            {
                await App.Current.Runtime.Settings.ImportAsync(path);
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "Imported successfully";
                StatusInfoBar.Message = "Settings loaded. Restarting components...";
                StatusInfoBar.IsOpen = true;
            }
            else
            {
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = "File not found";
                StatusInfoBar.Message = $"Could not locate {path}";
                StatusInfoBar.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Import failed";
            StatusInfoBar.Message = ex.Message;
            StatusInfoBar.IsOpen = true;
        }
    }
}
