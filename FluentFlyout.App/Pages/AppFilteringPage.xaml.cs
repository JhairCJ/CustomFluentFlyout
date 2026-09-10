using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFlyout.App.Pages;

public sealed partial class AppFilteringPage : Page
{
    private readonly ObservableCollection<string> currentApps = [];
    private bool initialized;

    public AppFilteringPage()
    {
        InitializeComponent();
        AppsListView.ItemsSource = currentApps;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        FilteringToggle.IsOn = settings.AppFilteringEnabled;
        ModeComboBox.SelectedIndex = Math.Clamp(settings.AppFilteringMode, 0, 1);
        RefreshAppsList();
        initialized = true;
    }

    private void RefreshAppsList()
    {
        currentApps.Clear();
        var settings = App.Current.Runtime.Settings.Current;
        var list = settings.AppFilteringMode == 1 ? settings.AllowedApps : settings.BlockedApps;
        foreach (var app in list)
        {
            currentApps.Add(app);
        }
    }

    private async void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        var settings = App.Current.Runtime.Settings.Current;
        settings.AppFilteringEnabled = FilteringToggle.IsOn;
        await App.Current.Runtime.Settings.SaveAsync();
    }

    private async void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        var settings = App.Current.Runtime.Settings.Current;
        settings.AppFilteringMode = ModeComboBox.SelectedIndex;
        RefreshAppsList();
        await App.Current.Runtime.Settings.SaveAsync();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var appName = NewAppTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(appName)) return;

        var settings = App.Current.Runtime.Settings.Current;
        var list = settings.AppFilteringMode == 1 ? settings.AllowedApps : settings.BlockedApps;
        if (!list.Contains(appName, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(appName);
            currentApps.Add(appName);
            NewAppTextBox.Text = string.Empty;
            await App.Current.Runtime.Settings.SaveAsync();
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string appName })
        {
            var settings = App.Current.Runtime.Settings.Current;
            var list = settings.AppFilteringMode == 1 ? settings.AllowedApps : settings.BlockedApps;
            list.Remove(appName);
            currentApps.Remove(appName);
            await App.Current.Runtime.Settings.SaveAsync();
        }
    }
}
