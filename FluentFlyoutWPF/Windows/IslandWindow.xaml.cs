// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls.TaskbarWidget;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text;
using Windows.Media.Control;
using static WindowsMediaController.MediaManager;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Fluent Island: contenedor escalable superior central con estados inactivo,
/// compacto y expandido. Aloja funcionalidades registradas (media,
/// temporizador, cajón de aplicaciones) bajo un contrato común —habilitada/
/// disponible/activa/seleccionada + vistas + dimensiones + exclusiva— (001 RF-11, RF-25).
///
/// <para>Este archivo es el ESTADO y el CICLO DE VIDA: constantes de
/// dimensiones, campos compartidos por todos los partials, arranque/cierre,
/// contrato del contenedor (registro de funcionalidades y resolución de qué se
/// muestra) y los puntos de entrada que los ajustes usan en caliente. El resto
/// vive en partials vecinos, uno por responsabilidad:</para>
///
/// <list type="bullet">
/// <item><c>IslandWindow.Views.cs</c> — banda de contenido vigente
/// (<see cref="IslandContentMode"/>) y las rutas ÚNICAS de presentación en
/// compacto y expandido que las tres funcionalidades comparten.</item>
/// <item><c>IslandWindow.Media.cs</c> — sesiones multimedia, snapshot
/// presentado, carátula, capacidades, seek y controles.</item>
/// <item><c>IslandWindow.States.cs</c> — máquina de estados: reposo, aviso
/// temporal, repliegues y expansión.</item>
/// <item><c>IslandWindow.Hover.cs</c> — detección de puntero, franja de
/// tolerancia y clics de apertura.</item>
/// <item><c>IslandWindow.Presentation.cs</c> — estilo, tipografía, línea de
/// actividad, punto de estado y posición.</item>
/// <item><c>IslandWindow.Activity.cs</c> — buzón coalescido de actividad,
/// reconciliación única, cadencias por contenido y contexto por eventos.</item>
/// <item><c>IslandWindow.Monitoring.cs</c> — supresión contextual cacheada y
/// ecualizador condicionado a reproducción visible.</item>
/// <item><c>IslandWindow.Frame.cs</c> — motor de animación por frame y
/// geometría del contenedor.</item>
/// <item><c>IslandWindow.Background.cs</c> — fondo de álbum difuminado y
/// giratorio.</item>
/// <item><c>IslandWindow.Timer.cs</c> — funcionalidad temporizador.</item>
/// <item><c>IslandWindow.Apps.cs</c> — funcionalidad cajón de aplicaciones.</item>
/// <item><c>IslandWindow.Shelf.cs</c> — funcionalidad estante de archivos.</item>
/// <item><c>IslandWindow.Calendar.cs</c> — funcionalidad recordatorios de
/// calendario.</item>
/// <item><c>IslandWindow.Bluetooth.cs</c> — funcionalidad dispositivos Bluetooth
/// conectados (aviso siempre temporal).</item>    /// <item><c>IslandWindow.Dictation.cs</c> — dictado por voz (spec 006): tarjeta
    /// con micrófono a la izquierda y ondas a la derecha, y el ciclo de la sesión
    /// mientras se mantiene la tecla.</item>
    /// <item><c>IslandFeatures.cs</c> — contrato de funcionalidades y registro.</item>
/// </list>
/// </summary>
public partial class IslandWindow : Window
{
    // --- Dimensiones del contenedor (001 MOD RF-11, RF-15, RF-25) ---

    /// <summary>Ancho del lienzo transparente de la ventana (crece con el contenido: <see cref="PositionTopCenter"/>).</summary>
    private const double DefaultWindowWidth = 640;
    private const int DefaultExpandedIslandWidth = 320;
    private const int DefaultExpandedIslandHeight = 126;
    // Estados del contenedor (001 MOD RF-11): compacto estándar y la pieza
    // inactiva (negra, más estrecha que el compacto, sin contenido).
    private const double CompactPillWidth = 240;
    // El estilo notch es más estrecho por diseño (001 RF-15).
    private const double NotchCompactWidth = 200;
    // Ancho del reposo inactivo (001 MOD RF-11, RF-16): la pieza negra estrecha.
    // Es a propósito la MISMA en los dos estilos —no deriva del compacto—, porque
    // el reposo es una presencia mínima, no una cápsula alargada.
    private const double InactivePillWidth = 112;
    // Ancho de la línea de actividad (y base de la franja de detección).
    private const double LineFullWidth = 120;

