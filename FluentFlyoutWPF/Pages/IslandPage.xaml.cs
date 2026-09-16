// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class IslandPage : Page
{
    public IslandPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
    }

    /// <summary>
    /// Commits the font choice the moment an item is picked from the dropdown.
    /// The source is set directly from the selected item because in an editable
    /// ComboBox <see cref="ComboBox.Text"/> still holds the previous value when
    /// <see cref="ComboBox.SelectionChanged"/> fires (it syncs afterwards), so
    /// pushing the binding there would commit the stale font and the change
    /// would only land later on focus loss. Typed-in names still commit on
    /// focus loss via the LostFocus trigger.
    /// </summary>
    private void IslandFontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0
            && e.AddedItems[0] is ComboBoxItem item
            && item.Content is string name
            && !string.IsNullOrWhiteSpace(name))
        {
            SettingsManager.Current.IslandFontFamily = name;
        }
    }

    private void TimerPresetAdd_Click(object sender, RoutedEventArgs e) => SettingsManager.Current.AddTimerPreset();

    private void TimerPresetDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is TimerPreset preset)
            SettingsManager.Current.RemoveTimerPreset(preset);
    }

    private void IslandAppAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Añadir aplicación al Island",
            Filter = "Aplicaciones (*.exe)|*.exe|Accesos directos (*.lnk)|*.lnk|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
            SettingsManager.Current.AddIslandApp(dialog.FileName);
    }

    private void IslandAppDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is IslandApp app)
            SettingsManager.Current.RemoveIslandApp(app);
    }
}
