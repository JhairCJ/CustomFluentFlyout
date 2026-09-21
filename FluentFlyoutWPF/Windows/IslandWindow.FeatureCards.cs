// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// FICHA de una funcionalidad del contenedor (change island-fichas): todo lo que el
/// contenedor sabe de ella, en UNA declaración —su modo de contenido, su tarjeta
/// compacta, sus paneles del expandido, la columna en la que vive dentro de una
/// pantalla combinada, cómo se repinta, cómo envejece sus cuentas mientras está a la
/// vista, cuándo su modo es presentable y cuándo sostiene la vista por sí sola—.
///
/// <para>Antes ese mismo dato estaba repartido en nueve despachos por id
/// (<c>ModeForFeature</c>, <c>ViewOwnerFeature</c>, <c>MemberPanels</c>,
/// <c>ColumnFor</c>, <c>RefreshMemberContent</c>, <c>ShowMemberPanels</c>,
/// <c>RefreshCombinedScreenTick</c>, <c>SingleFeatureSustainsView</c>,
/// <c>ResolveActiveVigente</c>) y cuatro listas paralelas: añadir una funcionalidad
/// eran decenas de puntos de edición y bastaba olvidar uno para que su vista se
/// perdiera en silencio. Ahora es una entrada de la tabla y nada más.</para>
/// </summary>
internal sealed record IslandFeatureCard(
    string Id,
    IslandContentMode Mode,
    /// <summary>Tarjeta compacta (siempre existe: el compacto enseña una sola ficha).</summary>
    FrameworkElement Compact,
    /// <summary>Paneles del expandido: los que se encienden con ella y se mueven a su columna.</summary>
    UIElement[] Expanded,
    /// <summary>Columna del expandido de una pantalla combinada (null = sin columna propia).</summary>
    Func<StackPanel?> Column,
    /// <summary>Enciende sus paneles del expandido (con capacidades, alerta o fundido si los tiene).</summary>
    Action ShowExpanded,
    /// <summary>Apaga sus paneles del expandido.</summary>
    Action HideExpanded,
    /// <summary>Repinta su contenido con los datos de ahora.</summary>
    Action Refresh,
    /// <summary>Envejece sus cuentas mientras su vista está delante (null = no tiene).</summary>
    Action? Tick,
    /// <summary>¿Su MODO es presentable ahora mismo? (no es lo mismo que «usable»)</summary>
    Func<bool> ModeAvailable,
    /// <summary>¿Sostiene la vista por sí sola? (actividad propia: reproduce, cuenta, avisa)</summary>
    Func<bool> Sustains,
    /// <summary>
    /// Actividad PROPIA de la funcionalidad —reproducir, contar— para «Visible mientras
    /// activo» (null = la declara su contrato, como una funcionalidad futura).
    /// </summary>
    Func<bool>? OwnActivity = null,
    /// <summary>Medio del compacto que el modo ultra aparta (null = su compacto no lleva medio).</summary>
    UIElement? Middle = null,
    /// <summary>Cómo vuelve ese medio a su sitio al apagar el modo ultra (null = siempre visible).</summary>
    Func<Visibility>? RestoreMiddle = null);

/// <summary>
/// Tabla de fichas del contenedor: la ÚNICA fuente de lo que cada funcionalidad
/// necesita del contenedor. Se construye una vez, cuando ya existen los elementos del
/// XAML, y de ella salen todos los despachos que antes eran switches.
/// </summary>
public partial class IslandWindow
{
    private readonly Dictionary<string, IslandFeatureCard> _featureCards = new(StringComparer.Ordinal);
    private readonly Dictionary<IslandContentMode, string> _featureIdByMode = [];

    /// <summary>Enciende los elementos dados (la visibilidad la manda el contenedor).</summary>
    private static Action Show(params UIElement[] elements) => () =>
    {
        foreach (var element in elements) element.Visibility = Visibility.Visible;
    };

    /// <summary>Apaga los elementos dados.</summary>
    private static Action Hide(params UIElement[] elements) => () =>
    {
        foreach (var element in elements) element.Visibility = Visibility.Collapsed;
    };

    /// <summary>Ficha de una funcionalidad por su id (null si el id no está registrado).</summary>
    private IslandFeatureCard? FeatureCard(string id) =>
        _featureCards.TryGetValue(id, out var card) ? card : null;

    /// <summary>Ficha del contenido vigente (null con una pantalla combinada, que no es una funcionalidad).</summary>
    private IslandFeatureCard? FeatureCardOfMode(IslandContentMode mode) =>
        _featureIdByMode.TryGetValue(mode, out var id) ? FeatureCard(id) : null;

