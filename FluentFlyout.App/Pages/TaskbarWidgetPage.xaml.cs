using FluentFlyout.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFlyout.App.Pages;

public sealed partial class TaskbarWidgetPage : Page
{
    private bool loading = true;

    public TaskbarWidgetPage()
    {
        InitializeComponent();
        Load();
    }

    private UserSettings Settings => App.Current.Runtime.Settings.Current;

    private void Load()
    {
        loading = true;
        EnableToggle.IsOn = Settings.TaskbarWidgetEnabled;
        AlbumArtToggle.IsOn = Settings.TaskbarWidgetShowAlbumArt;
        ControlsToggle.IsOn = Settings.TaskbarWidgetControlsEnabled;
        BackgroundBlurToggle.IsOn = Settings.TaskbarWidgetBackgroundBlur;
        RotateToggle.IsOn = Settings.TaskbarWidgetBackgroundRotate;
        RotateDirectionCombo.SelectedIndex = Math.Clamp(Settings.TaskbarWidgetBackgroundRotateDirection, 0, 1);
        RotateDurationSlider.Value = Settings.TaskbarWidgetBackgroundRotateDuration;
        RotateSizeSlider.Value = Settings.TaskbarWidgetBackgroundRotateSize;
        BlurIntensitySlider.Value = Settings.TaskbarWidgetBackgroundBlurIntensity;
        PositionCombo.SelectedIndex = Math.Clamp(Settings.TaskbarWidgetPosition, 0, 2);
        AutoHideToggle.IsOn = Settings.TaskbarWidgetAutoHide;
        loading = false;
    }

    private async void Save()
    {
        if (loading)
            return;

        Settings.TaskbarWidgetEnabled = EnableToggle.IsOn;
        Settings.TaskbarWidgetShowAlbumArt = AlbumArtToggle.IsOn;
        Settings.TaskbarWidgetControlsEnabled = ControlsToggle.IsOn;
        Settings.TaskbarWidgetBackgroundBlur = BackgroundBlurToggle.IsOn;
        Settings.TaskbarWidgetBackgroundRotate = RotateToggle.IsOn;
        Settings.TaskbarWidgetBackgroundRotateDirection = RotateDirectionCombo.SelectedIndex is 0 or 1 ? RotateDirectionCombo.SelectedIndex : 0;
        Settings.TaskbarWidgetBackgroundRotateDuration = (int)RotateDurationSlider.Value;
        Settings.TaskbarWidgetBackgroundRotateSize = (int)RotateSizeSlider.Value;
        Settings.TaskbarWidgetBackgroundBlurIntensity = (int)BlurIntensitySlider.Value;
        Settings.TaskbarWidgetPosition = PositionCombo.SelectedIndex is >= 0 and <= 2 ? PositionCombo.SelectedIndex : 1;
        Settings.TaskbarWidgetAutoHide = AutoHideToggle.IsOn;

        await App.Current.Runtime.Settings.SaveAsync();
        App.Current.WindowManager.RecreateTaskbarHost();
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Save();
    private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => Save();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Save();
}
