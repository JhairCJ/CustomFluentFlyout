// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
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
        RefreshScreensEditor();
    }

    // ------------------------------------------------------------------
    // Pantallas del Island (change island-pantallas)
    // ------------------------------------------------------------------

    /// <summary>
    /// Editor de pantallas: una fila por pantalla con una casilla por funcionalidad,
    /// más subir/bajar/quitar. Se construye en código (las casillas son dinámicas:
    /// una por funcionalidad conocida) y se reconstruye entero tras cada cambio, así
    /// el editor siempre enseña lo que hay guardado.
    /// </summary>
    private void RefreshScreensEditor()
    {
        var settings = SettingsManager.Current;
        IslandScreensPanel.Children.Clear();
        for (int i = 0; i < settings.IslandScreens.Count; i++)
        {
            string screen = settings.IslandScreens[i];
            var ids = IslandFeatureIds.ParseScreen(screen);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = $"Pantalla {i + 1}: {string.Join(", ", ids.Select(IslandFeatureIds.DisplayName))}",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(title, 0);
            header.Children.Add(title);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(ScreenButton("Subir", $"Subir la pantalla {i + 1}", () => MoveScreen(screen, -1)));
            actions.Children.Add(ScreenButton("Bajar", $"Bajar la pantalla {i + 1}", () => MoveScreen(screen, +1)));
            actions.Children.Add(ScreenButton("Quitar", "Quitar esta pantalla (la última no se puede quitar)", () =>
            {
                settings.RemoveIslandScreen(screen);
                RefreshScreensEditor();
            }));
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);
            row.Children.Add(header);

            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var id in IslandFeatureIds.All)
            {
                string featureId = id;
                chips.Children.Add(new CheckBox
                {
                    Content = IslandFeatureIds.DisplayName(id),
                    IsChecked = ids.Contains(id),
                    Margin = new Thickness(0, 0, 14, 4),
                    VerticalContentAlignment = VerticalAlignment.Center,
                });
                if (chips.Children[^1] is CheckBox box)
                {
                    box.Checked += (_, _) => ToggleScreenFeature(screen, featureId, true);
                    box.Unchecked += (_, _) => ToggleScreenFeature(screen, featureId, false);
                }
            }
            row.Children.Add(chips);
            IslandScreensPanel.Children.Add(row);
        }
    }

    private static Button ScreenButton(string text, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = tooltip,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void MoveScreen(string screen, int delta)
    {
        SettingsManager.Current.MoveIslandScreen(screen, delta);
        RefreshScreensEditor();
    }

    private void IslandScreenNew_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.AddIslandScreen();
        RefreshScreensEditor();
    }

    /// <summary>
    /// Mete o saca una funcionalidad de una pantalla. Quitar la última dejaría una
    /// pantalla vacía (no habría nada que enseñar): en ese caso no se aplica y el
    /// editor vuelve a pintar la casilla marcada.
    /// </summary>
    private void ToggleScreenFeature(string screen, string featureId, bool included)
    {
        var settings = SettingsManager.Current;
        int index = settings.IslandScreens.IndexOf(screen);
        if (index < 0) return;
        var ids = IslandFeatureIds.ParseScreen(screen).ToList();
        if (included)
        {
            if (!ids.Contains(featureId)) ids.Add(featureId);
        }
        else
        {
            ids.Remove(featureId);
        }
        string updated = IslandFeatureIds.FormatScreen(ids);
        if (updated.Length > 0) settings.IslandScreens[index] = updated;
        RefreshScreensEditor();
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

    // --- recordatorios de Google Calendar ---

    /// <summary>
    /// Inicia sesión con Google desde ajustes: abre el navegador en la pantalla de
    /// permiso y captura la respuesta en un bucle local; después guarda los tokens y
    /// deja el calendario leyéndose solo. Mientras dura, el botón se apaga para que no
    /// se abran dos autorizaciones a la vez.
    /// </summary>
    private async void GoogleCalendarSignIn_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsManager.Current;
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        try
        {
            string clientId = settings.GoogleCalendarClientId.Trim();
            if (clientId.Length == 0)
            {
                settings.GoogleCalendarError = "Pega el id del cliente OAuth de tu proyecto de Google.";
                return;
            }
            settings.GoogleCalendarError = "";
            settings.GoogleCalendarStatus = "Esperando a que autorices en el navegador…";
            var tokens = await GoogleCalendarClient.AuthorizeAsync(clientId, settings.GoogleCalendarClientSecret.Trim());
            GoogleCalendarClient.Save(settings, tokens);
            settings.GoogleCalendarAccount = await GoogleCalendarClient.FetchAccountAsync(tokens.AccessToken);
            settings.GoogleCalendarStatus = "Sesión iniciada.";
            // Si alguien se conecta es porque quiere los recordatorios: se enciende la
            // funcionalidad en el mismo gesto (el interruptor sigue estando ahí).
            settings.IslandCalendarEnabled = true;
            SettingsManager.SaveSettings();
            RefreshIslandCalendar();
            await GoogleCalendarService.Instance.RefreshNowAsync();
        }
        catch (OperationCanceledException)
        {
            // El usuario cerró la pestaña o dejó pasar los cinco minutos.
            settings.GoogleCalendarStatus = "";
            settings.GoogleCalendarError = "No se completó la autorización: vuelve a intentarlo.";
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Calendario: inicio de sesión fallido");
            settings.GoogleCalendarStatus = "";
            settings.GoogleCalendarError = ex.Message;
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Cierra la sesión: se van los tokens y con ellos la lectura del calendario. El id
    /// y el secreto del cliente se conservan para no volver a pegarlos.
    /// </summary>
    private void GoogleCalendarSignOut_Click(object sender, RoutedEventArgs e)
    {
        SettingsManager.Current.SignOutGoogleCalendar();
        RefreshIslandCalendar();
    }

    /// <summary>Sincroniza ahora mismo, sin esperar al siguiente ciclo del servicio.</summary>
    private async void GoogleCalendarRefresh_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsManager.Current;
        if (!settings.GoogleCalendarSignedIn)
        {
            settings.GoogleCalendarError = "Inicia sesión con Google para leer el calendario.";
            return;
        }
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        settings.GoogleCalendarStatus = "Sincronizando…";
        try
        {
            await GoogleCalendarService.Instance.RefreshNowAsync();
            settings.GoogleCalendarStatus = settings.GoogleCalendarError.Length == 0 ? "Calendario al día." : "";
        }
        finally
        {
            if (button != null) button.IsEnabled = true;
            // La vista del Island se reengancha al estado nuevo (sesión, minutos, orden).
            RefreshIslandCalendar();
        }
    }

    /// <summary>Aplica el ajuste del calendario al contenedor sin reiniciar la app.</summary>
    private static void RefreshIslandCalendar() =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshCalendarContent();
}