    private void RegisterCard(IslandFeatureCard card)
    {
        _featureCards[card.Id] = card;
        _featureIdByMode[card.Mode] = card.Id;
    }

    /// <summary>
    /// Construye la tabla de fichas. Es el inventario completo de funcionalidades del
    /// contenedor: añadir una es añadir una entrada aquí (más su vista y su contenido),
    /// sin tocar ninguna regla del contenedor.
    /// </summary>
    private void BuildFeatureCards()
    {
        // --- música: la capa de siempre (comparte contenedor con el temporizador) ---
        RegisterCard(new IslandFeatureCard(
            IslandFeatureIds.Media, IslandContentMode.Media,
            Compact: MusicCompactGrid,
            Expanded: [MusicExpandedTop, SeekRow, ControlsRow],
            Column: () => ScreenColumnMedia,
            ShowExpanded: () =>
            {
                MusicExpandedTop.Visibility = Visibility.Visible;
                ControlsRow.Visibility = Visibility.Visible;
                SeekRow.Visibility = Visibility.Visible;
                // Las capacidades de la sesión deciden el seek y los botones; sin
                // sesión que presentar la fila no se deja visible (001 MOD RF-9).
                if (Current() is { } session) ApplyCapabilities(session);
                else SeekRow.Visibility = Visibility.Collapsed;
            },
            HideExpanded: Hide(MusicExpandedTop, SeekRow, ControlsRow),
            Refresh: () =>
            {
                if (Current() is { } session) RefreshUi(session);
            },
            Tick: () =>
            {
                if (Current() is { } session) UpdateSeek(session);
            },
            ModeAvailable: () => true,
            Sustains: IsMediaActiveForContract,
            OwnActivity: IsMediaActiveForContract,
            Middle: CompactTitle,
            RestoreMiddle: () => Visibility.Visible));

        // --- temporizador: la otra cara de esa capa, con su alerta exclusiva ---
        RegisterCard(new IslandFeatureCard(
            IslandFeatureIds.Timer, IslandContentMode.Timer,
            Compact: TimerCompactGrid,
            Expanded: [TimerAlert, TimerExpanded, TimerRunPanel],
            Column: () => ScreenColumnTimer,
            ShowExpanded: () =>
            {
                bool alert = _timer.State == Classes.IslandTimerState.Alerting;
                bool idle = _timer.State == Classes.IslandTimerState.Idle;
                // La alerta es instantánea; los reels y el panel de marcha se
                // funden solo al entrar (FadeInPanel).
                TimerAlert.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
                CrossfadeTimerPanels(showConfig: idle && !alert, showRun: !idle && !alert);
            },
            HideExpanded: Hide(TimerAlert, TimerExpanded, TimerRunPanel),
            Refresh: RefreshTimerUI,
            Tick: RefreshTimerUI,
            ModeAvailable: TimerModeAvailable,
            // En «Visible mientras activo» una cuenta pausada no sostiene el compacto
            // (002 MOD RF-7); en «Aviso temporal» basta con seguir vivo.
            Sustains: () => SettingsManager.Current.IslandVisibilityMode == 0
                ? IsTimerActiveForCompact()
                : TimerKeepsAlive(),
            OwnActivity: IsTimerActiveForCompact,
            Middle: TimerProgressZone,
            RestoreMiddle: () => SettingsManager.Current.IslandTimerShowProgress && !UltraCompactOn
                ? Visibility.Visible
                : Visibility.Collapsed));

        // --- cajón de aplicaciones ---
        RegisterCard(new IslandFeatureCard(
            IslandFeatureIds.Apps, IslandContentMode.Apps,
            Compact: AppsCompactGrid,
            Expanded: [AppsExpanded],
            Column: () => ScreenColumnApps,
            ShowExpanded: Show(AppsExpanded),
            HideExpanded: Hide(AppsExpanded),
            Refresh: RefreshAppList,
            Tick: null,
            ModeAvailable: AppsModeAvailable,
            Sustains: AppsKeepsView));

        // --- estante de archivos ---
        RegisterCard(new IslandFeatureCard(
            IslandFeatureIds.Shelf, IslandContentMode.Shelf,
            Compact: ShelfCompactGrid,
            Expanded: [ShelfExpanded],
            Column: () => ScreenColumnShelf,
            ShowExpanded: Show(ShelfExpanded),
            HideExpanded: Hide(ShelfExpanded),
            Refresh: RefreshShelfList,
            Tick: null,
            ModeAvailable: ShelfModeAvailable,
            Sustains: ShelfKeepsView));

        // --- recordatorios de calendario ---
        RegisterCard(new IslandFeatureCard(
            IslandFeatureIds.Calendar, IslandContentMode.Calendar,
            Compact: CalendarCompactGrid,
            Expanded: [CalendarExpanded],
            Column: () => ScreenColumnCalendar,
            ShowExpanded: Show(CalendarExpanded),
            HideExpanded: Hide(CalendarExpanded),
            Refresh: RefreshCalendarList,
            Tick: RefreshCalendarList,
            ModeAvailable: CalendarModeAvailable,
            Sustains: CalendarKeepsView,
            Middle: CalendarCompactTitle));

        // --- dispositivo Bluetooth conectado: AVISO, sin expandido ---
        RegisterCard(new IslandFeatureCard(
            IslandFeatureIds.Bluetooth, IslandContentMode.Bluetooth,
            Compact: BluetoothCompactGrid,
            Expanded: [],
            Column: () => null,
            ShowExpanded: () => { },
            HideExpanded: () => { },
            Refresh: RefreshBluetoothUI,
            Tick: null,
            ModeAvailable: BluetoothModeAvailable,
            Sustains: BluetoothActive,
            Middle: BluetoothName));

        // --- portapapeles ---
        RegisterCard(new IslandFeatureCard(
            IslandFeatureIds.Clipboard, IslandContentMode.Clipboard,
            Compact: ClipboardCompactGrid,
            Expanded: [ClipboardExpanded],
            Column: () => ScreenColumnClipboard,
            ShowExpanded: () =>
            {
                ClipboardExpanded.Visibility = Visibility.Visible;
                RefreshClipboardViews();
            },
            HideExpanded: Hide(ClipboardExpanded),
            Refresh: RefreshClipboardViews,
            Tick: null,
            ModeAvailable: ClipboardModeAvailable,
            // El portapapeles no tiene actividad propia: lo declara su contrato.
            Sustains: () => FeatureById(IslandFeatureIds.Clipboard)?.State.Active == true));

        // --- clima del lugar configurado ---
        RegisterCard(new IslandFeatureCard(
            IslandFeatureIds.Weather, IslandContentMode.Weather,
            Compact: WeatherCompactGrid,
            Expanded: [WeatherExpanded],
            Column: () => ScreenColumnWeather,
            ShowExpanded: () =>
            {
                WeatherExpanded.Visibility = Visibility.Visible;
                RefreshWeatherUI();
            },
            HideExpanded: Hide(WeatherExpanded),
            Refresh: RefreshWeatherUI,
            Tick: null,
            ModeAvailable: WeatherModeAvailable,
            Sustains: WeatherKeepsView));

        // --- cargador del equipo: AVISO, sin expandido ---
        RegisterCard(new IslandFeatureCard(
            IslandFeatureIds.Power, IslandContentMode.Power,
            Compact: PowerCompactGrid,
            Expanded: [],
            Column: () => null,
            ShowExpanded: () => { },
            HideExpanded: () => { },
            Refresh: RefreshPowerUI,
            Tick: null,
            ModeAvailable: PowerModeAvailable,
            Sustains: PowerActive,
            Middle: PowerTitle));
    }

