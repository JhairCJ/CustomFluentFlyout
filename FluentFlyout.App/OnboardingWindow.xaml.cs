using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace FluentFlyout.App;

public sealed partial class OnboardingWindow : Window
{
    public OnboardingWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(680, 520));
        AppWindow.Title = "Welcome to FluentFlyout";

        var settings = App.Current.Runtime.Settings.Current;
        MediaFlyoutToggle.IsOn = settings.MediaFlyoutEnabled;
        TaskbarWidgetToggle.IsOn = settings.TaskbarWidgetEnabled;
        LockKeysToggle.IsOn = settings.LockKeysEnabled;
        StartupToggle.IsOn = settings.Startup;
    }

    private async void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        settings.MediaFlyoutEnabled = MediaFlyoutToggle.IsOn;
        settings.TaskbarWidgetEnabled = TaskbarWidgetToggle.IsOn;
        settings.LockKeysEnabled = LockKeysToggle.IsOn;
        settings.Startup = StartupToggle.IsOn;

        await App.Current.Runtime.Settings.SaveAsync();
        Close();
    }
}