    // Sin latido global: la actividad llega por el buzón coalescido de
    // IslandWindow.Activity.cs (001 MOD RF-1/RF-16, ADDED RF-3) y cada refresco
    // vive solo mientras su contenido lo necesita. Aquí no hay ningún ciclo
    // permanente de 40–200 ms.

    private double ExpandedIslandWidth => Math.Clamp(
        SettingsManager.Current.IslandExpandedWidth > 0 ? SettingsManager.Current.IslandExpandedWidth : DefaultExpandedIslandWidth,
        280,
        600);
    private double ExpandedIslandHeight => Math.Clamp(
        SettingsManager.Current.IslandExpandedHeight > 0 ? SettingsManager.Current.IslandExpandedHeight : DefaultExpandedIslandHeight,
        100,
        220);

    // Dimensiones efectivas del contenido seleccionado (001 MOD RF-25): la
    // funcionalidad puede pedir ancho/alto propios y el contenedor los respeta;
    // con 0 (lo que hacen media y temporizador) valen el ancho configurado común
    // y la altura medida del contenido. Así una funcionalidad futura con otra
    // geometría no necesita tocar el motor del contenedor.
    private double ContentExpandedWidth => _contentMode == IslandContentMode.Screen && ScreenIsCombined()
        // Pantalla combinada: una columna por funcionalidad, de izquierda a derecha, y
        // el ancho es DINÁMICO (lo fija el número de columnas; change island-pantallas RF-3).
        ? ScreenExpandedWidthForMembers(CurrentScreenColumnCount())
        : _selectedFeature is { ExpandedPreferredWidth: > 0 } f
            ? Math.Clamp(f.ExpandedPreferredWidth, 200, 600)
            : ExpandedIslandWidth;
    private double ContentExpandedHeight => _selectedFeature is { ExpandedPreferredHeight: > 0 } f
        ? Math.Clamp(f.ExpandedPreferredHeight, 80, 260)
        : ExpandedIslandHeight;
    /// <summary>
    /// Ancho de reposo (compacto) que declara el contenido seleccionado, con el modo
    /// ultra compacto por delante (entonces manda el ancho de sus dos extremos).
    /// </summary>
    private double ContentCompactWidth => RestCompactWidth(_contentMode == IslandContentMode.Screen
        // El compacto de una pantalla enseña UNA sola funcionalidad (change
        // island-pantallas): mide lo que ella declare. Solo se pasa por aquí en las
        // transiciones de repliegue, con la composición de columnas ya de salida.
        ? CompactWidthOfCurrentScreen()
        : _selectedFeature is { } f
            ? Math.Clamp(f.CompactWidth, 160, 360)
            : CompactPillWidth);
    /// <summary>Alto de reposo (compacto) que declara el contenido seleccionado.</summary>
    private double ContentCompactHeight => _selectedFeature is { } f
        ? Math.Clamp(f.CompactHeight, 28, 60)
        : 34;

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly Brush IslandBorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush MediaPlayingBrush = new SolidColorBrush(Color.FromRgb(0xB6, 0xF0, 0xB5));
    private static readonly Brush MediaPausedBrush = new SolidColorBrush(Color.FromRgb(0x76, 0x7B, 0x79));
    /// <summary>Borde del Island mientras se arrastra algo encima (estante).</summary>
    private static readonly Brush ShelfDropBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    // Hay algo aceptable arrastrándose sobre el Island: el borde se ilumina.
    private bool _shelfDropHot;

    // --- Estado compartido por todos los partials ---

    private readonly MainWindow _main;
    private readonly Dictionary<string, DateTime> _lastPlay = new();
    private readonly Dictionary<string, DateTime> _lastFeatureEvent = new();
    private string? _currentId;
    // Media session pinned by a direct Island interaction. This prevents an OS
    // focus move caused by pause/play/skip from changing what the Island controls.
    // A different session that genuinely starts playing releases the pin.
    private string? _mediaPinnedSessionId;
    private bool _expanded;
    private bool _drag;
    private readonly Visualizer _eq = new(Visualizer.Options.Island);
    private int _eqBars = -1;
    private bool _eqRunning;

