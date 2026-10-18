// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Controls.TaskbarWidget;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using MicaWPF.Core.Helpers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FluentFlyout.Controls;

/// <summary>
/// Interaction logic for TaskbarVisualizerControl.xaml
/// </summary>
public partial class TaskbarVisualizerControl : UserControl
{
    // reference to main window for flyout functions
    private static readonly Visualizer visualizer = new();

    private readonly SolidColorBrush _hoverBrush = new(Colors.Transparent);

    private static readonly SolidColorBrush HoverBorderDark = new(Color.FromArgb(93, 255, 255, 255)) { Opacity = 0.25 };
    private static readonly SolidColorBrush HoverBorderLight = new(Color.FromArgb(93, 255, 255, 255)) { Opacity = 1 };

    public TaskbarVisualizerControl()
    {
        InitializeComponent();

        // Set DataContext for bindings
        DataContext = SettingsManager.Current;

        if (SettingsManager.Current.TaskbarVisualizerEnabled)
        {
            visualizer.Start();
        }

        VisualizerContainer.Source = visualizer.Bitmap;

        MainBorder.Background = _hoverBrush;

        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    }

    public static void OnTaskbarVisualizerEnabledChanged(bool value)
    {
        if (visualizer == null)
            return;

        if (value)
        {
            visualizer.Start();
        }
        else
        {
            visualizer.Stop();
        }
    }

    public static void OnTaskbarVisualizerHighRefreshRateChanged()
    {
        visualizer?.RestartRenderLoop();
    }

    public static void ResizeBars(int newBarCount)
    {
        visualizer?.ResizeBarList(newBarCount);
    }

    internal void SetPresentationActive(bool active) => visualizer.SetPresentationActive(active);

    public static void DisposeVisualizer()
    {
        if (visualizer == null)
            return;

        visualizer.Dispose();
    }

    private void Grid_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!SettingsManager.Current.TaskbarVisualizerClickable || !SettingsManager.Current.TaskbarVisualizerHasContent) return;

        WindowsThemeDetector.GetWindowsTheme(out _, out var systemTheme);
        bool isDark = systemTheme == WindowsThemeDetector.ThemeMode.Dark;
        TopBorder.BorderBrush = isDark ? HoverBorderDark : HoverBorderLight;
        TaskbarWidgetAnimationEnvironment.AnimateHover(_hoverBrush,
            isDark ? Color.FromArgb(0x18, 255, 255, 255) : Color.FromArgb(0x99, 255, 255, 255));
    }

    private void Grid_MouseLeave(object sender, MouseEventArgs e)
    {
        TaskbarWidgetAnimationEnvironment.AnimateHover(_hoverBrush, Colors.Transparent);
        TopBorder.BorderBrush = Brushes.Transparent;
    }

    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // only continue when the visualizer is clickable and actually has content
        // otherwise it would show an empty container to click on which is weird
        if (!SettingsManager.Current.TaskbarVisualizerClickable || !SettingsManager.Current.TaskbarVisualizerHasContent) return;

        TaskbarWidgetAnimationEnvironment.Pulse(VisualizerContainer);
        // open settings when clicked
        SettingsWindow.ShowInstance("TaskbarVisualizerPage");
    }
}
