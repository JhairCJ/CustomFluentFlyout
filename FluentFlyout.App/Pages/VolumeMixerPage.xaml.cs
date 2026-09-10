using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentFlyout.App.Pages;

public sealed partial class VolumeMixerPage : Page
{
    private bool initialized;

    public VolumeMixerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.Runtime.Settings.Current;
        VolumeFlyoutToggle.IsOn = settings.VolumeControlEnabled;
        VolumeMixerToggle.IsOn = settings.VolumeMixerEnabled;
        DurationSlider.Value = settings.VolumeControlDuration;
        initialized = true;
    }

    private async void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        var settings = App.Current.Runtime.Settings.Current;
        settings.VolumeControlEnabled = VolumeFlyoutToggle.IsOn;
        settings.VolumeMixerEnabled = VolumeMixerToggle.IsOn;
        await App.Current.Runtime.Settings.SaveAsync();
    }

    private async void SliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!initialized) return;
        var settings = App.Current.Runtime.Settings.Current;
        settings.VolumeControlDuration = (int)DurationSlider.Value;
        await App.Current.Runtime.Settings.SaveAsync();
    }
}
