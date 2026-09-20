// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// PANTALLAS del Island (change island-pantallas): el contenedor deja de navegar
/// por funcionalidades sueltas y navega por pantallas configuradas, y una pantalla
/// puede llevar VARIAS funcionalidades a la vez.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Pantalla simple = vista de siempre</b> (RF-1): con una sola
/// funcionalidad la pantalla presenta su vista rica (música con carátula y
/// ecualizador, temporizador con sus reels…), así que nada del comportamiento
/// actual cambia por el hecho de configurar pantallas.</item>
/// <item><b>Pantalla combinada = resumen + paneles</b> (RF-2/RF-3): en compacto se
/// ve una fila de fichas (icono y un dato corto por funcionalidad, «78 %»,
/// «12:30», «en 5 min») y en expandido se apilan los paneles de todas ellas. El
/// ancho del compacto lo fija el número de fichas y el alto lo mide el contenido:
/// el tamaño de una pantalla combinada es DINÁMICO por construcción.</item>
/// <item><b>Una sola navegación</b> (RF-4): la rueda y las flechas recorren
/// pantallas (no funcionalidades) y se saltan las que no tienen nada usable; la
/// «activa vigente» de «Visible mientras activo» se resuelve a su pantalla, de modo
/// que el compacto enseña el grupo entero al que pertenece lo que está activo.</item>
/// <item><b>Sin configuración rige lo de siempre</b> (RF-5): una pantalla por
/// funcionalidad, en el orden por defecto.</item>
/// </list>
///
/// <para>Parte del IslandWindow; el registro de funcionalidades está en
/// <c>IslandFeatures.cs</c> y las rutas de presentación en
/// <c>IslandWindow.Views.cs</c>.</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>Ancho de una ficha de pantalla combinada (icono + dato corto).</summary>
    private const double ScreenChipWidth = 96;
    /// <summary>Ancho de reposo del compacto de una sola funcionalidad (CompactLayer del XAML).</summary>
    private const double SingleCompactLayerWidth = 240;
    /// <summary>Ancho de una columna del expandido de una pantalla combinada.</summary>
    private const double ScreenColumnWidth = 236;
    /// <summary>Separación entre columnas del expandido (igual que el margen del XAML).</summary>
    private const double ScreenColumnGap = 14;
    /// <summary>Relleno lateral del contenido expandido (el margen del ExpandedLayer: 16+16).</summary>
    private const double ScreenRowPadding = 32;
    /// <summary>Ancho máximo del expandido de una pantalla combinada.</summary>
    private const double ScreenExpandedMaxWidth = 900;

    /// <summary>Pantallas configuradas, ya saneadas y en orden de navegación.</summary>
    private readonly List<string[]> _screens = [];
    /// <summary>Pantalla vigente (índice de <see cref="_screens"/>).</summary>
    private int _screenIndex;

    /// <summary>
    /// Relee las pantallas configuradas y deja la vigente en un índice válido. Se
    /// llama al arrancar y en cada cambio del ajuste; NO toca la vista (quien
    /// cambia de pantalla es la navegación o el ajuste que la re-presenta).
    /// </summary>
    public void ApplyScreens()
    {
        _screens.Clear();
        foreach (var raw in SettingsManager.Current.IslandScreens)
        {
            var ids = IslandFeatureIds.ParseScreen(raw);
            if (ids.Count > 0) _screens.Add([.. ids]);
        }
        if (_screens.Count == 0)
            _screens.AddRange(IslandFeatureIds.DefaultScreens.Select(id => new[] { id }));
        _screenIndex = Math.Clamp(_screenIndex, 0, _screens.Count - 1);
    }

    private IReadOnlyList<string> CurrentScreenIds() =>
        _screens.Count == 0 ? [] : _screens[Math.Clamp(_screenIndex, 0, _screens.Count - 1)];

    /// <summary>Funcionalidades usables de una pantalla, en el orden de la pantalla.</summary>
    private List<IIslandFeature> ScreenUsableFeatures(IReadOnlyList<string> ids)
    {
        var members = new List<IIslandFeature>();
        foreach (var id in ids)
        {
            if (FeatureById(id) is { } feature && feature.State.Usable) members.Add(feature);
        }
        return members;
    }

    /// <summary>Miembros usables de la pantalla vigente.</summary>
    private List<IIslandFeature> CurrentScreenFeatures() => ScreenUsableFeatures(CurrentScreenIds());

    /// <summary>¿La pantalla vigente lleva varias funcionalidades? (entonces su compacto es de fichas)</summary>
    private bool ScreenIsCombined() => CurrentScreenFeatures().Count > 1;

    /// <summary>Primera pantalla que contiene la funcionalidad dada (-1 si no está en ninguna).</summary>
    private int ScreenIndexOfFeature(string id)
    {
        for (int i = 0; i < _screens.Count; i++)
            for (int j = 0; j < _screens[i].Length; j++)
                if (string.Equals(_screens[i][j], id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Pantallas con algo usable ahora mismo: es la unidad de navegación (RF-4).</summary>
    private int UsableScreenCount() => _screens.Count(screen => ScreenUsableFeatures(screen).Count > 0);

    /// <summary>
    /// Presenta la pantalla que contiene la funcionalidad dada (compacto). Es el
    /// punto por el que las rutas de «activa vigente» muestran el grupo entero al
    /// que pertenece lo activo, en vez de arrancar su pantalla del grupo.
    /// </summary>
    private bool ShowScreenOfFeature(IIslandFeature feature)
    {
        int index = ScreenIndexOfFeature(feature.Id);
        if (index >= 0) _screenIndex = index;
        return ShowCurrentScreenCompact();
    }

    /// <summary>Presenta la pantalla vigente: su vista rica si es simple, sus fichas si es combinada.</summary>
    private bool ShowCurrentScreenCompact()
    {
        var members = CurrentScreenFeatures();
        if (members.Count == 0) return false;
        if (members.Count == 1) return members[0].TryShowCompact();
        ShowCombinedScreenCompact(members);
        return true;
    }

    /// <summary>
    /// Expande la pantalla vigente: la vista rica de su única funcionalidad si es
    /// simple, y sus columnas de IZQUIERDA A DERECHA si es combinada.
    /// </summary>
    private bool ExpandCurrentScreen()
    {
        var members = CurrentScreenFeatures();
        if (members.Count == 0) return false;
        if (members.Count == 1) return members[0].TryShowExpanded();
        ShowExpandedView(IslandContentMode.Screen, members[0], RefreshScreenMembers);
        return true;
    }

    /// <summary>
    /// La pantalla vigente deja paso a otra que sí tenga algo usable: se busca la
    /// siguiente en orden de navegación y se adopta como vigente. Se usa cuando un
    /// ajuste deja la pantalla actual sin nada que enseñar (una funcionalidad
    /// apagada, una pantalla editada) para no quedarse con una vista vacía.
    /// </summary>
    private bool MoveToNearestUsableScreen()
    {
        for (int step = 0; step < _screens.Count; step++)
        {
            int index = (_screenIndex + step) % _screens.Count;
            if (ScreenUsableFeatures(_screens[index]).Count == 0) continue;
            _screenIndex = index;
            return true;
        }
        return false;
    }

    /// <summary>
    /// El ajuste de pantallas cambió (o se apagó una funcionalidad): se relee la
    /// configuración y, si el Island está a la vista, se vuelve a presentar la
    /// pantalla vigente —compacto o expandido— para que el cambio se VEA en el acto
    /// y el contenedor se adapte (ancho y alto del contenido nuevo). Oculto no se
    /// despliega nada: la próxima aparición ya usa la configuración nueva.
    /// </summary>
    public void RefreshScreensContent() => Dispatcher.Invoke(() =>
    {
        ApplyScreens();
        if (!IsBoxShown || _disposed) return;
        if (_contentMode == IslandContentMode.Screen)
        {
            if (!MoveToNearestUsableScreen()) { ShowInactiveOrHidden(); return; }
            if (_expanded ? ExpandCurrentScreen() : ShowCurrentScreenCompact()) return;
        }
        // Otra vista delante (música, temporizador, un aviso…): solo se re-mide y se
        // recoloca la caja, que es lo que el ajuste de pantallas puede cambiarle.
        SyncMeasuredHeight();
        PositionTopCenter();
    });

    private void ShowCombinedScreenCompact(List<IIslandFeature> members) =>
        ShowCompactView(IslandContentMode.Screen, members[0], RefreshScreenMembers);

    /// <summary>
    /// Repinta el contenido de todos los miembros de la pantalla vigente. Las
    /// fichas del compacto salen de <see cref="IIslandFeature.Summary"/> y los
    /// paneles del expandido de cada funcionalidad; el contenedor decide después
    /// cuáles se ven (<c>ApplyContentVisibilityCore</c>).
    /// </summary>
    private void RefreshScreenMembers()
    {
        var members = CurrentScreenFeatures();
        ScreenCompactList.ItemsSource = members
            .Select(feature => feature.Summary)
            .Select(summary => new ScreenChip(summary.Glyph, summary.Text))
            .ToList();
        foreach (var member in members) RefreshMemberContent(member.Id);
    }

    /// <summary>
    /// Refresco periódico de una pantalla combinada con el expandido delante (su
    /// cadencia por contenido): cuenta atrás del temporizador, cuentas del calendario
    /// y seek de la música. El resto no envejece mientras se mira.
    /// </summary>
    private void RefreshCombinedScreenTick()
    {
        if (_disposed || !IsBoxShown || _contentMode != IslandContentMode.Screen || !_expanded) return;
        foreach (var member in CurrentScreenFeatures())
        {
            switch (member.Id)
            {
                case "timer":
                    RefreshTimerUI();
                    break;
                case "calendar":
                    RefreshCalendarList();
                    break;
                case "media":
                    if (Current() is { } session) UpdateSeek(session);
                    break;
            }
        }
    }

    /// <summary>Refresca los datos de una funcionalidad dentro de una pantalla combinada.</summary>
    private void RefreshMemberContent(string id)
    {
        switch (id)
        {
            case "media":
                if (Current() is { } session) RefreshUi(session);
                break;
            case "timer":
                RefreshTimerUI();
                break;
            case "apps":
                RefreshAppList();
                break;
            case "shelf":
                RefreshShelfList();
                break;
            case "calendar":
                RefreshCalendarList();
                break;
            case IslandFeatureIds.Bluetooth:
                RefreshBluetoothUI();
                break;
            case IslandFeatureIds.Clipboard:
                RefreshClipboardViews();
                break;
            case IslandFeatureIds.Weather:
                RefreshWeatherUI();
                break;
        }
    }

    /// <summary>
    /// Ficha del compacto de una pantalla combinada: el icono de la funcionalidad y
    /// su dato corto (el resumen que ella misma declara).
    /// </summary>
    private sealed record ScreenChip(Wpf.Ui.Controls.SymbolRegular Glyph, string Text);

    /// <summary>Ancho que necesita el compacto de una pantalla combinada (RF-3).</summary>
    private static double ScreenWidthForMembers(int members) =>
        Math.Clamp(members * ScreenChipWidth + 24, 160, 480);

    // ------------------------------------------------------------------
    // Visibilidad de las capas de una pantalla combinada
    // ------------------------------------------------------------------

    /// <summary>
    /// Deja a la vista SOLO lo que compone la pantalla vigente: su fila de fichas y
    /// los paneles del expandido de cada miembro, apilados. Todo lo demás se apaga
    /// (una pantalla combinada no puede enseñar una capa de una funcionalidad que no
    /// la compone).
    /// </summary>
    private void ApplyScreenLayerVisibility()
    {
        var members = CurrentScreenFeatures();
        // Pantalla simple (o sin miembros usables): los paneles vuelven a su sitio.
        if (members.Count <= 1) RestoreExpandedHomes();
        ScreenCompactGrid.Visibility = Visibility.Visible;
        CompactLayer.Width = ScreenWidthForMembers(members.Count);
        MusicCompactGrid.Visibility = Visibility.Collapsed;
        TimerCompactGrid.Visibility = Visibility.Collapsed;
        AppsCompactGrid.Visibility = Visibility.Collapsed;
        ShelfCompactGrid.Visibility = Visibility.Collapsed;
        CalendarCompactGrid.Visibility = Visibility.Collapsed;
        BluetoothCompactGrid.Visibility = Visibility.Collapsed;
        ClipboardCompactGrid.Visibility = Visibility.Collapsed;
        WeatherCompactGrid.Visibility = Visibility.Collapsed;
        WeatherExpanded.Visibility = Visibility.Collapsed;
        HideAllExpandedPanels();
        // El expandido va de IZQUIERDA A DERECHA: los paneles de cada miembro se
        // trasladan a su columna, en el orden de la pantalla (RF-2/RF-3).
        ComposeScreenExpandedRow(members);
        foreach (var member in members) ShowMemberPanels(member.Id);
        UpdateArrows();
    }

    /// <summary>Apaga todos los paneles del expandido (punto de partida de una composición).</summary>
    private void HideAllExpandedPanels()
    {
        MusicExpandedTop.Visibility = Visibility.Collapsed;
        ControlsRow.Visibility = Visibility.Collapsed;
        SeekRow.Visibility = Visibility.Collapsed;
        TimerAlert.Visibility = Visibility.Collapsed;
        AppsExpanded.Visibility = Visibility.Collapsed;
        ShelfExpanded.Visibility = Visibility.Collapsed;
        CalendarExpanded.Visibility = Visibility.Collapsed;
        ClipboardExpanded.Visibility = Visibility.Collapsed;
        CrossfadeTimerPanels(showConfig: false, showRun: false);
    }

    /// <summary>
    /// Enseña los paneles del expandido que pertenecen a una funcionalidad, con las
    /// mismas reglas que su pantalla simple (la alerta del temporizador manda sobre
    /// sus reels y el seek de música se ajusta a las capacidades de la sesión).
    /// </summary>
    private void ShowMemberPanels(string id)
    {
        switch (id)
        {
            case "media":
                MusicExpandedTop.Visibility = Visibility.Visible;
                ControlsRow.Visibility = Visibility.Visible;
                SeekRow.Visibility = Visibility.Visible;
                if (Current() is { } session) ApplyCapabilities(session);
                else SeekRow.Visibility = Visibility.Collapsed;
                break;
            case "timer":
                bool alert = _timer.State == IslandTimerState.Alerting;
                bool idle = _timer.State == IslandTimerState.Idle;
                TimerAlert.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
                CrossfadeTimerPanels(showConfig: idle && !alert, showRun: !idle && !alert);
                break;
            case "apps":
                AppsExpanded.Visibility = Visibility.Visible;
                break;
            case "shelf":
                ShelfExpanded.Visibility = Visibility.Visible;
                break;
            case "calendar":
                CalendarExpanded.Visibility = Visibility.Visible;
                break;
            case IslandFeatureIds.Clipboard:
                ClipboardExpanded.Visibility = Visibility.Visible;
                RefreshClipboardViews();
                break;
            case IslandFeatureIds.Weather:
                WeatherExpanded.Visibility = Visibility.Visible;
                RefreshWeatherUI();
                break;
            // Bluetooth no tiene expandido: su aviso vive en el compacto.
        }
    }

    /// <summary>
    /// ¿La vista de una pantalla la sostiene alguno de sus miembros? (RF-4). Es la
    /// regla de sostenimiento de una pantalla combinada: mientras una sola de sus
    /// funcionalidades esté activa, la pantalla se queda.
    /// </summary>
    private bool ScreenSustainsView() => CurrentScreenFeatures().Any(SingleFeatureSustainsView);

    // ------------------------------------------------------------------
    // Composición del expandido (izquierda a derecha)
    // ------------------------------------------------------------------

    /// <summary>
    /// Paneles del expandido que pertenecen a cada funcionalidad: son los que se
    /// MUEVEN a su columna cuando la pantalla es combinada (cada uno vive en un solo
    /// contenedor a la vez, así que se traslada, no se copia).
    /// </summary>
    private IEnumerable<UIElement> MemberPanels(string id) => id switch
    {
        "media" => [MusicExpandedTop, SeekRow, ControlsRow],
        "timer" => [TimerAlert, TimerExpanded, TimerRunPanel],
        "apps" => [AppsExpanded],
        "shelf" => [ShelfExpanded],
        "calendar" => [CalendarExpanded],
        IslandFeatureIds.Clipboard => [ClipboardExpanded],
        IslandFeatureIds.Weather => [WeatherExpanded],
        // Bluetooth no tiene expandido: su aviso vive en el compacto.
        _ => [],
    };

    /// <summary>Columna del expandido que aloja los paneles de una funcionalidad (null si no tiene).</summary>
    private StackPanel? ColumnFor(string id) => id switch
    {
        "media" => ScreenColumnMedia,
        "timer" => ScreenColumnTimer,
        "apps" => ScreenColumnApps,
        "shelf" => ScreenColumnShelf,
        "calendar" => ScreenColumnCalendar,
        IslandFeatureIds.Clipboard => ScreenColumnClipboard,
        IslandFeatureIds.Weather => ScreenColumnWeather,
        _ => null,
    };

    /// <summary>
    /// Dónde vivía cada panel antes de entrar en una columna: devolverlo a su sitio
    /// es lo que permite volver a las vistas simples sin duplicar paneles ni
    /// reordenar el expandido a mano.
    /// </summary>
    private readonly Dictionary<UIElement, (Panel Parent, int Index)> _expandedHomes = [];

    /// <summary>
    /// Compone el expandido de una pantalla combinada: una columna por funcionalidad,
    /// de IZQUIERDA A DERECHA en el orden de la pantalla, con los paneles de cada una
    /// dentro. Las columnas que no componen la pantalla se ocultan y reciben el ancho
    /// que les toca (el ancho total de la caja es dinámico: lo fija el número de
    /// columnas).
    /// </summary>
    private void ComposeScreenExpandedRow(List<IIslandFeature> members)
    {
        RestoreExpandedHomes();
        var columns = new List<(string Id, StackPanel Host)>();
        foreach (var member in members)
        {
            if (ColumnFor(member.Id) is { } host) columns.Add((member.Id, host));
        }
        foreach (var column in AllScreenColumns())
        {
            bool used = columns.Any(c => ReferenceEquals(c.Host, column));
            column.Visibility = used ? Visibility.Visible : Visibility.Collapsed;
            column.Width = used ? ScreenColumnWidth : double.NaN;
        }
        ScreenExpandedRow.Visibility = columns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, host) in columns)
        {
            foreach (var panel in MemberPanels(id)) MoveToColumn(panel, host);
        }
    }

    private IEnumerable<StackPanel> AllScreenColumns() =>
        [ScreenColumnMedia, ScreenColumnTimer, ScreenColumnApps, ScreenColumnShelf, ScreenColumnCalendar, ScreenColumnClipboard, ScreenColumnWeather];

    /// <summary>Mueve un panel a su columna recordando su sitio original (una sola vez).</summary>
    private void MoveToColumn(UIElement panel, Panel host)
    {
        if (VisualTreeHelper.GetParent(panel) is not Panel parent) return;
        if (ReferenceEquals(parent, host)) return;
        if (!_expandedHomes.ContainsKey(panel)) _expandedHomes[panel] = (parent, parent.Children.IndexOf(panel));
        parent.Children.Remove(panel);
        host.Children.Add(panel);
    }

    /// <summary>
    /// Devuelve todos los paneles a su sitio original. Se llama al componer (punto de
    /// partida limpio) y al salir de una pantalla combinada; sin paneles movidos no
    /// hace nada.
    /// </summary>
    private void RestoreExpandedHomes()
    {
        if (_expandedHomes.Count == 0)
        {
            ScreenExpandedRow.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var (panel, home) in _expandedHomes.OrderBy(kv => kv.Value.Index))
        {
            if (VisualTreeHelper.GetParent(panel) is Panel parent && !ReferenceEquals(parent, home.Parent))
                parent.Children.Remove(panel);
            home.Parent.Children.Insert(Math.Min(home.Index, home.Parent.Children.Count), panel);
        }
        _expandedHomes.Clear();
        ScreenExpandedRow.Visibility = Visibility.Collapsed;
        foreach (var column in AllScreenColumns())
        {
            column.Children.Clear();
            column.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Ancho del expandido de una pantalla combinada: dinámico, una columna por
    /// funcionalidad, sin pasarse del monitor.
    /// </summary>
    private double ScreenExpandedWidthForMembers(int members)
    {
        // Cada columna lleva su separación a la derecha (el margen del XAML), así que
        // el ancho necesario es una columna + su separación por cada miembro, más el
        // relleno lateral del contenido. Si faltara ese último margen, la última
        // columna quedaría recortada por el borde de la caja.
        double total = members * (ScreenColumnWidth + ScreenColumnGap) + ScreenRowPadding;
        var primary = PrimaryMonitor();
        double monitorWidth = primary.dpiX > 0 ? primary.workArea.Width * 96.0 / primary.dpiX : ScreenExpandedMaxWidth;
        return Math.Clamp(total, 280, Math.Min(ScreenExpandedMaxWidth, Math.Max(280, monitorWidth - 24)));
    }
}