    // Muelles: p morph 0..1 (compacto->expandido), q entrada 0..1 (oculto->visible)
    private double _p, _pT, _pv;
    private double _q, _qT, _qv;
    private bool _loopOn;
    private bool _hidingViaCompact; // salida directa desde expandido: p y q van a 0 a la vez
    private double _hexp = 172;
    private double _hexpShown = 172; // altura renderizada: glidea tras _hexp sin saltos
    private double _lineW = LineFullWidth;
    private string _lastTrackKey = "";
    private bool _popPlaying;
    private double _pop; // 0..1 pulso de cambio de pista
    private bool _albumArtHovering;
    private bool _hasAlbumCover;
    private BitmapImage? _displayedAlbumArt;
    private BitmapImage? _albumFlipArt; // última portada pedida por un volteo en curso
    private int _albumFlipVersion;
    private bool _albumFlipRunning;

    // Album-art background, matching the taskbar widget's blurred/rotating viewport.
    private BitmapImage? _backgroundIcon;
    private BitmapImage? _bakedIcon;
    private BitmapSource? _bakedBackground;
    private double _bakedSideDip;
    private int _bakedBlurRadius;
    private BitmapImage? _bakingIcon;
    private double _bakingSide;
    private int _bakingBlurRadius;
    private RotateTransform? _backgroundRotateTransform;
    private bool _backgroundRotationActive;
    private bool _backgroundRotationAnimationRunning;
    private bool _backgroundRotationPaused;
    private bool _backgroundRotationWasUp;
    private double _appliedRotationDurationSeconds;
    private int? _appliedDesiredFrameRate;
    private double _pausedRotationAngle;
    private BitmapSource? _backgroundCrossfadeTarget;
    private int _backgroundCrossfadeVersion;
    private int _backgroundGeneration;
    private bool _disposed;
    private bool _wasSuppressed;
    private Thickness _expandedMarginOrig;

    // --- Contrato del contenedor escalable (change island-contenedor-escalable) ---
    // Registro de funcionalidades (001 MOD RF-11, RF-13): media y temporizador
    // cumplen el contrato habilitada/disponible/activa/seleccionada + vistas +
    // dimensiones + exclusiva declarable. Futuras funcionalidades = otro registro.
    private readonly IslandFeatureRegistry _features = new();
    private IIslandFeature? _selectedFeature; // último-activo: funcionalidad en uso
    private bool _inactiveShown; // objetivo del reposo: la pieza inactiva (p=0, q=1)
    private bool _inactiveHot; // hover vivo sobre la pieza inactiva
    private double _inactiveHotT; // 0..1 micro-crecimiento del hover
    // Progreso animado hacia la pieza inactiva (0 = contenido, 1 = pieza).
    // La geometría (ancho de reposo) y el desvanecido del contenido comparten
    // este mismo reloj, así el paso compacto-con-contenido <-> inactivo es una
    // sola transición fluida en vez de un cambio de ancho + un borrado seco
    // (001 MOD RF-16). Punto único de entrada/salida: SetInactiveRest(true) y
    // EnterContent(), de modo que un repliegue con contenido NUNCA atraviesa la
    // geometría de la pieza (residuo a medio camino = se descarta).
    private double _inactiveT;  // 0..1 progreso real hacia la pieza inactiva
    private double _inactiveTt; // objetivo 0/1 de _inactiveT
    // Repliegue en dos fases (001 MOD RF-16): cuando el repliegue parte del
    // expandido CON contenido que debe seguir visible (la activa vigente), la fase
    // 1 (hacia la pieza inactiva) encadena la fase 2 (de la pieza al compacto) al
    // asentarse. Aquí vive la funcionalidad a la que hay que reabrir; null = el
    // repliegue va al reposo. La reapertura la resuelve TryReopenFromInactive() al
    // llegar a la pieza (o el tick, si la actividad llega más tarde).
    private IIslandFeature? _pendingCompactFeature;
    // El repliegue en curso partió de la geometría expandida: mientras el ancho
    // morfa hacia la pieza, el compacto no florece de paso —expandido → inactivo es
    // UNA sola transición (001 MOD RF-16)—.
    private bool _collapseFromExpanded;

