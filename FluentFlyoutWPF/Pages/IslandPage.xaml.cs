// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using FluentFlyoutWPF.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Pages;

public partial class IslandPage : Page
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

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

    // --- orden de las funcionalidades del Island ---

    private void IslandFeatureMoveUp_Click(object sender, RoutedEventArgs e) => MoveIslandFeature(sender, -1);

    private void IslandFeatureMoveDown_Click(object sender, RoutedEventArgs e) => MoveIslandFeature(sender, +1);

    /// <summary>
    /// Sube o baja una funcionalidad en la lista del Island. El elemento de la lista ES
    /// el identificador (ver <see cref="IslandFeatureIds"/>), así que el botón ya sabe
    /// qué mueve: el ajuste se guarda solo y el contenedor reordena la navegación en el
    /// acto (UserSettings.MoveIslandFeature).
    /// </summary>
    private static void MoveIslandFeature(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is not string id) return;
        SettingsManager.Current.MoveIslandFeature(id, delta);
    }

    // --- estante de archivos del Island ---

    /// <summary>
    /// Aparca archivos elegidos a mano: es el mismo gesto que soltarlos sobre el Island
    /// (se MUEVEN a la carpeta del estante), para quien prefiera un diálogo al arrastre.
    /// </summary>
    private void IslandShelfAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Aparcar archivos en el estante del Island",
            Filter = "Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        SettingsManager.Current.AddIslandShelfPaths(dialog.FileNames);
    }

    /// <summary>Abre en el Explorador la carpeta propia del estante.</summary>
    private void IslandShelfOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(UserSettings.IslandShelfFolder);
            Process.Start(new ProcessStartInfo(UserSettings.IslandShelfFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Estante: no se pudo abrir la carpeta {Folder}", UserSettings.IslandShelfFolder);
            SettingsManager.Current.IslandShelfError = "No se pudo abrir la carpeta del estante.";
        }
    }

    private void IslandShelfItemRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is IslandShelfItem item)
            SettingsManager.Current.RemoveIslandShelfItem(item);
    }

    /// <summary>
    /// Vacía el estante devolviendo cada elemento a su carpeta original: vaciar no borra
    /// nada. Los que no puedan volver (su carpeta original ya no existe) se quedan y se
    /// explica el motivo.
    /// </summary>
    private void IslandShelfClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SettingsManager.Current.IslandShelfItems.ToList())
            SettingsManager.Current.RemoveIslandShelfItem(item);
    }
}
