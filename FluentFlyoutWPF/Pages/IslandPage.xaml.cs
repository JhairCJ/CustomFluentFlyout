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
            : IslandStrings.Get("IslandPage_NoPlace", "No place chosen");
        ApplyCategoryVisibility();
    }

    private void ApplyCategoryVisibility()
    {
        if (_category == IslandCategory.All)
            return;

        // La categoría de cada fila es su Tag: el mismo nombre que esta enumeración.
        // Mover, añadir o borrar filas del XAML no rompe nada, y una fila sin Tag
        // (o con una categoría que no existe) no se cuela en ninguna página.
        string category = _category.ToString();
        foreach (FrameworkElement child in IslandSettingsGrid.Children)
            child.Visibility = child.Tag as string == category ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Pantallas del Island (change island-pantallas)
    // ------------------------------------------------------------------

    /// <summary>
    /// Editor de pantallas: una fila por pantalla con las funcionalidades que la
    /// componen como TARJETAS EN ORDEN, de izquierda a derecha —el mismo orden en el
    /// que la pantalla las presenta en el Island: sus columnas en el expandido y el
    /// turno del compacto cuando una de ellas está activa—, cada una movible con ◀ ▶ y
    /// quitables con ✕, más una lista para añadir las que falten. Se reconstruye entero
    /// tras cada cambio, así el editor siempre enseña lo que hay guardado.
    /// </summary>
    private void RefreshScreensEditor()
    {
        var settings = SettingsManager.Current;
        IslandScreensPanel.Children.Clear();
        IslandScreenNewButton.IsEnabled = settings.HasUnassignedIslandFeature();
        var assigned = settings.IslandScreens
            .SelectMany(IslandFeatureIds.ParseScreen)
            .ToHashSet(StringComparer.Ordinal);
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
                Text = IslandStrings.Format("IslandPage_ScreenTitle", "Screen {0} · {1}/{2}",
                    i + 1, ids.Count, IslandFeatureIds.MaxFeaturesPerScreen),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 0);
            header.Children.Add(title);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(ScreenButton(IslandStrings.Get("IslandPage_MoveUp", "Up"),
                IslandStrings.Format("IslandPage_MoveUpTip", "Move screen {0} up in the wheel and arrow cycle", i + 1),
                () => MoveScreen(screen, -1)));
            actions.Children.Add(ScreenButton(IslandStrings.Get("IslandPage_MoveDown", "Down"),
                IslandStrings.Format("IslandPage_MoveDownTip", "Move screen {0} down in the wheel and arrow cycle", i + 1),
                () => MoveScreen(screen, +1)));
            actions.Children.Add(ScreenButton(IslandStrings.Get("IslandPage_Remove", "Remove"),
                IslandStrings.Get("IslandPage_RemoveScreenTip", "Remove this screen (the last one cannot be removed)"), () =>
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

            // Añadir: solo las funcionalidades que no están asignadas a NINGUNA otra
            // pantalla y solo mientras quede hueco. Una pantalla admite hasta
            // MaxFeaturesPerScreen: es lo que cabe en una sola pantalla del Island.
            bool full = ids.Count >= IslandFeatureIds.MaxFeaturesPerScreen;
            // Solo las funcionalidades que SON pantalla: los avisos (Bluetooth,
            // cargador) no se colocan en ninguna —se presentan solos cuando ocurre su
            // evento— y ofrecerlos aquí era invitar a crear una pantalla vacía.
            var missing = full
                ? new List<string>()
                : IslandFeatureIds.Screenable.Where(id => !assigned.Contains(id)).ToList();
            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            addRow.Children.Add(new TextBlock
            {
                Text = IslandStrings.Get("IslandPage_AddLabel", "Add:"),
                FontSize = 12,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
            var add = new ComboBox
            {
                Width = 240,
                IsEnabled = missing.Count > 0,
                ToolTip = IslandStrings.Format("IslandPage_AddTip",
                    "It is added at the end of the screen; place it with ◀ ▶ (max. {0} per screen)",
                    IslandFeatureIds.MaxFeaturesPerScreen),
            };
            if (full)
            {
                add.Items.Add(IslandStrings.Format("IslandPage_ScreenFull", "Screen full ({0}/{1})",
                    IslandFeatureIds.MaxFeaturesPerScreen, IslandFeatureIds.MaxFeaturesPerScreen));
                add.SelectedIndex = 0;
                add.IsEnabled = false;
            }
            else if (missing.Count == 0)
            {
                add.Items.Add(IslandStrings.Get("IslandPage_ScreenAll", "The screen already has them all"));
                add.SelectedIndex = 0;
                add.IsEnabled = false;
            }
            else
            {
                foreach (var id in missing) add.Items.Add(IslandStrings.FeatureName(id));
            }
            add.SelectionChanged += (_, e) =>
            {
                if (e.AddedItems.Count == 0 || e.AddedItems[0] is not string name) return;
                var featureId = missing.FirstOrDefault(id => IslandStrings.FeatureName(id) == name);
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
            Text = IslandStrings.FeatureName(featureId),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        // Posición dentro de la pantalla: se ve el orden de un vistazo.
        content.Children.Add(new TextBlock
        {
            Text = IslandStrings.Format("IslandPage_Position", "#{0}", position + 1),
            FontSize = 11,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        var moveLeft = ScreenButton("◀", IslandStrings.Get("IslandPage_MoveEarlier", "Move earlier in the screen"), () =>
        {
            if (SettingsManager.Current.MoveIslandScreenFeature(screen, featureId, -1)) RefreshScreensEditor();
        });
        moveLeft.IsEnabled = position > 0;
        var moveRight = ScreenButton("▶", IslandStrings.Get("IslandPage_MoveLater", "Move later in the screen"), () =>
        {
            if (SettingsManager.Current.MoveIslandScreenFeature(screen, featureId, +1)) RefreshScreensEditor();
        });
        moveRight.IsEnabled = position < total - 1;
        var remove = ScreenButton("✕", IslandStrings.Get("IslandPage_RemoveFeatureTip", "Take this feature out of the screen (the last one cannot be taken out)"), () =>
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
        if (SettingsManager.Current.AddIslandScreen()) RefreshScreensEditor();
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
            Title = IslandStrings.Get("IslandPage_AddAppDialog", "Add app to the Island"),
            Filter = IslandStrings.Get("IslandPage_AppFilter",
                "Applications (*.exe)|*.exe|Shortcuts (*.lnk)|*.lnk|All files (*.*)|*.*"),
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
            settings.IslandWeatherError = IslandStrings.Get("IslandPage_WeatherFewLetters", "Type at least two letters of the place.");
            return;
        }
        settings.IslandWeatherError = "";
        settings.IslandWeatherStatus = IslandStrings.Get("IslandPage_WeatherSearching", "Searching places…");
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
                settings.IslandWeatherError = IslandStrings.Format("IslandPage_WeatherNoResults",
                    "No place named «{0}» was found.", query);
                return;
            }
            settings.IslandWeatherStatus = places.Count == 1
                ? IslandStrings.Get("IslandPage_WeatherOne", "One place found: click to choose it.")
                : IslandStrings.Format("IslandPage_WeatherMany", "{0} places found: pick yours.", places.Count);
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
            settings.IslandWeatherError = IslandStrings.Get("IslandPage_WeatherSearchFailed", "Could not search: check your internet connection.");
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
        settings.IslandWeatherStatus = IslandStrings.Format("IslandPage_WeatherChosen", "Place chosen: {0}.", place.Label);
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
            Title = IslandStrings.Get("IslandPage_ParkDialog", "Park files in the Island shelf"),
            Filter = IslandStrings.Get("IslandPage_AllFilesFilter", "All files (*.*)|*.*"),
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
            SettingsManager.Current.IslandShelfError = IslandStrings.Get("IslandPage_ShelfOpenFailed", "Could not open the shelf folder.");
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
                settings.GoogleCalendarError = IslandStrings.Get("IslandPage_CalendarNeedClientId", "Paste the OAuth client id of your Google project.");
                return;
            }
            settings.GoogleCalendarError = "";
            settings.GoogleCalendarStatus = IslandStrings.Get("IslandPage_CalendarWaiting", "Waiting for you to authorize in the browser…");
            var tokens = await GoogleCalendarClient.AuthorizeAsync(clientId, settings.GoogleCalendarClientSecret.Trim());
            GoogleCalendarClient.Save(settings, tokens);
            settings.GoogleCalendarAccount = await GoogleCalendarClient.FetchAccountAsync(tokens.AccessToken);
            settings.GoogleCalendarStatus = IslandStrings.Get("IslandPage_CalendarSignedIn", "Signed in.");
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
            settings.GoogleCalendarError = IslandStrings.Get("IslandPage_CalendarAuthFailed", "The authorization was not completed: try again.");
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
            settings.GoogleCalendarError = IslandStrings.Get("IslandPage_CalendarSignInFirst", "Sign in with Google to read the calendar.");
            return;
        }
        var button = sender as Button;
        if (button != null) button.IsEnabled = false;
        settings.GoogleCalendarStatus = IslandStrings.Get("IslandPage_CalendarSyncing", "Syncing…");
        try
        {
            await GoogleCalendarService.Instance.RefreshNowAsync();
            settings.GoogleCalendarStatus = settings.GoogleCalendarError.Length == 0
                ? IslandStrings.Get("IslandPage_CalendarUpToDate", "Calendar up to date.")
                : "";
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
