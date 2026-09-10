using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentFlyout.App.Pages;

public sealed partial class NextUpPage : Page
{
    private bool initialized;

    public NextUpPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        NextUpToggle.IsOn = settings.NextUpEnabled;
        DurationSlider.Value = settings.NextUpDuration;
        initialized = true;
    }

    private async void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        var settings = App.Current.Runtime.Settings.Current;
        settings.NextUpEnabled = NextUpToggle.IsOn;
        await App.Current.Runtime.Settings.SaveAsync();
    }

    private async void SliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!initialized) return;
        var settings = App.Current.Runtime.Settings.Current;
        settings.NextUpDuration = (int)DurationSlider.Value;
        await App.Current.Runtime.Settings.SaveAsync();
    }
}
