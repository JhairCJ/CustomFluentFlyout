using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentFlyout.App.Pages;

public sealed partial class LockKeysPage : Page
{
    private bool initialized;

    public LockKeysPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        LockKeysToggle.IsOn = settings.LockKeysEnabled;
        DurationSlider.Value = settings.LockKeysDuration;
        initialized = true;
    }

    private async void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        var settings = App.Current.Runtime.Settings.Current;
        settings.LockKeysEnabled = LockKeysToggle.IsOn;
        await App.Current.Runtime.Settings.SaveAsync();
    }

    private async void SliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!initialized) return;
        var settings = App.Current.Runtime.Settings.Current;
        settings.LockKeysDuration = (int)DurationSlider.Value;
        await App.Current.Runtime.Settings.SaveAsync();
    }
}
