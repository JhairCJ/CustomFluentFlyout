using FluentFlyout.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFlyout.App.Pages;

public sealed partial class TaskbarVisualizerPage : Page
{
    private bool loading = true;

    public TaskbarVisualizerPage()
    {
        InitializeComponent();
        Load();
    }

    private UserSettings Settings => App.Current.Runtime.Settings.Current;

    private void Load()
    {
        loading = true;
        EnableToggle.IsOn = Settings.TaskbarVisualizerEnabled;
        HighRefreshToggle.IsOn = Settings.TaskbarVisualizerHighRefreshRate;
        ClickableToggle.IsOn = Settings.TaskbarVisualizerClickable;
        BarCountSlider.Value = Settings.TaskbarVisualizerBarCount;
        SensitivitySlider.Value = Settings.TaskbarVisualizerAudioSensitivity;
        PeakSlider.Value = Settings.TaskbarVisualizerAudioPeakLevel;
        SmoothingSlider.Value = Settings.TaskbarVisualizerSmoothing;
        CenteredToggle.IsOn = Settings.TaskbarVisualizerCenteredBars;
        BaselineToggle.IsOn = Settings.TaskbarVisualizerBaseline;
        BaselineAutoHideToggle.IsOn = Settings.TaskbarVisualizerBaselineAutoHide;
        loading = false;
    }

    private async void Save()
    {
        if (loading)
            return;

        Settings.TaskbarVisualizerEnabled = EnableToggle.IsOn;
        Settings.TaskbarVisualizerHighRefreshRate = HighRefreshToggle.IsOn;
        Settings.TaskbarVisualizerClickable = ClickableToggle.IsOn;
        Settings.TaskbarVisualizerBarCount = (int)BarCountSlider.Value;
        Settings.TaskbarVisualizerAudioSensitivity = (int)SensitivitySlider.Value;
        Settings.TaskbarVisualizerAudioPeakLevel = (int)PeakSlider.Value;
        Settings.TaskbarVisualizerSmoothing = (int)SmoothingSlider.Value;
        Settings.TaskbarVisualizerCenteredBars = CenteredToggle.IsOn;
        Settings.TaskbarVisualizerBaseline = BaselineToggle.IsOn;
        Settings.TaskbarVisualizerBaselineAutoHide = BaselineAutoHideToggle.IsOn;

        await App.Current.Runtime.Settings.SaveAsync();
        App.Current.WindowManager.RecreateTaskbarHost();
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Save();
    private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => Save();
}
