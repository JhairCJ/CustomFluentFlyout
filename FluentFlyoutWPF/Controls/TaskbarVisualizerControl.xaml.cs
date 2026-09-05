// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using MicaWPF.Core.Enums;
using MicaWPF.Core.Helpers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentFlyout.Controls;

/// <summary>
/// Interaction logic for TaskbarVisualizerControl.xaml
/// </summary>
public partial class TaskbarVisualizerControl : UserControl
{
    // reference to main window for flyout functions
    private static readonly Visualizer visualizer = new();

    // Hover animation objects, created once and reused: the previous code allocated two
    // animations + two brushes per MouseEnter/Leave. Only their To values change.
    private readonly ColorAnimation _hoverColorIn = new()
    {
        Duration = TimeSpan.FromMilliseconds(200),
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
    };
    private readonly DoubleAnimation _hoverOpacityIn = new()
    {
        Duration = TimeSpan.FromMilliseconds(200),
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
    };
    private readonly ColorAnimation _hoverColorOut = new()
    {
        To = Colors.Transparent,
        Duration = TimeSpan.FromMilliseconds(200),
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
    };
    private readonly DoubleAnimation _hoverOpacityOut = new()
    {
        To = 0,
        Duration = TimeSpan.FromMilliseconds(200),
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
    };

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

        // for hover animation
        if (MainBorder.Background is not SolidColorBrush)
        {
            MainBorder.Background = new SolidColorBrush(Colors.Transparent);
            MainBorder.Background.Opacity = 0;
        }

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

    public static void DisposeVisualizer()
    {
        if (visualizer == null)
            return;

        visualizer.Dispose();
    }

    // TODO: The following mouse events are almost the same as the ones in TaskbarWidgetControl.xaml.cs.
    // We should find a way to unify these methods instead of duplicating them.

    private void Grid_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!SettingsManager.Current.TaskbarVisualizerClickable || !SettingsManager.Current.TaskbarVisualizerHasContent) return;

        // hover effects with animations, hard-coded colors because I can't find the resource brushes
        WindowsThemeDetector.GetWindowsTheme(out _, out var systemTheme);
        bool isDark = systemTheme == WindowsThemeDetector.ThemeMode.Dark;
        if (isDark)
        { // dark mode
            _hoverColorIn.To = Color.FromArgb(197, 255, 255, 255);
            _hoverOpacityIn.To = 0.075;
            TopBorder.BorderBrush = HoverBorderDark;
        }
        else
        { // light mode
            _hoverColorIn.To = Color.FromArgb(255, 255, 255, 255);
            _hoverOpacityIn.To = 0.6;
            TopBorder.BorderBrush = HoverBorderLight;
        }

        // rare case where background is not a SolidColorBrush after SetupWindow
        if (MainBorder.Background is not SolidColorBrush)
        {
            MainBorder.Background = new SolidColorBrush(Colors.Transparent);
            MainBorder.Background.Opacity = 0;
        }

        MainBorder.Background.BeginAnimation(SolidColorBrush.ColorProperty, _hoverColorIn);
        MainBorder.Background.BeginAnimation(SolidColorBrush.OpacityProperty, _hoverOpacityIn);
    }

    private void Grid_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!SettingsManager.Current.TaskbarVisualizerClickable || !SettingsManager.Current.TaskbarVisualizerHasContent) return;

        MainBorder.Background?.BeginAnimation(SolidColorBrush.ColorProperty, _hoverColorOut);
        MainBorder.Background?.BeginAnimation(SolidColorBrush.OpacityProperty, _hoverOpacityOut);

        TopBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
    }

    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // only continue when the visualizer is clickable and actually has content
        // otherwise it would show an empty container to click on which is weird
        if (!SettingsManager.Current.TaskbarVisualizerClickable || !SettingsManager.Current.TaskbarVisualizerHasContent) return;

        // open settings when clicked
        SettingsWindow.ShowInstance("TaskbarVisualizerPage");
    }
}