    // --- Estado del aviso y del hover ---

    // Anti-reapertura por hover tras ocultar/colapsar la caja bajo el cursor.
    private DateTime _hoverSnoozeUntil = DateTime.MinValue;
    // Tolerancia al abandonar el expandido (001 MOD RF-4): hay una espera armada
    // para replegar cuando el puntero se fue, y la versión invalida el disparo
    // cuando el puntero vuelve antes de que venza.
    private bool _hoverLeavePending;
    private int _hoverLeaveVersion;
    // Aviso temporal (001 RF-2, 002 RF-16): vencimiento ABSOLUTO del aviso vigente
    // y versión que invalida los repliegues obsoletos. El plazo no se prolonga al
    // expandir, interactuar ni recibir datos: expandir solo lo POSPONE conservando
    // el tiempo restante, y al volver al compacto el aviso sigue cumpliendo su
    // plazo hasta replegarse a inactivo/nada.
    private DateTime _noticeUntil = DateTime.MinValue;
    private int _noticeVersion;
    // ¿Hay un chequeo de vencimiento del aviso armado ahora mismo? Lo consulta la
    // recuperación de 5 s para reparar un disparo perdido sin duplicar cadenas.
    private bool _noticeCheckActive;
    // El aviso vigente es FORZADO: vence también en «Visible mientras activo».
    // Es el de los contenidos que notifican siempre de forma temporal (dispositivos
    // Bluetooth, change island-bluetooth-conectado RF-1). Cualquier armado sin
    // force lo descarta, así la vista siguiente no hereda un vencimiento ajeno.
    private bool _noticeForced;
    // A qué VISTA pertenece el plazo vigente. El vencimiento es uno solo para todo el
    // contenedor, así que sin esta etiqueta un plazo armado por el temporizador (o por
    // la música) hacía que Bluetooth o el cargador se declararan «activos» y saltara su
    // aviso sin que hubiera pasado nada (change island-avisos).
    private IslandContentMode _noticeMode;
    // Aviso PENDIENTE del temporizador (002 RF-16): una acción que pone la cuenta
    // en marcha (empezar, reanudar, reiniciar) dentro del expandido deja su aviso
    // armado pero sin gastar. El plazo debe correr cuando el compacto del
    // temporizador se presenta de verdad —no mientras el usuario configura la
    // cuenta—, así el aviso se ve entero en vez de nacer vencido. Lo consume
    // ShowTimerCompact y lo descarta ClearTemporaryNotice.
    private bool _pendingTimerNotice;

    // --- Ciclo de vida ---

