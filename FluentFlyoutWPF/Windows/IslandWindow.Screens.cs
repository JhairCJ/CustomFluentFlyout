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
/// PANTALLAS del Island (change island-pantallas): el contenedor no navega por
/// funcionalidades sueltas —ni las ordena con ninguna lista aparte— sino por pantallas
/// configuradas, y una pantalla puede llevar VARIAS funcionalidades a la vez.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>La pantalla es la vista</b>: cuando una funcionalidad entra en escena
/// (reproduce, cuenta, avisa…), el Island presenta SU PANTALLA, no la vista de la
/// funcionalidad suelta. Con una sola funcionalidad usable esa pantalla es su vista
/// rica de siempre y nada cambia (RF-1); con varias, el COMPACTO enseña UNA de ellas
/// —su vista rica— y el EXPANDIDO la pantalla entera, con una columna por
/// funcionalidad (RF-2/RF-3).</item>
/// <item><b>El compacto no agrupa</b>: una pantalla con varias funcionalidades enseña
/// en el compacto UNA sola (la que sostiene la vista: la que reproduce, cuenta o
/// avisa), con la vista rica de esa funcionalidad; las demás esperan al expandido. El
/// clic en el compacto abre la pantalla en la que está esa funcionalidad, que es donde
/// se ven todas juntas.</item>
/// <item><b>Sin pantalla no hay vista</b>: una funcionalidad que no está en ninguna
/// pantalla no se muestra aunque esté activa; el contenedor resuelve otra pantalla o
/// el reposo, nunca su vista suelta.</item>
/// <item><b>Hasta cuatro por pantalla</b>
/// (<see cref="IslandFeatureIds.MaxFeaturesPerScreen"/>): son las que caben en una
/// sola pantalla, de izquierda a derecha. El ajuste no guarda más y el ancho de las
/// columnas se reparte para que todas se vean enteras.</item>
/// <item><b>Orden propio</b> (RF-2/RF-3): el orden de una pantalla es el de sus
/// funcionalidades, y es el que siguen las columnas del expandido —y la elección de
/// qué funcionalidad ocupa el compacto—, siempre de izquierda a derecha. No hay
/// ninguna otra lista de orden.</item>
/// <item><b>Una sola navegación</b> (RF-4): la rueda y las flechas recorren
/// pantallas (no funcionalidades) y se saltan las que no tienen nada usable; la
/// «activa vigente» de «Visible mientras activo» se resuelve a su pantalla, de modo
/// que el compacto enseña la funcionalidad activa de ese grupo (una sola).</item>
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
    /// <summary>Ancho de reposo del compacto de una sola funcionalidad (CompactLayer del XAML).</summary>
    private const double SingleCompactLayerWidth = 240;
    /// <summary>Ancho MÁXIMO de una columna del expandido de una pantalla combinada.</summary>
    private const double ScreenColumnWidth = 236;
    /// <summary>Separación entre columnas del expandido (igual que el margen del XAML).</summary>
    private const double ScreenColumnGap = 14;
    /// <summary>Relleno lateral del contenido expandido (el margen del ExpandedLayer: 16+16).</summary>
    private const double ScreenRowPadding = 32;
    /// <summary>
    /// Ancho máximo del expandido de una pantalla combinada: con cuatro columnas (el
    /// máximo de <see cref="IslandFeatureIds.MaxFeaturesPerScreen"/>) entran a su ancho
    /// completo, y nunca se pasa del monitor.
    /// </summary>
    private const double ScreenExpandedMaxWidth = 1100;

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
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in SettingsManager.Current.IslandScreens)
        {
            var ids = IslandFeatureIds.ParseScreen(raw)
                .Where(assigned.Add)
                .ToList();
            if (ids.Count > 0) _screens.Add([.. ids]);
        }
        // Sin ninguna pantalla configurada rigen las de fábrica, PARSEADAS: envolver
        // cada cadena («media+timer+apps+shelf») en un array de un elemento dejaba
        // pantallas que no contenían ninguna funcionalidad conocida —ninguna usable—
        // y el contenedor se quedaba sin nada que presentar (ni la música).
        if (_screens.Count == 0)
            _screens.AddRange(IslandFeatureIds.DefaultScreens
                .Select(screen => IslandFeatureIds.ParseScreen(screen).ToArray())
                .Where(ids => ids.Length > 0));
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

    /// <summary>¿La pantalla vigente lleva varias funcionalidades? (entonces su expandido es de columnas)</summary>
    private bool ScreenIsCombined() => CurrentScreenFeatures().Count > 1;

    /// <summary>Primera pantalla que contiene la funcionalidad dada (-1 si no está en ninguna).</summary>
    private int ScreenIndexOfFeature(string id)
    {
        for (int i = 0; i < _screens.Count; i++)
            for (int j = 0; j < _screens[i].Length; j++)
                if (string.Equals(_screens[i][j], id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// Pantalla «de» una funcionalidad para resolver su vista. La configuración se
    /// sanea para que cada funcionalidad pertenezca como máximo a una pantalla, pero
    /// la búsqueda sigue siendo defensiva por si llega una colección antigua.
    /// </summary>
    private int ResolveScreenIndexFor(string id)
    {
        return ScreenIndexOfFeature(id);
    }

    /// <summary>Modo de contenido de una funcionalidad: lo declara su ficha (IslandWindow.FeatureCards.cs).</summary>
    private IslandContentMode ModeForFeature(string id) =>
        FeatureCard(id)?.Mode ?? IslandContentMode.Media;

    /// <summary>
    /// ¿La funcionalidad está en alguna pantalla? Sin pantalla no hay vista: una
    /// funcionalidad que se quedó fuera de todas (p. ej. al borrar su pantalla) no se
    /// muestra aunque esté activa.
    /// </summary>
    private bool FeatureInAnyScreen(IIslandFeature feature) => ResolveScreenIndexFor(feature.Id) >= 0;

    /// <summary>
    /// Una funcionalidad sin pantalla NO tiene vista (change island-pantallas)… salvo:
    /// <list type="bullet">
    /// <item>una EXCLUSIVA: la alerta final del temporizador necesita superficie para
    /// poder cerrarse (002 RF-2), y sin vista se quedaría bloqueando el contenedor sin
    /// forma de quitarla;</item>
    /// <item>un AVISO (<see cref="IslandFeatureIds.IsNotice"/>): su vista es una tarjeta
    /// temporal —dispositivo Bluetooth, cargador— que no tiene expandido ni entra en la
    /// navegación, así que no puede depender de que el usuario la haya colocado en una
    /// pantalla. Ése era el fallo: con un ajuste sin esa pantalla, el aviso se perdía.
    /// </item>
    /// </list>
    /// </summary>
    private static bool ScreenlessFeatureKeepsOwnView(IIslandFeature feature) =>
        feature.State.Exclusive || IslandFeatureIds.IsNotice(feature.Id);

    /// <summary>
    /// ¿La pantalla de la funcionalidad es COMBINADA ahora mismo? (varias
    /// funcionalidades usables). Entonces su EXPANDIDO no es la vista suelta de la
    /// funcionalidad, sino la pantalla entera: una columna por funcionalidad, de
    /// izquierda a derecha.
    /// </summary>
    private bool ScreenOfFeatureIsCombined(IIslandFeature feature)
    {
        int index = ResolveScreenIndexFor(feature.Id);
        return index >= 0 && ScreenUsableFeatures(_screens[index]).Count > 1;
    }

    /// <summary>
    /// Resuelve la presentación de una funcionalidad a su PANTALLA. Adopta su índice
    /// siempre (es la pantalla que el clic posterior abrirá) y, solo con el EXPANDIDO
    /// delante, reescribe el modo y el contenido a la composición de la pantalla: el
    /// compacto enseña UNA funcionalidad con su vista rica, nunca el grupo. Devuelve
    /// false si la funcionalidad no está en ninguna pantalla: sin pantalla no hay vista
    /// que presentar.
    /// </summary>
    private bool AdoptScreenFor(IIslandFeature feature, bool expanded, ref IslandContentMode mode, ref Action present)
    {
        int index = ResolveScreenIndexFor(feature.Id);
        if (index < 0) return false;
        _screenIndex = index;
        if (!expanded || mode == IslandContentMode.Screen) return true;
        if (ScreenUsableFeatures(_screens[index]).Count > 1)
        {
            mode = IslandContentMode.Screen;
            present = RefreshScreenMembers;
        }
        return true;
    }

    /// <summary>
    /// Funcionalidades registradas EN ORDEN DE PANTALLA (una entrada por funcionalidad
    /// con su posición): es el orden que sustituye a la antigua lista reordenable, tanto
    /// para desempatar la activa vigente como para cualquier recorrido del contenedor.
    /// Las que no están en ninguna pantalla se quedan fuera.
    /// </summary>
    private IEnumerable<(IIslandFeature Feature, int Order)> ScreenOrderedFeatures()
    {
        for (int i = 0; i < _screens.Count; i++)
        {
            for (int j = 0; j < _screens[i].Length; j++)
            {
                if (FeatureById(_screens[i][j]) is { } feature)
                    yield return (feature, i * IslandFeatureIds.MaxFeaturesPerScreen + j);
            }
        }
    }

    /// <summary>
    /// Funcionalidad que sostiene la vista vigente (null con una pantalla delante, en el
    /// reposo o cuando la vista es de una funcionalidad desconocida).
    /// </summary>
    private IIslandFeature? ViewOwnerFeature() =>
        FeatureCardOfMode(_contentMode) is { } card ? FeatureById(card.Id) : null;

    /// <summary>
    /// Funcionalidad que el usuario tiene DELANTE en el COMPACTO (null con el expandido
    /// delante, en la pieza de reposo o sin caja): es la que decide qué pantalla abre el
    /// clic, porque el compacto enseña una sola funcionalidad.
    /// </summary>
    private IIslandFeature? ShownCompactFeature() =>
        IsBoxShown && !_expanded && !_inactiveShown ? ViewOwnerFeature() : null;

    /// <summary>Pantallas con algo usable ahora mismo: es la unidad de navegación (RF-4).</summary>
    private int UsableScreenCount() => _screens.Count(screen => ScreenUsableFeatures(screen).Count > 0);

    /// <summary>¿La pantalla vigente contiene esta funcionalidad?</summary>
    private bool CurrentScreenContains(string id) => CurrentScreenIds().Contains(id);

    /// <summary>
    /// ¿La vista vigente enseña esta funcionalidad? Con una PANTALLA delante la enseñan
    /// sus miembros (una pantalla combinada puede tener varias a la vez); con una vista
    /// rica, solo la suya. Es el portero de los refrescos «pinta lo que ya está delante»
    /// de cada funcionalidad.
    /// </summary>
    private bool ViewShowsFeature(string id) =>
        _contentMode == IslandContentMode.Screen ? CurrentScreenContains(id) : ViewOwnerFeature()?.Id == id;

    /// <summary>
    /// ¿La vista vigente es el EXPANDIDO de una pantalla que contiene la música? Entonces
    /// la composición la manda la pantalla y un evento de música solo repinta su
    /// contenido: no cambia el modo ni las capas (si lo hiciera, el expandido saltaría
    /// de las columnas a la vista musical suelta y la pantalla desaparecería).
    /// </summary>
    private bool ScreenOwnsMediaView() =>
        _contentMode == IslandContentMode.Screen && CurrentScreenContains(IslandFeatureIds.Media);

    /// <summary>
    /// La vista vigente perdió a uno de sus miembros (se apagó una funcionalidad, se le
    /// cerró la sesión…): si era una PANTALLA, se recompone con los que quedan —y vuelve
    /// a la vista rica de su única funcionalidad si solo queda una—. Devuelve true si la
    /// pantalla ya resolvió la vista (nada más que replantear).
    /// </summary>
    private bool RecoverScreensAfterMemberLost()
    {
        if (_contentMode != IslandContentMode.Screen) return false;
        ApplyContentVisibility();
        SyncMeasuredHeight();
        return true;
    }

    /// <summary>
    /// Presenta el COMPACTO de la funcionalidad dada dentro de SU pantalla: adopta la
    /// pantalla —que es la que abrirá un clic posterior— y enseña la vista rica de ESA
    /// funcionalidad, porque el compacto muestra una sola. Es el punto por el que las
    /// rutas de «activa vigente» devuelven a la vista lo que está activo. Sin pantalla
    /// (la funcionalidad se quedó fuera de todas) no presenta nada y devuelve false.
    /// </summary>
    private bool ShowScreenOfFeature(IIslandFeature feature)
    {
        int index = ResolveScreenIndexFor(feature.Id);
        if (index < 0) return false;
        _screenIndex = index;
        return feature.TryShowCompact();
    }

    /// <summary>
    /// Presenta el compacto de la pantalla vigente: la vista rica de UNA sola de sus
    /// funcionalidades (la que sostiene la vista), nunca el grupo entero.
    /// </summary>
    private bool ShowCurrentScreenCompact()
    {
        var member = CompactMemberOfCurrentScreen();
        return member != null && member.TryShowCompact();
    }

    /// <summary>
    /// Funcionalidad que ocupa el COMPACTO de la pantalla vigente: el compacto enseña
    /// UNA sola —la primera que sostiene la vista (música reproduciendo, temporizador
    /// contando, un aviso vivo) y, si ninguna la sostiene, la primera usable—, y esa es
    /// la que el clic abre (su pantalla entera).
    /// </summary>
    private IIslandFeature? CompactMemberOfCurrentScreen()
    {
        var members = CurrentScreenFeatures();
        foreach (var member in members)
        {
            if (SingleFeatureSustainsView(member)) return member;
        }
        return members.Count > 0 ? members[0] : null;
    }

    /// <summary>
    /// Ancho de reposo del compacto de la pantalla vigente: el que declara la
    /// funcionalidad que lo ocupa (el compacto nunca agrupa varias).
    /// </summary>
    private double CompactWidthOfCurrentScreen() =>
        CompactMemberOfCurrentScreen() is { } member ? Math.Clamp(member.CompactWidth, 160, 360) : CompactPillWidth;

    /// <summary>
    /// Expande la pantalla vigente: la vista rica de su única funcionalidad si es
    /// simple, y la pantalla ENTERA —sus columnas, de IZQUIERDA A DERECHA— si es
    /// combinada. Es lo que se ve al hacer clic en el compacto. Una pantalla combinada
    /// sin columnas que enseñar (solo avisos que viven en el compacto) no abre nada:
    /// devolver false deja paso a la siguiente pantalla que sí pueda.
    /// </summary>
    private bool ExpandCurrentScreen()
    {
        var members = CurrentScreenFeatures();
        if (members.Count == 0) return false;
        if (members.Count == 1) return members[0].TryShowExpanded();
        if (CurrentScreenColumnCount() == 0) return false;
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
    /// El ajuste de pantallas cambió (o se apagó una funcionalidad, o la funcionalidad
    /// vigente entró/salió de una pantalla): se relee la configuración y, si el Island
    /// está a la vista, se vuelve a presentar la vista vigente —compacto o expandido—
    /// para que el cambio se VEA en el acto y el contenedor se adapte (ancho y alto del
    /// contenido nuevo). Oculto no se despliega nada: la próxima aparición ya usa la
    /// configuración nueva.
    /// </summary>
    public void RefreshScreensContent() => Dispatcher.Invoke(() =>
    {
        ApplyScreens();
        if (!IsBoxShown || _disposed) return;
        // La vista vigente se re-presenta cuando es una PANTALLA (pudo cambiar su
        // composición) o cuando es la vista de una funcionalidad que se quedó sin
        // pantalla (sin pantalla no hay vista). El compacto de una funcionalidad no
        // cambia porque su pantalla pase a ser combinada: sigue siendo su vista rica.
        var owner = ViewOwnerFeature();
        bool repaint = _contentMode == IslandContentMode.Screen
            || (owner != null && !FeatureInAnyScreen(owner));
        if (repaint)
        {
            if (owner != null && FeatureInAnyScreen(owner)) _screenIndex = ResolveScreenIndexFor(owner.Id);
            if (MoveToNearestUsableScreen())
            {
                if (_expanded ? ExpandCurrentScreen() : ShowCurrentScreenCompact()) return;
            }
            ShowInactiveOrHidden();
            return;
        }
        // Otra vista delante (un aviso, el reposo): solo se re-mide y se recoloca la
        // caja, que es lo que el ajuste de pantallas puede cambiarle.
        SyncMeasuredHeight();
        PositionTopCenter();
    });

    /// <summary>
    /// Repinta el contenido de todos los miembros de la pantalla vigente: es el
    /// contenido del EXPANDIDO de una pantalla combinada (sus columnas).
    /// </summary>
    private void RefreshScreenMembers()
    {
        foreach (var member in CurrentScreenFeatures()) RefreshMemberContent(member.Id);
    }

    /// <summary>
    /// Refresco periódico de una pantalla combinada que está delante: en el expandido,
    /// cuenta atrás del temporizador, cuentas del calendario y seek de la música (cada
    /// columna envejece con su cadencia). El compacto no pasa por aquí: enseña una sola
    /// funcionalidad y se refresca por la ruta de esa funcionalidad.
    /// </summary>
    private void RefreshCombinedScreenTick()
    {
        if (_disposed || !IsBoxShown || _contentMode != IslandContentMode.Screen) return;
        if (!_expanded) return;
        // Cada columna envejece con su cadencia: la declara su ficha.
        foreach (var member in CurrentScreenFeatures()) FeatureCard(member.Id)?.Tick?.Invoke();
    }

    /// <summary>Refresca los datos de una funcionalidad dentro de una pantalla combinada.</summary>
    private void RefreshMemberContent(string id) => FeatureCard(id)?.Refresh();

    /// <summary>
    /// Columnas que ocupará el expandido de la pantalla vigente: una por funcionalidad
    /// con paneles propios. Las que solo viven en el compacto (Bluetooth, cargador) no
    /// cuentan: su ancho no se reserva.
    /// </summary>
    private int CurrentScreenColumnCount() => CurrentScreenFeatures().Count(m => ColumnFor(m.Id) != null);

    // ------------------------------------------------------------------
    // Visibilidad de las capas de una pantalla combinada
    // ------------------------------------------------------------------

    /// <summary>
    /// Deja a la vista SOLO lo que compone el EXPANDIDO de la pantalla vigente: los
    /// paneles del expandido de cada miembro, en columnas de izquierda a derecha. Todo
    /// lo demás se apaga (una pantalla combinada no puede enseñar una capa de una
    /// funcionalidad que no la compone). El compacto no pasa por aquí: enseña una sola
    /// funcionalidad con su vista rica.
    /// </summary>
    private void ApplyScreenLayerVisibility()
    {
        var members = CurrentScreenFeatures();
        // Pantalla simple (o sin miembros usables): los paneles vuelven a su sitio.
        if (members.Count <= 1) RestoreExpandedHomes();
        HideAllContentLayers();
        // El expandido va de IZQUIERDA A DERECHA: los paneles de cada miembro se
        // trasladan a su columna, en el orden de la pantalla (RF-2/RF-3).
        ComposeScreenExpandedRow(members);
        foreach (var member in members) ShowMemberPanels(member.Id);
        UpdateArrows();
    }

    /// <summary>
    /// Enseña los paneles del expandido que pertenecen a una funcionalidad, con las
    /// mismas reglas que su pantalla simple: la alerta del temporizador manda sobre sus
    /// reels y el seek de música se ajusta a las capacidades de la sesión. Lo declara su
    /// ficha (IslandWindow.FeatureCards.cs).
    /// </summary>
    private void ShowMemberPanels(string id) => FeatureCard(id)?.ShowExpanded();

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
    /// contenedor a la vez, así que se traslada, no se copia). Los declara su ficha.
    /// </summary>
    private IEnumerable<UIElement> MemberPanels(string id) => FeatureCard(id)?.Expanded ?? [];

    /// <summary>Columna del expandido que aloja los paneles de una funcionalidad (null si no tiene).</summary>
    private StackPanel? ColumnFor(string id) => FeatureCard(id)?.Column();

    /// <summary>
    /// Dónde vivía cada panel antes de entrar en una columna: devolverlo a su sitio
    /// es lo que permite volver a las vistas simples sin duplicar paneles ni
    /// reordenar el expandido a mano.
    /// </summary>
    private readonly Dictionary<UIElement, (Panel Parent, int Index)> _expandedHomes = [];

    /// <summary>
    /// Compone el expandido de una pantalla combinada: una columna por funcionalidad,
    /// de IZQUIERDA A DERECHA en el orden de la pantalla, con los paneles de cada una
    /// dentro. Las columnas se COLOCAN en ese orden (el orden del XAML no manda: una
    /// pantalla «timer+media» enseña el temporizador a la izquierda) y las que no
    /// componen la pantalla se aparcan al final, ocultas. El ancho de cada columna se
    /// reparte para que todas quepan enteras en la caja (el ancho total es dinámico: lo
    /// fija el número de columnas).
    /// </summary>
    private void ComposeScreenExpandedRow(List<IIslandFeature> members)
    {
        RestoreExpandedHomes();
        var columns = new List<(string Id, StackPanel Host)>();
        foreach (var member in members)
        {
            if (ColumnFor(member.Id) is { } host) columns.Add((member.Id, host));
        }
        // Las columnas usadas van delante, en el orden de la pantalla; detrás quedan
        // (ocultas) las demás, para no perder ninguna referencia del XAML.
        ScreenExpandedRow.Children.Clear();
        foreach (var (_, host) in columns) ScreenExpandedRow.Children.Add(host);
        foreach (var column in AllScreenColumns())
        {
            if (!columns.Any(c => ReferenceEquals(c.Host, column))) ScreenExpandedRow.Children.Add(column);
        }
        double columnWidth = ScreenColumnWidthForCount(columns.Count);
        foreach (var column in AllScreenColumns())
        {
            bool used = columns.Any(c => ReferenceEquals(c.Host, column));
            column.Visibility = used ? Visibility.Visible : Visibility.Collapsed;
            column.Width = used ? columnWidth : double.NaN;
        }
        ScreenExpandedRow.Visibility = columns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, host) in columns)
        {
            foreach (var panel in MemberPanels(id)) MoveToColumn(panel, host);
        }
    }

    /// <summary>
    /// Ancho de cada columna del expandido de una pantalla combinada: se reparte el
    /// ancho disponible (el de la caja, ya clavado al monitor) entre las columnas que se
    /// van a ver, para que la última no quede recortada por el borde.
    /// </summary>
    private double ScreenColumnWidthForCount(int columns)
    {
        if (columns <= 0) return ScreenColumnWidth;
        double available = ScreenExpandedWidthForMembers(columns) - ScreenRowPadding
            - Math.Max(0, columns - 1) * ScreenColumnGap;
        return Math.Clamp(available / columns, 150, ScreenColumnWidth);
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
            RestoreScreenColumnOrder();
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
        RestoreScreenColumnOrder();
        foreach (var column in AllScreenColumns())
        {
            column.Children.Clear();
            column.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Devuelve las columnas del expandido a su orden del XAML: componer una pantalla
    /// las reordena (izquierda a derecha según la pantalla) y fuera de ella se aparcan
    /// en su sitio.
    /// </summary>
    private void RestoreScreenColumnOrder()
    {
        var canonical = AllScreenColumns().ToList();
        if (ScreenExpandedRow.Children.Count == canonical.Count
            && canonical.All(column => ScreenExpandedRow.Children.Contains(column)))
        {
            ScreenExpandedRow.Children.Clear();
            foreach (var column in canonical) ScreenExpandedRow.Children.Add(column);
        }
    }

    /// <summary>
    /// Ancho del expandido de una pantalla combinada: dinámico, una columna por
    /// funcionalidad visible en el expandido, sin pasarse del monitor.
    /// </summary>
    private double ScreenExpandedWidthForMembers(int columns)
    {
        // Cada columna lleva su separación a la derecha (el margen del XAML), así que
        // el ancho necesario es una columna + su separación por cada columna, más el
        // relleno lateral del contenido. Si faltara ese último margen, la última
        // columna quedaría recortada por el borde de la caja.
        double total = columns * (ScreenColumnWidth + ScreenColumnGap) + ScreenRowPadding;
        var primary = PrimaryMonitor();
        double monitorWidth = primary.dpiX > 0 ? primary.workArea.Width * 96.0 / primary.dpiX : ScreenExpandedMaxWidth;
        return Math.Clamp(total, 280, Math.Min(ScreenExpandedMaxWidth, Math.Max(280, monitorWidth - 24)));
    }
}
