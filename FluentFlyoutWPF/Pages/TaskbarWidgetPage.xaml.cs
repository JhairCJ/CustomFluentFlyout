// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Utils;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class TaskbarWidgetPage : Page
{
    public TaskbarWidgetPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;
        UpdateMonitorList();
    }

    private void UpdateMonitorList()
    {
        MonitorUtil.UpdateMonitorList(
            TaskbarWidgetSelectedMonitorComboBox,
            () => SettingsManager.Current.TaskbarWidgetSelectedMonitor,
            value => SettingsManager.Current.TaskbarWidgetSelectedMonitor = value);
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
    private void TaskbarWidgetFontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0
            && e.AddedItems[0] is ComboBoxItem item
            && item.Content is string name
            && !string.IsNullOrWhiteSpace(name))
        {
            SettingsManager.Current.TaskbarWidgetFontFamily = name;
        }
    }
}