    public IslandWindow(MainWindow main)
    {
        _main = main;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        // Inventario de funcionalidades (change island-fichas): la ÚNICA fuente de lo
        // que el contenedor sabe de cada una. Va lo primero, con los elementos del
        // XAML ya creados, porque todo lo que sigue la consulta.
        BuildFeatureCards();
        InitTimer();
        InitApps();
        InitShelf();
        InitCalendar();
        InitBluetooth();
        InitClipboard();
        InitWeather();
        InitPower();
        InitDictation();
        // Contenedor escalable: media, temporizador, cajón de aplicaciones, estante de
        // archivos, recordatorios de calendario, dispositivos Bluetooth, portapapeles,
        // clima y cargador se registran una vez; el contrato decide qué se puede mostrar
        // (RF-11, RF-13) y las pantallas configuradas mandan el orden y la composición.
        _features.Register(new IslandMediaFeature(this));
        _features.Register(new IslandTimerFeature(this));
        _features.Register(new IslandAppsFeature(this));
        _features.Register(new IslandShelfFeature(this));
        _features.Register(new IslandCalendarFeature(this));
        _features.Register(new IslandBluetoothFeature(this));
        _features.Register(new IslandClipboardFeature(this));
        _features.Register(new IslandWeatherFeature(this));
        _features.Register(new IslandPowerFeature(this));
        _features.Register(new IslandDictationFeature(this));
        // Pantallas configuradas: son la ÚNICA fuente del orden y de la vista —el
        // contenedor navega por ellas y una pantalla puede llevar varias
        // funcionalidades juntas, de izquierda a derecha (change island-pantallas).
        ApplyScreens();
        // Migración del «Siempre en su lugar» (retirado, 001 REMOVED): un modo
        // guardado con el valor 2 pasa a «Visible mientras activo».
        if (SettingsManager.Current.IslandVisibilityMode is < 0 or > 1)
            SettingsManager.Current.IslandVisibilityMode = 0;
        _expandedMarginOrig = ExpandedLayer.Margin;
        ApplyAlbumArtRadius();
        CompactEq.Source = _eq.Bitmap;
        ExpandedEq.Source = _eq.Bitmap;
        ApplyStyle();
        ApplyIslandTextStyle();
        UpdateBackgroundMode();
        SyncMeasuredHeight();
        SnapFrame();
        Show();
        Visibility = Visibility.Visible;
        PositionTopCenter();
        // El Island sigue siempre las sesiones del sistema: es parte de su
        // función, independiente del toggle del Media Flyout (que solo
        // controla la ventana emergente de música). Sin sesión disponible el
        // contenido se reduce al temporizador (RF-13, RF-14).
        HookMediaEvents(true);
        // Actividad orientada a eventos: buzón coalescido, notificación nativa de
        // puntero, contexto por eventos de Windows y red de recuperación de 5 s
        // (001 MOD RF-1/RF-3/RF-12/RF-28). Sin latido de 200 ms ni poll de 40 ms.
        InitActivity();
        // Idioma en caliente: los textos del XAML se refrescan solos; los que escribe
        // el código los re-aplica este aviso (ver IslandWindow.Presentation.cs).
        LocalizationManager.LanguageChanged += OnLanguageChanged;
        SyncExistingMediaState();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowHelper.SetTopmost(this);
        PositionTopCenter();
        SyncMeasuredHeight();
        SnapFrame();
        // Reposo inicial resuelto ya en el arranque (001 MOD RF-2): el contenedor
        // nace con su vista decidida —activa vigente, pieza inactiva u oculto según
        // ajustes— en vez de quedarse la caja Collapsed del XAML hasta el primer
        // evento de media. Ése era el fallo: sin reproducción el Island no existía,
        // y por tanto no había forma de abrir el temporizador ni el cajón.
        //
        // Solo se toca si no hay nada a la vista: si el arranque ya adoptó una
        // reproducción en curso (SyncExistingMediaState), su vista manda. La caja se
        // hace visible ANTES de resolver —con q=0 la gobierna el frame, así que
        // entra con el revelado punto -> pieza— y sin residuos en el árbol.
        if (!SettingsManager.Current.IslandEnabled)
        {
            SnapHidden();
            Visibility = Visibility.Collapsed;
            return;
        }
        if (IsBoxShown) return;
        // Nada de residuos en el árbol antes de la primera aparición: sin evento de
        // media previo, la capa compacta conservaría del XAML su ecualizador y su
        // titular vacío, y el revelado los enseñaría un instante.
        ClearInactiveResidue();
        IslandBox.Visibility = Visibility.Visible;
        ShowInactiveOrHidden();
    }

    private void NotePlay(string id) { _lastPlay[id] = DateTime.Now; _lastFeatureEvent["media"] = DateTime.UtcNow; _currentId = id; }
    private void NoteFeatureEvent(string id) => _lastFeatureEvent[id] = DateTime.UtcNow;

    public void Dispose()
    {
        _disposed = true;
        LocalizationManager.LanguageChanged -= OnLanguageChanged;
        ShutdownActivity();
        ShutdownBluetooth();
        ShutdownClipboard();
        ShutdownWeather();
        ShutdownPower();
        ShutdownDictation();
        ClearTemporaryNotice();
        StopLoop();
        StopBackgroundRotation();
        _eq.Dispose();
        HookMediaEvents(false);
    }

    // ------------------------------------------------------------------
    // Contrato del contenedor (001 MOD RF-11, RF-13; 002 MOD RF-1)
    // ------------------------------------------------------------------
    // Cada funcionalidad declara habilitada/disponible/activa/seleccionada,
    // aporta sus vistas y puede declarar acceso exclusivo persistente.
    // ------------------------------------------------------------------

    private bool _mediaHooksOn;

