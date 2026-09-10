using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFlyout.App;

public sealed partial class SettingsWindow : Window
{
    private readonly Dictionary<string, Type> _pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Home"] = typeof(Pages.HomePage),
        ["Media"] = typeof(Pages.MediaFlyoutPage),
        ["Volume"] = typeof(Pages.VolumeMixerPage),
        ["Widget"] = typeof(Pages.TaskbarWidgetPage),
        ["Visualizer"] = typeof(Pages.TaskbarVisualizerPage),
        ["NextUp"] = typeof(Pages.NextUpPage),
        ["LockKeys"] = typeof(Pages.LockKeysPage),
        ["System"] = typeof(Pages.SystemPage),
        ["Filtering"] = typeof(Pages.AppFilteringPage),
        ["Advanced"] = typeof(Pages.AdvancedPage),
        ["About"] = typeof(Pages.AboutPage)
    };

    private readonly List<(string Title, string PageTag)> searchCatalog =
    [
        ("Home", "Home"),
        ("Media Flyout", "Media"),
        ("Album Artwork", "Media"),
        ("Flyout Position", "Media"),
        ("Volume Mixer", "Volume"),
        ("Master Volume", "Volume"),
        ("Taskbar Widget", "Widget"),
        ("Widget Controls", "Widget"),
        ("Audio Visualizer", "Visualizer"),
        ("Spectrum Bars", "Visualizer"),
        ("Next Up Banner", "NextUp"),
        ("Lock Keys", "LockKeys"),
        ("Caps Lock", "LockKeys"),
        ("System Startup", "System"),
        ("App Language", "System"),
        ("App Filtering", "Filtering"),
        ("Blocklist", "Filtering"),
        ("Allowlist", "Filtering"),
        ("Advanced Settings", "Advanced"),
        ("Fullscreen Detection", "Advanced"),
        ("Export / Import Settings", "Advanced"),
        ("About FluentFlyout", "About"),
        ("Version & GitHub", "About")
    ];

    public SettingsWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ContentFrame.Navigate(typeof(Pages.HomePage));
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string key && _pages.TryGetValue(key, out var page))
            ContentFrame.Navigate(page);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                sender.ItemsSource = null;
            }
            else
            {
                var matches = searchCatalog
                    .Where(s => s.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Title)
                    .ToList();
                sender.ItemsSource = matches;
            }
        }
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string chosenTitle)
        {
            var entry = searchCatalog.FirstOrDefault(s => string.Equals(s.Title, chosenTitle, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(entry.PageTag))
            {
                ShowPage(entry.PageTag);
            }
        }
    }

    public void ShowPage(string? page)
    {
        if (string.IsNullOrWhiteSpace(page) || !_pages.TryGetValue(page, out var pageType))
            return;

        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag as string, page, StringComparison.OrdinalIgnoreCase))
            {
                Navigation.SelectedItem = item;
                ContentFrame.Navigate(pageType);
                return;
            }
        }
    }
}