    // ------------------------------------------------------------------
    // Punto de partida y presentación de capas
    // ------------------------------------------------------------------

    /// <summary>
    /// Punto de partida de cualquier vista: NINGUNA capa a la vista (ni compactas ni
    /// paneles del expandido). Cada vista enciende después SOLO lo suyo, así ninguna
    /// deja puesta la capa de la anterior —el fallo clásico era el estante sin apagar
    /// el calendario o el calendario sin apagar el portapapeles: dos vistas
    /// superpuestas—.
    /// </summary>
    private void HideAllContentLayers()
    {
        foreach (var card in _featureCards.Values) card.Compact.Visibility = Visibility.Collapsed;
        HideAllExpandedPanels();
    }

    /// <summary>Apaga los paneles del expandido de todas las funcionalidades.</summary>
    private void HideAllExpandedPanels()
    {
        foreach (var card in _featureCards.Values) card.HideExpanded();
    }

    /// <summary>
    /// Presenta la CARA del contenido vigente: su tarjeta compacta y sus paneles.
    /// El compacto enseña una sola funcionalidad (agrupar es cosa del expandido de una
    /// pantalla), así que esto es exactamente «lo que se ve» de la vista vigente.
    /// </summary>
    private void ShowContentFace(IslandFeatureCard card)
    {
        HideAllContentLayers();
        card.Compact.Visibility = Visibility.Visible;
        card.ShowExpanded();
    }

    /// <summary>
    /// ¿Esta funcionalidad, ella sola, sostiene la vista? Su actividad propia la declara
    /// su ficha; una funcionalidad futura sin ficha la declara su contrato.
    /// </summary>
    private bool SingleFeatureSustainsView(IIslandFeature feature) =>
        FeatureCard(feature.Id)?.Sustains() ?? feature.State.Active;
}