    internal IslandFeatureState GetMediaFeatureState()
    {
        bool enabled = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandMediaEnabled;
        // Disponible con snapshot vigente o con una sesión permitida conocida
        // (p. ej. pausada desde antes del arranque): el clic abre sus controles.
        bool available = enabled && (MusicAvailable() || FirstAllowed() != null);
        bool active = IsMediaActiveForContract();
        return new IslandFeatureState(enabled, available, active,
            Selected: _selectedFeature?.Id == "media", Exclusive: false);
    }

    internal IslandFeatureState GetTimerFeatureState()
    {
        bool enabled = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandTimerEnabled;
        // Sin media siempre es usable: abre su configuración (001 MOD RF-9).
        bool available = enabled;
        bool active = enabled && _timer.IsCounting;
        // La alerta final es el caso canónico de acceso exclusivo persistente
        // (002 MOD RF-2): bloquea sustituciones y minimizado hasta X o reinicio.
        return new IslandFeatureState(enabled, available, active,
            Selected: _selectedFeature?.Id == "timer",
            Exclusive: enabled && _timer.State == IslandTimerState.Alerting);
    }

    private IIslandFeature? FeatureById(string id) =>
        _features.Features.FirstOrDefault(f => f.Id == id);

    /// <summary>Funcionalidad «media» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? MediaFeature => FeatureById("media");

    /// <summary>Funcionalidad «temporizador» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? TimerFeature => FeatureById("timer");

    private void SelectFeature(string id) => _selectedFeature = FeatureById(id);

    /// <summary>
    /// Ajuste de pantalla completa en caliente: la supresión se sirve desde una
    /// instantánea cacheada, así que se invalida, se recalcula con el valor nuevo y
    /// se publica el contexto. Si el Island tenía que apartarse, se aparta en el
    /// acto, sin esperar a los 5 s de la recuperación (001 MOD RF-8/14).
    /// </summary>
    public void RefreshSuppressionState() => Dispatcher.Invoke(() =>
    {
        _ctxValid = false;
        RefreshContextSnapshot();
        PostActivity(IslandActivityReason.Context);
    });

    private bool HasExclusive() =>
        _features.Features.Any(f => f.State.Exclusive);

    // --- consultas de contenido vigente ---

    private bool AnimationsEnabled => SettingsManager.Current.IslandAnimated && SettingsManager.Current.FlyoutAnimationSpeed != 0;

    /// <summary>¿La vista actual es música (no temporizador) con sesión disponible?</summary>
    private bool MusicContentShown() => _contentMode == IslandContentMode.Media && MusicAvailable();

    // ------------------------------------------------------------------
    // Ajustes en caliente: aplicar el estado completo del contenedor sin
    // reiniciar la aplicación.
    // ------------------------------------------------------------------

