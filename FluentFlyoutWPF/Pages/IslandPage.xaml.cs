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
using System.Windows.Media;

namespace FluentFlyoutWPF.Pages;

public partial class IslandPage : Page
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public enum IslandCategory
    {
        All,
        General,
        Screens,
        Music,
        Interaction,
        Appearance,
        Content,
        Timer,
        Apps,
        Organization,
        Tools,
    }

    private readonly IslandCategory _category;

    public IslandPage() : this(IslandCategory.All)
    {
    }

    protected IslandPage(IslandCategory category)
    {
        _category = category;
        InitializeComponent();
        DataContext = SettingsManager.Current;
        RefreshScreensEditor();
        IslandWeatherPlaceText.Text = SettingsManager.Current.IslandWeatherPlace.Trim().Length > 0
            ? SettingsManager.Current.IslandWeatherPlace
            : "Sin lugar elegido";
        ApplyCategoryVisibility();
    }

    private void ApplyCategoryVisibility()
    {
        if (_category == IslandCategory.All)
            return;

        HashSet<int> visibleRows = _category switch
        {
            IslandCategory.General => [0, 1, 2, 3, 4, 5, 6],
            IslandCategory.Screens => [7, 8],
            IslandCategory.Music => [9, 10, 11, 12, 13, 14],
            IslandCategory.Interaction => [15, 16, 17, 18, 19, 20, 21, 22, 23],
            IslandCategory.Appearance => [24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34],
            IslandCategory.Content => [36, 37],
            IslandCategory.Timer => [38, 39, 40, 41, 42],
            IslandCategory.Apps => [43, 44],
            IslandCategory.Organization => [47, 48],
            IslandCategory.Tools => [49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59],
            _ => [],
        };

        foreach (FrameworkElement child in IslandSettingsGrid.Children)
            child.Visibility = visibleRows.Contains(Grid.GetRow(child)) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Pantallas del Island (change island-pantallas)
    // ------------------------------------------------------------------

    /// <summary>
    /// Editor de pantallas: una fila por pantalla con las funcionalidades que la
    /// componen como TARJETAS EN ORDEN, de izquierda a derecha —el mismo orden en el
    /// que la pantalla las presenta en el Island, en sus fichas del compacto y en sus
    /// columnas del expandido—, cada una movible con ◀ ▶ y quitables con ✕, más una
    /// lista para añadir las que falten. Se reconstruye entero tras cada cambio, así
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
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = $"Pantalla {i + 1} · {ids.Count}/{IslandFeatureIds.MaxFeaturesPerScreen}",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 0);
            header.Children.Add(title);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(ScreenButton("Subir", $"Subir la pantalla {i + 1} en el recorrido de la rueda y las flechas", () => MoveScreen(screen, -1)));
            actions.Children.Add(ScreenButton("Bajar", $"Bajar la pantalla {i + 1} en el recorrido de la rueda y las flechas", () => MoveScreen(screen, +1)));
            actions.Children.Add(ScreenButton("Quitar", "Quitar esta pantalla (la última no se puede quitar)", () =>
            {
                settings.RemoveIslandScreen(screen);
                RefreshScreensEditor();
            }));
            Grid.SetColumn(actions, 1);
            header.Children.Add(actions);
            row.Children.Add(header);

            // Tarjetas en orden: el orden de la fila ES el orden de la pantalla.
            var cards = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
            for (int position = 0; position < ids.Count; position++)
            {
                string featureId = ids[position];
                int at = position;
                cards.Children.Add(ScreenFeatureCard(screen, featureId, at, ids.Count));
            }
            row.Children.Add(cards);

            // Añadir: solo las funcionalidades que no están ya en esta pantalla y solo
            // mientras quede hueco. Una pantalla admite hasta MaxFeaturesPerScreen: es lo
            // que cabe en una sola pantalla del Island (una ficha por funcionalidad en el
            // compacto y una columna en el expandido).
            bool full = ids.Count >= IslandFeatureIds.MaxFeaturesPerScreen;
            var missing = full
                ? new List<string>()
                : IslandFeatureIds.All.Where(id => !ids.Contains(id)).ToList();
            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            addRow.Children.Add(new TextBlock
            {
                Text = "Añadir:",
                FontSize = 12,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
            var add = new ComboBox
            {
                Width = 240,
                IsEnabled = missing.Count > 0,
                ToolTip = $"Se añade al final de la pantalla; colócala con ◀ ▶ (máx. {IslandFeatureIds.MaxFeaturesPerScreen} por pantalla)",
            };
            if (full)
            {
                add.Items.Add($"Pantalla completa ({IslandFeatureIds.MaxFeaturesPerScreen}/{IslandFeatureIds.MaxFeaturesPerScreen})");
                add.SelectedIndex = 0;
                add.IsEnabled = false;
            }
            else if (missing.Count == 0)
            {
                add.Items.Add("La pantalla las lleva todas");
                add.SelectedIndex = 0;
                add.IsEnabled = false;
            }
            else
            {
                foreach (var id in missing) add.Items.Add(IslandFeatureIds.DisplayName(id));
            }
            add.SelectionChanged += (_, e) =>
            {
                if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string name) return;
                string featureId = missing.FirstOrDefault(id => IslandFeatureIds.DisplayName(id) == name);
                if (featureId != null && settings.AddIslandScreenFeature(screen, featureId))
                    RefreshScreensEditor();
            };
            addRow.Children.Add(add);
            row.Children.Add(addRow);
            IslandScreensPanel.Children.Add(row);
        }
    }

    /// <summary>
    /// Tarjeta de una funcionalidad dentro de una pantalla: su nombre y los mandos
    /// para colocarla (◀ ▶) o sacarla (✕). El orden de las tarjetas es el orden en el
    /// que la pantalla la muestra, de izquierda a derecha.
    /// </summary>
    private Border ScreenFeatureCard(string screen, string featureId, int position, int total)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 4, 3),
            Margin = new Thickness(0, 0, 8, 8),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = IslandFeatureIds.DisplayName(featureId),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        // Posición dentro de la pantalla: se ve el orden de un vistazo.
        content.Children.Add(new TextBlock
        {
            Text = $"{position + 1}º",
            FontSize = 11,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        var moveLeft = ScreenButton("◀", "Mover antes en la pantalla", () =>
        {
            if (SettingsManager.Current.MoveIslandScreenFeature(screen, featureId, -1)) RefreshScreensEditor();
        });
        moveLeft.IsEnabled = position > 0;
        var moveRight = ScreenButton("▶", "Mover después en la pantalla", () =>
        {
            if (SettingsManager.Current.MoveIslandScreenFeature(screen, featureId, +1)) RefreshScreensEditor();
        });
        moveRight.IsEnabled = position < total - 1;
        var remove = ScreenButton("✕", "Sacar esta funcionalidad de la pantalla (la última no se puede sacar)", () =>
        {
            if (SettingsManager.Current.RemoveIslandScreenFeature(screen, featureId)) RefreshScreensEditor();
        });
        content.Children.Add(moveLeft);
        content.Children.Add(moveRight);
        content.Children.Add(remove);
        card.Child = content;
        return card;
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

    // --- clima (change island-clima) ---

    private CancellationTokenSource? _weatherSearch;

    /// <summary>Enter en la caja de búsqueda busca igual que el botón.</summary>
    private void IslandWeatherSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) IslandWeatherSearch_Click(sender, e);
    }

    /// <summary>
    /// Busca lugares y los enseña como sugerencias: se elige el lugar CORRECTO entre
    /// varios del mismo nombre (nombre, región y país de cada uno), que es justo lo
    /// que un campo de coordenadas no resuelve.
    /// </summary>
    private async void IslandWeatherSearch_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsManager.Current;
        string query = (IslandWeatherSearch.Text ?? "").Trim();
        IslandWeatherPlaces.Children.Clear();
        if (query.Length < 2)
        {
            settings.IslandWeatherError = "Escribe al menos dos letras del lugar.";
            return;
        }
        settings.IslandWeatherError = "";
        settings.IslandWeatherStatus = "Buscando lugares…";
        IslandWeatherSearchBtn.IsEnabled = false;
        _weatherSearch?.Cancel();
        var cts = new CancellationTokenSource();
        _weatherSearch = cts;
        try
        {
            var places = await IslandWeatherService.SearchPlacesAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (places.Count == 0)
            {
                settings.IslandWeatherStatus = "";
                settings.IslandWeatherError = $"No se encontró ningún lugar llamado «{query}».";
                return;
            }
            settings.IslandWeatherStatus = places.Count == 1
                ? "Un lugar encontrado: pulsa para elegirlo."
                : $"{places.Count} lugares encontrados: elige el tuyo.";
            foreach (var place in places)
            {
                var button = new Button
                {
                    Content = place.Label,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(0, 0, 0, 4),
                    ToolTip = $"{place.Latitude:0.####}, {place.Longitude:0.####}",
                };
                button.Click += (_, _) => ApplyWeatherPlace(place);
                IslandWeatherPlaces.Children.Add(button);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Clima: búsqueda de lugares fallida");
            settings.IslandWeatherStatus = "";
            settings.IslandWeatherError = "No se pudo buscar: revisa la conexión a internet.";
        }
        finally
        {
            IslandWeatherSearchBtn.IsEnabled = true;
            if (ReferenceEquals(_weatherSearch, cts)) _weatherSearch = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Adopta el lugar elegido: se guarda con sus coordenadas y la funcionalidad se
    /// enciende sola (elegir un lugar es querer verlo). El Island consulta el lugar
    /// nuevo en el acto.
    /// </summary>
    private void ApplyWeatherPlace(IslandWeatherPlace place)
    {
        var settings = SettingsManager.Current;
        settings.IslandWeatherPlace = place.Label;
        settings.IslandWeatherLatitude = place.Latitude;
        settings.IslandWeatherLongitude = place.Longitude;
        settings.IslandWeatherError = "";
        settings.IslandWeatherEnabled = true;
        IslandWeatherPlaceText.Text = place.Label;
        IslandWeatherPlaces.Children.Clear();
        settings.IslandWeatherStatus = $"Lugar elegido: {place.Label}.";
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshWeatherNow();
    }

    /// <summary>Refresco a mano del dato (el ciclo periódico sigue igual).</summary>
    private void IslandWeatherRefresh_Click(object sender, RoutedEventArgs e) =>
        (Application.Current?.MainWindow as MainWindow)?.islandWindow?.RefreshWeatherNow();

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