    public void RefreshEnabledState()
    {
        // Re-vincula la lista de presets: restaurar un archivo de ajustes en
        // caliente reemplaza la colección y el ItemsSource se quedaría en la
        // vieja (asignar la misma instancia es inofensivo).
        if (!ReferenceEquals(TimerPresetList.ItemsSource, SettingsManager.Current.IslandTimerPresets))
            TimerPresetList.ItemsSource = SettingsManager.Current.IslandTimerPresets;
        if (!SettingsManager.Current.IslandEnabled)
        {
            SnapHidden();
            UpdateBackgroundMode();
            Visibility = Visibility.Collapsed;
            return;
        }

        if (Suppressed())
        {
            _wasSuppressed = true;
            SnapHidden();
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        // El contenido visible (temporizador o cajón) se conserva ante
        // actualizaciones del contenedor (001 ADDED RF-1): no se reconstruye ni
        // se desplaza sin un evento nuevo.
        if (IsBoxShown && _contentMode != IslandContentMode.Media)
        {
            if (_contentMode == IslandContentMode.Apps && !AppsModeAvailable()) FallbackFromAppsView();
            // Mismo caso para el estante: si dejó de ser usable mientras estaba a la
            // vista (se apagó en ajustes), se repliega a la activa vigente o al reposo.
            else if (_contentMode == IslandContentMode.Shelf && !ShelfModeAvailable()) FallbackFromShelfView();
            // Y el calendario: sin sesión (o con la funcionalidad apagada) no puede
            // quedarse pintado (cerrar sesión no deja eventos ajenos a la vista).
            else if (_contentMode == IslandContentMode.Calendar && !CalendarModeAvailable()) FallbackFromCalendarView();
            // El aviso de Bluetooth, igual: apagado el ajuste no puede quedarse su
            // capa puesta (RefreshBluetoothContent ya lo repliega, pero el ajuste del
            // contenedor puede llegar por esta ruta).
            else if (_contentMode == IslandContentMode.Bluetooth && !BluetoothModeAvailable()) FallbackFromBluetoothView();
            else
            {
                if (_contentMode == IslandContentMode.Timer) RefreshTimerUI();
                ApplyContentVisibility();
                SyncMeasuredHeight();
            }
            RefreshAppearance();
            return;
        }
        // SIN media no hay vista musical; con cuenta viva el clic abre el
        // temporizador (RF-13). Never anchoring an empty music box.
        var session0 = MediaContentAvailable() ? ActiveMediaSession() : null;
        var status0 = session0 == null ? null : SafeStatus(session0) ?? _music?.Status;
        bool active = status0 == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            || (SettingsManager.Current.IslandPauseCountsActive
                && status0 == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
        if (SettingsManager.Current.IslandVisibilityMode == 0 && active)
        {
            if (session0 != null) ShowMusicCompact(session0, status0);
        }
        else if (TimerKeepsAlive()) ShowTimerCompact();
        else if (!_expanded)
        {
            // Reposo re-resuelto (pieza o nada) al cambiar ajustes y TAMBIÉN al
            // arrancar: la condición no puede exigir una caja ya visible, porque el
            // arranque es justo el caso en que todavía no hay ninguna (001 MOD RF-2).
            ShowInactiveOrHidden();
        }
        RefreshAppearance();
        // El ajuste ya presentó la vista; el buzón reconcilia una sola vez el
        // estado final (ecualizador, flechas, cadencia por contenido).
        PostActivity(IslandActivityReason.Settings);
    }

    public void RefreshVisibilityState()
    {
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        if (IsBoxShown && _contentMode != IslandContentMode.Media)
        {
            // Temporizador o cajón visible: la actualización re-aplica su estado
            // sin reconstruir paneles ni ceder la vista a música sin evento nuevo
            // (001 ADDED RF-1, 002 ADDED RF-1).
            if (_contentMode == IslandContentMode.Timer) RefreshTimerUI();
            ApplyContentVisibility();
            SyncMeasuredHeight();
            return;
        }
        // Sin media no hay vista por estado musical: el clic con el temporizador
        // lo cubre (RF-13); salir sin tocar el control multimedia.
        // La vista vigente es la de la actividad real (adoptando sesión si el
        // snapshot estaba ausente o desfasado): nunca una decisión tomada con un
        // snapshot obsoleto (001 MOD RF-4, RF-11).
        var session = ActiveMediaSession();
        if (session == null)
        {
            // Igual que en RefreshEnabledState: el reposo se resuelve aunque la caja
            // todavía no esté a la vista (arranque), no solo cuando ya lo estaba.
            if (!TimerKeepsAlive() && !_expanded) ShowInactiveOrHidden();
            return;
        }
        var status = SafeStatus(session) ?? _music?.Status;
        if (status == null) return;
        // «Visible mientras activo»: reproducir es actividad por sí mismo y la
        // pausa sostiene solo con el ajuste de pausa-activa (001 MOD RF-6);
        // «Aviso temporal»: rigen los activadores legacy.
        bool showForStatus = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? (SettingsManager.Current.IslandVisibilityMode == 0 || SettingsManager.Current.IslandShowOnPlayPause)
            : status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                && (SettingsManager.Current.IslandShowOnPause
                    || (SettingsManager.Current.IslandPauseCountsActive
                        && SettingsManager.Current.IslandVisibilityMode == 0));
        if (showForStatus)
        {
            _currentId = session.Id;
            if (_expanded) RefreshUi(session, status);
            else ShowMusicCompact(session, status);
        }
        else if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
                 status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
        {
            HidePerMode();
        }
        PostActivity(IslandActivityReason.Settings);
    }
}
