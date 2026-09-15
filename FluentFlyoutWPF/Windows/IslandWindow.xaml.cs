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
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text;
using Windows.Media.Control;
using static WindowsMediaController.MediaManager;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Fluent Island: contenedor escalable superior central. Inactivo / compacto / expandido.
/// Aloja funcionalidades registradas (media, temporizador) bajo un contrato común:
/// habilitada/disponible/activa/seleccionada + vistas + dimensiones + exclusiva (RF-11, RF-13).
/// Animación de CAJA ÚNICA con muelles por frame (sin Storyboards): el
/// ancho/alto reales del IslandBox se interpolan, así no hay dos cuadrados
/// peleándose por Visibility. Re-apuntar a mitad de vuelo es gratis.
/// </summary>
public partial class IslandWindow : Window
{
    private const int DefaultExpandedIslandWidth = 320;
    private const int DefaultExpandedIslandHeight = 126;
    // Estados del contenedor (001 MOD RF-11): compacto estándar y la pieza
    // inactiva (negra, más estrecha que el compacto, sin contenido).
    private const double CompactPillWidth = 240;
    private const double InactivePillWidth = 112;
    private double ExpandedIslandWidth => Math.Clamp(
        SettingsManager.Current.IslandExpandedWidth > 0 ? SettingsManager.Current.IslandExpandedWidth : DefaultExpandedIslandWidth,
        280,
        600);
    private double ExpandedIslandHeight => Math.Clamp(
        SettingsManager.Current.IslandExpandedHeight > 0 ? SettingsManager.Current.IslandExpandedHeight : DefaultExpandedIslandHeight,
        100,
        220);
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly Brush IslandBorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush MediaPlayingBrush = new SolidColorBrush(Color.FromRgb(0xB6, 0xF0, 0xB5));
    private static readonly Brush MediaPausedBrush = new SolidColorBrush(Color.FromRgb(0x76, 0x7B, 0x79));

    private readonly MainWindow _main;
    private readonly Dictionary<string, DateTime> _lastPlay = new();
    private string? _currentId;
    private bool _expanded;
    private bool _drag;
    private CancellationTokenSource? _hideCts;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _hoverPoll;
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
    private string _lastTrackKey = "";
    private int _popVersion;
    private bool _popPlaying;
    private double _pop; // 0..1 pulso de cambio de pista
    private bool _albumArtHovering;
    private bool _hasAlbumCover;
    private BitmapImage? _displayedAlbumArt;
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
    private bool _inactiveShown; // el reposo actual es la pieza inactiva (p=0, q=1)
    private bool _inactiveHot; // hover vivo sobre la pieza inactiva
    private double _inactiveHotT; // 0..1 micro-crecimiento del hover
    private bool _pendingRestAfterCollapse; // expandido->compacto: falta resolver reposo

    public IslandWindow(MainWindow main)
    {
        _main = main;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        InitTimer();
        // Contenedor escalable: media y temporizador se registran en orden de
        // navegación; el contrato decide qué se puede mostrar (RF-11, RF-13).
        _features.Register(new IslandMediaFeature(this));
        _features.Register(new IslandTimerFeature(this));
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
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        _hoverPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverPoll.Tick += (_, _) => PollFringeHover();
        _hoverPoll.Start();
        SyncExistingMediaState();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowHelper.SetTopmost(this);
        PositionTopCenter();
        SyncMeasuredHeight();
        SnapFrame();
    }

    private void NotePlay(string id) { _lastPlay[id] = DateTime.Now; _currentId = id; }

    private bool _mediaHooksOn;

    /// <summary>
    /// Engancha/desengancha los eventos del control multimedia del Island.
    /// El Island SIEMPRE sigue las sesiones (es su propio contenido); el
    /// parámetro existe para desenganchar limpiamente al cerrar.
    /// </summary>
    private void HookMediaEvents(bool on)
    {
        if (on == _mediaHooksOn) return;
        var mm = _main.mediaManager;
        if (on)
        {
            mm.OnAnyPlaybackStateChanged += OnPlayState;
            mm.OnAnyMediaPropertyChanged += OnMediaProp;
            mm.OnAnySessionClosed += OnClosed;
        }
        else
        {
            mm.OnAnyPlaybackStateChanged -= OnPlayState;
            mm.OnAnyMediaPropertyChanged -= OnMediaProp;
            mm.OnAnySessionClosed -= OnClosed;
            Dispatcher.Invoke(() =>
            {
                if (_music != null) OnMusicUnavailable();
            });
        }
        _mediaHooksOn = on;
    }

    /// <summary>
    /// Reconcilia el snapshot con las sesiones actuales (ajustes de media
    /// cambiados, filtros de apps, etc.). El toggle del Media Flyout NO
    /// afecta al Island: este siempre sigue las sesiones del sistema.
    /// </summary>
    public void RefreshMediaHook() => Dispatcher.Invoke(SyncExistingMediaState);

    /// <summary>
    /// Cambio del ajuste «Control multimedia» del Island: con el contenido
    /// apagado se libera el snapshot (sin vista musical vacía) y la vista
    /// vuelve al temporizador o se oculta; con él encendido se reconcilia.
    /// </summary>
    public void RefreshMediaContent() => Dispatcher.Invoke(() =>
    {
        if (!MediaContentAvailable())
        {
            if (_music != null) OnMusicUnavailable();
            else if (!TimerKeepsAlive()) HidePerMode();
        }
        else
        {
            SyncExistingMediaState();
        }
        UpdateMediaStatusDot();
        RefreshAppearance();
    });

    /// <summary>
    /// El contenido musical del Island se puede deshabilitar de forma
    /// independiente (igual que el temporizador con IslandTimerEnabled).
    /// </summary>
    private bool MediaContentAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandMediaEnabled;

    private MediaSession? NewestPlaying()
    {
        MediaSession? best = null;
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
        {
            if (!_main.IsSessionAllowed(s)) continue;
            if (s.ControlSession?.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
            if (best == null || (_lastPlay.TryGetValue(s.Id, out var t) && (!_lastPlay.TryGetValue(best.Id, out var bt) || t > bt)))
                best = s;
        }
        return best;
    }

    private MediaSession? Current()
    {
        // Resolución SIEMPRE mediada por el snapshot: sin música disponible no
        // hay sesión "actual" aunque el control multimedia siga vivo.
        var snap = _music;
        if (snap == null) return null;
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
            if (s.Id == snap.Id && _main.IsSessionAllowed(s)) return s;
        return null;
    }

    private MediaSession? FirstAllowed()
    {
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
            if (_main.IsSessionAllowed(s)) return s;
        return null;
    }

    // ------------------------------------------------------------------
    // Desacoplamiento del control multimedia (change island-contenedor-refactor)
    // ------------------------------------------------------------------
    // El Island NO depende del pipeline de media para existir: su estado
    // musical vive SOLO en este snapshot, actualizado por los eventos de
    // sesión. El render, la franja de hover, el punto de estado y el motor
    // de animación jamás consultan el control multimedia; sin sesiones el
    // Island simplemente solo tiene el temporizador como contenido
    // disponible y se abre por hover (RF-13).
    private sealed record IslandMediaSnapshot(
        string Id,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus Status);

    private IslandMediaSnapshot? _music;

    private bool MusicAvailable() => _music != null;

    private void ApplyMediaSnapshot(IslandMediaSnapshot? s)
    {
        bool existed = _music != null;
        _music = s;
        _lastStatus = s?.Status;
        if (s == null)
        {
            // Sin sesión: cero datos musicales residuales, la vista la decide
            // el contenedor (timer disponible u oculto), nunca música vacía.
            if (existed) ClearMusicResidue();
            return;
        }
        PaintGlyph();
    }

    // El gestor multimedia arranca antes que el Island, así que la primera
    // reproducción puede no emitir un evento que esta ventana alcance a ver.
    // Concilia el snapshot con las sesiones actuales: mientras la sesión
    // presentada SIGA EXISTIENDO se conserva (aunque esté pausada); solo se
    // libera cuando desaparece o pasa a no permitida. Así el tick nunca
    // arranca la vista a las manos del hover ni del usuario.
    private void SyncExistingMediaState()
    {
        if (!MediaContentAvailable() || Suppressed()) return;
        var snap = _music;
        if (snap != null)
        {
            // La sesión presentada sigue viva y permitida: conservarla tal cual
            // (el punto de estado ya refleja play/pausa). NADA de revocarla por
            // no estar reproduciendo ahora mismo.
            var held = Current();
            if (held != null)
            {
                var heldStatus = SafeStatus(held);
                if (heldStatus != null && heldStatus != snap.Status)
                    ApplyMediaSnapshot(new IslandMediaSnapshot(snap.Id, heldStatus.Value));
                return;
            }
            // Ya no existe (o no está permitida): liberar y limpiar residuos.
            OnMusicUnavailable();
            snap = null;
        }
        // Sin snapshot: adoptar algo que se esté reproduciendo AHORA (evento de
        // arranque perdido); una sesión pausada NO se adopta sola para no
        // robarle la vista al temporizador ni sorprender al usuario.
        var session = NewestPlaying();
        if (session == null) return;
        var status = SafeStatus(session);
        ApplyMediaSnapshot(new IslandMediaSnapshot(session.Id, status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
        // «Visible mientras activo» muestra la reproducción sin depender del
        // activador legacy; «Aviso temporal» lo respeta (001 MOD RF-1).
        bool mode0 = SettingsManager.Current.IslandVisibilityMode == 0;
        if (!mode0 && !SettingsManager.Current.IslandShowOnPlayPause) return;
        // El evento de reproducción es el más reciente: reclama la vista aunque
        // el temporizador siga contando (001 MOD RF-24); la cuenta no se toca.
        if (_expanded) RefreshUi(session, status);
        else ShowMusicCompact(session, status);
    }

    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastStatus;

    private void OnPlayState(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo? info)
    {
        var status = info?.PlaybackStatus ?? session.ControlSession?.GetPlaybackInfo()?.PlaybackStatus;
        Dispatcher.Invoke(() =>
        {
            if (!_main.IsSessionAllowed(session)) return;
            if (!MediaContentAvailable())
            {
                // Contenido musical deshabilitado: el snapshot se vacía y la
                // vista queda para el temporizador (o nada).
                if (_music?.Id == session.Id) OnMusicUnavailable();
                return;
            }
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                NotePlay(session.Id);
                ApplyMediaSnapshot(new IslandMediaSnapshot(session.Id,
                    status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
                // «Visible mientras activo» (0): reproduciendo manda sin importar
                // el activador legacy; «Aviso temporal» (1): respeta el ajuste.
                bool mode0 = SettingsManager.Current.IslandVisibilityMode == 0;
                if (!mode0 && !SettingsManager.Current.IslandShowOnPlayPause) return;
                if (_expanded) RefreshUi(session, status);
                else ShowMusicCompact(session, status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            }
            else if (_music?.Id == session.Id)
            {
                // La sesión del snapshot se pausó: conservarla en el snapshot
                // (punto de estado gris) y decidir presentación por ajuste.
                ApplyMediaSnapshot(new IslandMediaSnapshot(session.Id, status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
                if (_expanded)
                {
                    // Pausa desde el expandido (botón o visualizador): se queda
                    // expandido mostrando el estado de pausa con sus controles.
                    RefreshUi(session, status);
                    return;
                }
                bool keep = SettingsManager.Current.IslandVisibilityMode == 0
                    && SettingsManager.Current.IslandPauseCountsActive && IsBoxShown;
                // Pausar desde compacto con pausa!=activa en modo 0 debe ir a
                // inactivo/nada aunque el cursor siga encima (HidePerMode
                // retorna por IsMouseOverBoxOrStrip). Forzar Hide sin veto hover.
                bool forceHideFromCompact = !_expanded
                    && SettingsManager.Current.IslandVisibilityMode == 0
                    && !SettingsManager.Current.IslandPauseCountsActive
                    && !SettingsManager.Current.IslandShowOnPause;
                if (keep || SettingsManager.Current.IslandShowOnPause)
                    ShowMusicCompact(session, status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
                else if (forceHideFromCompact)
                    ShowInactiveOrHidden();
                else
                    HidePerMode();
            }
            else
            {
                var next = NewestPlaying();
                if (next != null)
                {
                    NotePlay(next.Id);
                    var nextStatus = SafeStatus(next);
                    ApplyMediaSnapshot(new IslandMediaSnapshot(next.Id, nextStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
                    if (!SettingsManager.Current.IslandShowOnPlayPause) return;
                    if (_expanded) RefreshUi(next);
                    else ShowMusicCompact(next);
                }
                else if (_music == null && SettingsManager.Current.IslandShowOnPause
                    && status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    // Sin snapshot y sin nadie reproduciendo: solo se crea vista
                    // si el usuario muestra pausas; nunca una vista sin datos.
                    NotePlay(session.Id);
                    ApplyMediaSnapshot(new IslandMediaSnapshot(session.Id,
                        status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
                    ShowMusicCompact(session, status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
                }
            }
        });
    }

    private void OnMediaProp(MediaSession session, GlobalSystemMediaTransportControlsSessionMediaProperties _)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_main.IsSessionAllowed(session)) return;
            if (!MediaContentAvailable())
            {
                if (_music?.Id == session.Id) OnMusicUnavailable();
                return;
            }
            var show = SafeStatus(session) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                ? session : NewestPlaying();
            if (show == null) return;
            var showStatus = SafeStatus(show);
            if (showStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                NotePlay(show.Id);
            ApplyMediaSnapshot(new IslandMediaSnapshot(show.Id, showStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
            if (_expanded || IslandBox.Visibility == Visibility.Visible) RefreshUi(show);
            else if (SettingsManager.Current.IslandShowOnTrackChange) ShowMusicCompact(show);
        });
    }

    private void OnClosed(MediaSession session)
    {
        _lastPlay.Remove(session.Id);
        Dispatcher.Invoke(() =>
        {
            if (_music?.Id != session.Id && MusicAvailable()) return;
            OnMusicUnavailable();
        });
    }

    // La música desapareció (cierre de la última sesión o candidata inválida):
    // sin compacto musical vacío ni datos muertos. El temporizador disponible
    // puede recuperar la vista inmediatamente (001 MOD RF-24, 002 MOD RF-14).
    private void OnMusicUnavailable()
    {
        _currentId = null;
        ApplyMediaSnapshot(null);
        // El poll de la franja dispara ExpandFromHover cada 150 ms; sin esta
        // pausa re-abriría la caja que acabamos de cerrar bajo el cursor.
        _hoverSnoozeUntil = DateTime.UtcNow.AddSeconds(TimerReshowSnoozeSeconds);
        ClearClosedMediaView();
    }

    private void ClearClosedMediaView()
    {
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        _expanded = false;
        // Al cerrar la última sesión no deben quedar restos musicales:
        // carátula, fondo, título ni estado de reproducción obsoleto
        // (001 MOD RF-24, 002 MOD RF-14).
        _timerMode = 0;
        ApplyTimerContentVisibility();
        ClearMusicResidue();
        if (TimerKeepsAlive()) ShowTimerCompact();
        else ShowInactiveOrHidden();
    }

    /// <summary>
    /// Retira carátula, fondo difuminado, títulos y estado de reproducción
    /// obsoletos para que la vista musical sin sesión no deje controles ni
    /// fondos residuales (001 MOD RF-24).
    /// </summary>
    private void ClearMusicResidue()
    {
        _lastStatus = null;
        _lastTrackKey = "";
        _displayedAlbumArt = null;
        _hasAlbumCover = false;
        PaintGlyph();
        SetAlbumArt(null);
        SetBackground(null);
        SongTitle.Text = "";
        SongArtist.Text = "";
        CompactTitle.Text = "";
        Seekbar.Value = 0;
        PosText.Text = "0:00";
        DurText.Text = "0:00";
    }

    // --- estados ---

    private bool IsBoxShown => IslandBox.Visibility == Visibility.Visible;
    // Anti-reapertura por hover tras ocultar/colapsar la caja bajo el cursor.
    private DateTime _hoverSnoozeUntil = DateTime.MinValue;

    private void ArmTemporaryHide()
    {
        if (SettingsManager.Current.IslandVisibilityMode != 1 || _expanded) return;
        _hideCts?.Cancel();
        var cts = _hideCts = new CancellationTokenSource();
        int ms = Math.Clamp(SettingsManager.Current.IslandVisibilityDuration, 1000, 10000);
        _ = Task.Delay(ms).ContinueWith(_ =>
            Dispatcher.Invoke(() => { if (!cts.IsCancellationRequested && !_expanded && !IsMouseOverBoxOrStrip()) ShowInactiveOrHidden(); }));
    }

    private void ShowMusicCompact(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null, bool forceAlbumFlip = false)
    {
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        if (!MusicAvailable()) return; // sin snapshot musical no hay vista musical (RF-13)
        _hideCts?.Cancel();
        _hidingViaCompact = false;
        SelectFeature("media");
        _inactiveShown = false;
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) { SnapHidden(); return; }
        RefreshUi(session, knownStatus, forceAlbumFlip);
        _expanded = false;
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled) SnapCompact();
        else
        {
            _pT = 0;
            _qT = 1;
            IslandBox.Visibility = Visibility.Visible;
            UpdateMediaStatusDot();
            UpdateRotationPauseState();
            EnsureLoop();
        }
        ArmTemporaryHide();
    }

    private void HidePerMode()
    {
        _hideCts?.Cancel();
        UpdateRotationPauseState();
        // Temporizador vivo: repliega a su compacto en vez de ocultar (H2 del spec 002).
        // Salvo avisando: la alerta manda y persiste expandida hasta X o reinicio.
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        if (TimerKeepsAlive()) { ShowTimerCompact(); return; }
        if (IsMouseOverBoxOrStrip()) return;
        if (_expanded)
        {
            _expanded = false;
            if (!AnimationsEnabled) { ShowInactiveOrHidden(); return; }
            if (ReturnToInactive())
            {
                // Minimizado obligatorio (001 MOD RF-4): primero compacta (p->0
                // con q=1); al asentarse resuelve el reposo (pieza inactiva o nada).
                _pendingRestAfterCollapse = true;
                _hidingViaCompact = false;
                _pT = 0;
            }
            else
            {
                _hidingViaCompact = true;
                _pT = 0; _qT = 0;
            }
            EnsureLoop();
            return;
        }
        ShowInactiveOrHidden();
    }

    private void CollapseAll() => SnapHidden();

    // -----------------------------------------------------------------
    // Contrato del contenedor escalable (001 MOD RF-11, RF-13; 002 MOD RF-1).
    // Cada funcionalidad declara habilitada/disponible/activa/seleccionada,
    // aporta sus vistas y puede declarar acceso exclusivo persistente.
    // -----------------------------------------------------------------
    internal IslandFeatureState GetMediaFeatureState()
    {
        bool enabled = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandMediaEnabled;
        // Disponible con snapshot vigente o con una sesión permitida conocida
        // (p. ej. pausada desde antes del arranque): el clic abre sus controles.
        bool available = enabled && (MusicAvailable() || FirstAllowed() != null);
        bool playing = _music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        bool active = available && (playing || SettingsManager.Current.IslandPauseCountsActive);
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

    private void SelectFeature(string id) =>
        _selectedFeature = _features.Features.FirstOrDefault(f => f.Id == id);

    /// <summary>
    /// Última usable para expandir (001 MOD RF-3): manda la funcionalidad en
    /// uso (último-activo); si ya no es usable, la última activa; si no, la
    /// primera usable. Sin ninguna usable no abre vista vacía.
    /// </summary>
    private IIslandFeature? LastUsableFeature()
    {
        if (_selectedFeature is { } sel && sel.State.Usable) return sel;
        var active = _features.ActiveFeatures().FirstOrDefault(f => f.State.Usable);
        if (active != null) return active;
        return _features.UsableFeatures().FirstOrDefault();
    }

    /// <summary>
    /// Expande la última usable con repliegue seguro: si la elegida deja de ser
    /// presentable en el último instante, prueba la siguiente usable; si no hay
    /// ninguna, resuelve el reposo sin abrir una caja vacía (RF-3, RF-9, RF-16).
    /// </summary>
    private bool ExpandLastUsable()
    {
        if ((Suppressed() && !HasExclusive()) || !SettingsManager.Current.IslandEnabled) { SnapHidden(); return false; }
        var feature = LastUsableFeature();
        if (feature != null && feature.TryShowExpanded()) return true;
        var alt = _features.NextUsableAfter(feature);
        if (alt != null && alt.TryShowExpanded()) return true;
        HidePerMode();
        return false;
    }

    // Implementaciones del contrato que el island presta a media y temporizador.
    internal bool ShowMediaExpandedFromContract()
    {
        var session = Current();
        if (session == null && MediaContentAvailable())
        {
            // Sesión permitida conocida sin snapshot (pausada antes del arranque):
            // se adopta para que el clic abra sus controles (RF-13).
            var known = FirstAllowed();
            if (known != null)
            {
                NotePlay(known.Id);
                ApplyMediaSnapshot(new IslandMediaSnapshot(known.Id,
                    SafeStatus(known) ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
                session = known;
            }
        }
        if (session == null) return false;
        ExpandSession(session);
        return true;
    }

    internal bool ShowMediaCompactFromContract()
    {
        var session = Current();
        if (session == null) return false;
        ShowMusicCompact(session);
        return true;
    }

    internal bool ShowTimerExpandedFromContract()
    {
        if (!TimerModeAvailable()) return false;
        ExpandTimer();
        return true;
    }

    internal bool ShowTimerCompactFromContract()
    {
        if (!TimerModeAvailable()) return false;
        ShowTimerCompact();
        return true;
    }

    /// <summary>¿Hay alguna funcionalidad habilitada y disponible? (001 MOD RF-9)</summary>
    private bool AnyFeatureUsable() => _features.UsableFeatures().Any();

    /// <summary>
    /// El toggle «volver a inactivo» decide el reposo: pieza negra visible o
    /// nada (001 MOD RF-2, por defecto inactivo visible). Sin funcionalidades
    /// usables no hay pieza: no se ancla una caja vacía.
    /// </summary>
    private bool ReturnToInactive() =>
        SettingsManager.Current.IslandReturnToInactive && AnyFeatureUsable();

    /// <summary>
    /// Reposo del contenedor (001 MOD RF-2): pieza inactiva o nada según el
    /// toggle; ante supresión, oculto (la pieza también se suprime, RF-14).
    /// </summary>
    private void ShowInactiveOrHidden()
    {
        if (Suppressed() && !HasExclusive()) { SnapHidden(); return; }
        if (ReturnToInactive()) ShowInactive();
        else GoHidden();
    }

    /// <summary>
    /// Estado inactivo (001 MOD RF-11, RF-16): pill negra más estrecha que el
    /// compacto, sin ninguna vista de contenido; el hover solo la agranda y el
    /// clic abre la última usable.
    /// </summary>
    private void ShowInactive()
    {
        _hideCts?.Cancel();
        _hidingViaCompact = false;
        _pendingRestAfterCollapse = false;
        _expanded = false;
        _timerMode = 0;
        _inactiveShown = true;
        _inactiveHot = false;
        ApplyTimerContentVisibility();
        ClearInactiveResidue();
        PositionTopCenter();
        if (!AnimationsEnabled)
        {
            _p = _pT = 0; _pv = 0;
            _q = _qT = 1; _qv = 0;
            _inactiveHotT = 0;
            ApplyFrame();
            IslandBox.Visibility = Visibility.Visible;
            UpdateRotationPauseState();
            UpdateLine();
            return;
        }
        _pT = 0;
        _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateRotationPauseState();
        UpdateLine();
        EnsureLoop();
    }

    /// <summary>
    /// Limpieza real del estado inactivo (001 MOD RF-11): no basta con fundir
    /// las capas por opacidad, el contenido residual (grillas compactas con la
    /// última funcionalidad, carátula, fondo, títulos y textos del temporizador)
    /// se retira de verdad. La restauración corre por las rutas normales
    /// (ApplyTimerContentVisibility + RefreshUi/RefreshTimerUI al mostrar
    /// compacto o expandido).
    /// </summary>
    private void ClearInactiveResidue()
    {
        // Grillas de contenido compacto: fuera del árbol visual mientras dura el reposo.
        MusicCompactGrid.Visibility = Visibility.Collapsed;
        TimerCompactGrid.Visibility = Visibility.Collapsed;
        // Datos musicales: sin carátula, fondo difuminado, títulos ni seek.
        ClearMusicResidue();
        // Datos del temporizador: sin restante ni progreso heredados.
        TimerRemaining.Text = "00:00:00";
        TimerProgressFill.Width = 0;
        TimerRunRemaining.Text = "00:00:00";
        // Por si algún panel expandido quedó visible de la vista anterior.
        TimerAlert.Visibility = Visibility.Collapsed;
        ApplyFrame();
    }

    // --- pieza inactiva (001 MOD RF-16): hover micro-crece, clic abre la usable ---
    private void InactiveBorder_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        // El clic completa el crecimiento y abre la última usable; sin usable
        // no abre vista vacía (001 MOD RF-3, RF-9, RF-16).
        if (!ExpandLastUsable())
            _hoverSnoozeUntil = DateTime.UtcNow.AddSeconds(TimerReshowSnoozeSeconds);
    }

    /// <summary>
    /// Clic en cualquier zona del island que no sea un control específico:
    /// expande la última usable (001 MOD RF-3). Título, álbum, ecualizador y
    /// reels tienen sus propios manejadores.
    /// </summary>
    private void HandleIslandClick(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        e.Handled = true;
        ExpandLastUsable();
    }

    private void GoHidden()
    {
        if (!AnimationsEnabled || !IsBoxShown) { SnapHidden(); return; }
        if (_hidingViaCompact) return;
        if (Math.Abs(_p) > 0.05)
        {
            _expanded = false; _hidingViaCompact = true; _pT = 0; _qT = 0; EnsureLoop(); return;
        }
        _qT = 0;
        EnsureLoop();
    }

    private void SnapCompact()
    {
        _inactiveShown = false;
        _p = _pT = 0; _pv = 0;
        _q = _qT = 1; _qv = 0;
        _hexpShown = _hexp;
        _pop = 0; _popVersion++; _popPlaying = false;
        StopLoop();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
    }

    private void SnapHidden()
    {
        _hidingViaCompact = false;
        _inactiveShown = false;
        _p = _pT = 0; _pv = 0;
        _q = _qT = 0; _qv = 0;
        _hexpShown = _hexp;
        _pop = 0; _popVersion++; _popPlaying = false;
        StopLoop();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Collapsed;
        UpdateRotationPauseState();
        UpdateLine();
        UpdateMediaStatusDot();
    }

    private void Box_MouseEnter(object sender, MouseEventArgs e) => HoverDetected();

    /// <summary>
    /// Detección de puntero (001 MOD RF-3, RF-10): el hover SOLO produce el
    /// micro-crecimiento vivo; abrir contenido exige un clic explícito
    /// (HandleIslandClick). La zona de detección se mantiene, pero por sí sola
    /// nunca despliega contenido.
    /// </summary>
    private void HoverDetected()
    {
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        if (_expanded || _drag || _reelDragging) return;
        // Anti-reapertura: el usuario acaba de ocultar la caja con el cursor
        // encima; la detección no debe devolverle contenido de inmediato.
        if (DateTime.UtcNow < _hoverSnoozeUntil) return;
        if (!_inactiveHot)
        {
            _inactiveHot = true;
            if (AnimationsEnabled) EnsureLoop();
            else { _inactiveHotT = 1; ApplyFrame(); }
        }
    }

    private int HoverTolH => Math.Clamp(SettingsManager.Current.IslandHoverToleranceHorizontal < 0 ? 12 : SettingsManager.Current.IslandHoverToleranceHorizontal, 0, 80);
    private int HoverTolV => Math.Clamp(SettingsManager.Current.IslandHoverToleranceVertical < 0 ? 4 : SettingsManager.Current.IslandHoverToleranceVertical, 0, 40);

    private void PollFringeHover()
    {
        if (_expanded || _drag) return;
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        if (!NativeMethods.GetCursorPos(out var p)) return;
        var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
        if (primary.monitorArea.Width == 0) return;
        double tolH = HoverTolH * primary.dpiX / 96.0;
        double tolV = HoverTolV * primary.dpiY / 96.0;
        double halfRaw = LineFullWidth * 0.5 * primary.dpiX / 96.0 + tolH;
        double cx = primary.workArea.Left + primary.workArea.Width / 2;
        if (Math.Abs(p.X - cx) > halfRaw) return;
        double lineTop = primary.workArea.Top + (IsNotch ? 1 : Math.Clamp(SettingsManager.Current.IslandLineTopOffset, 0, 60)) * primary.dpiY / 96.0;
        if (p.Y < primary.monitorArea.Top - 2 || p.Y > lineTop + 3 + tolV) return;
        HoverDetected();
    }

    private void ExpandSession(MediaSession session)
    {
        if (HasExclusive()) return;
        if (!MusicAvailable()) return; // sin snapshot musical no hay vista musical (RF-13)
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        _hideCts?.Cancel();
        _hidingViaCompact = false;
        _currentId = session.Id;
        SelectFeature("media");
        _inactiveShown = false;
        if (HasExclusive()) return;
        _timerMode = 0;
        ApplyTimerContentVisibility();
        bool wasExpanded = _expanded;
        RefreshUi(session);
        _expanded = true;
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled)
        {
            _p = _pT = 1; _pv = 0;
            _q = _qT = 1; _qv = 0;
            _pop = 0; _popPlaying = false;
            ApplyFrame();
            IslandBox.Visibility = Visibility.Visible;
            UpdateMediaStatusDot();
            UpdateRotationPauseState();
            return;
        }
        if (wasExpanded) return; // ya expandido: solo actualizar datos
        _pT = 1; _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
        EnsureLoop();
    }

    private void Box_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_inactiveHot)
        {
            _inactiveHot = false;
            if (AnimationsEnabled) EnsureLoop();
            else { _inactiveHotT = 0; ApplyFrame(); }
        }
        if (_drag || _reelDragging || Mouse.LeftButton == MouseButtonState.Pressed) return;
        if (IsLeavingTowardTopEdge()) return; // gracia hacia el borde: Tick colapsa al salir de verdad
        LeaveHover();
    }

    // Cursor saliendo por arriba hacia el borde (hueco entre borde e isla): no colapsar,
    // si no el poll de franja lo re-expande a los ~150ms y se ve encoger-crecer.
    private bool IsLeavingTowardTopEdge()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var p)) return false;
            var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
            if (primary.monitorArea.Width == 0) return false;
            double islandOff = (IsNotch ? 0 : Math.Clamp(SettingsManager.Current.IslandTopOffset, 0, 80)) * primary.dpiY / 96.0;
            double islandTop = primary.workArea.Top + islandOff;
            if (p.Y < primary.workArea.Top - 2 || p.Y > islandTop + 2) return false;
            double halfW = ((_expanded || _p > 0.2) ? ExpandedIslandWidth : LineFullWidth) * 0.5;
            halfW = halfW * primary.dpiX / 96.0 + HoverTolH * primary.dpiX / 96.0;
            double cx = primary.workArea.Left + primary.workArea.Width / 2;
            return Math.Abs(p.X - cx) <= halfW;
        }
        catch { return false; }
    }

    // Repliegue a compacto conservando el contenido (último-activo, 001 MOD RF-4).
    private void CollapseToCompact()
    {
        _expanded = false;
        _hidingViaCompact = false;
        UpdateLine();
        PositionTopCenter();
        if (!AnimationsEnabled) { _p = _pT = 0; _pv = 0; ApplyFrame(); }
        else { _pT = 0; EnsureLoop(); }
    }

    private void LeaveHover()
    {
        if (!_expanded) return;
        // Alerta de fin (exclusiva): persistente hasta X o reinicio, aunque el
        // ratón se vaya; el minimizado ordinario no la toca (001 MOD RF-4).
        if (_timer.State == Classes.IslandTimerState.Alerting) return;

        // Minimizado obligatorio (001 MOD RF-4): media controlada sigue en media
        // aunque el temporizador corra/pausado. Tres orígenes distinguibles:
        // 1) media expandida -> SIEMPRE compacto media (CollapseToCompact)
        // 2) timer expandido -> compacto timer
        // 3) sin música -> reposo sin residuos
        if (_timerMode == 0)
        {
            if (Suppressed()) { HidePerMode(); return; }
            var session = Current();
            if (session != null)
            {
                // Con música disponible: colapsar sin sustituir (RF-4, RF-12).
                // CollapseToCompact no toca _timerMode ni fuerza timer.
                CollapseToCompact();
                return;
            }
            // Música expandida pero sesión ya no existe -> inactivo/nada.
            ShowInactiveOrHidden();
            return;
        }
        if (_timerMode == 1)
        {
            if (TimerKeepsAlive()) { ShowTimerCompact(); return; }
            ShowInactiveOrHidden();
            return;
        }
        HidePerMode();
    }

    // --- motor de muelle ---

    private bool AnimationsEnabled => SettingsManager.Current.IslandAnimated && SettingsManager.Current.FlyoutAnimationSpeed != 0;
    // ponytail: el modo «Siempre en su lugar» se retiró (001 REMOVED): quedan
    // «Visible mientras activo» (0) y «Aviso temporal» (1); el reposo lo decide
    // el toggle «volver a inactivo» (pieza negra) o nada (001 MOD RF-2).
    private MediaSession? AnySession() => !MusicContentShown() || _music == null ? null : Current() ?? NewestPlaying() ?? FirstAllowed();

    /// <summary>
    /// El hover respeta el contenido que el usuario ya tiene delante:
    /// expande el modo activo, y solo si no hay ninguno elige por
    /// disponibilidad (música > temporizador). NUNCA roba el modo bajo el
    /// cursor: eso producía el ciclo expandir/colapsar con el temporizador.
    /// </summary>
    private bool MusicContentShown() => _timerMode == 0 && MusicAvailable();
    private bool IsNotch => Math.Clamp(SettingsManager.Current.IslandStyle, 0, 1) == 1;
    // Radios independientes de compacto y expandido (0-40). El <0 es "sin migrar":
    // hereda el radio único heredado hasta que CompleteInitialization lo rellena.
    private double IslandCompactRadius => Math.Clamp(
        SettingsManager.Current.IslandCompactBorderRadius < 0
            ? SettingsManager.Current.IslandBorderRadius
            : SettingsManager.Current.IslandCompactBorderRadius, 0, 40);
    private double IslandExpandedRadius => Math.Clamp(
        SettingsManager.Current.IslandExpandedBorderRadius < 0
            ? SettingsManager.Current.IslandBorderRadius
            : SettingsManager.Current.IslandExpandedBorderRadius, 0, 40);

    public void RefreshEnabledState()
    {
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
        // El temporizador visible se conserva ante actualizaciones del contenedor
        // (001 ADDED RF-1): no se reconstruye ni se desplaza sin un evento nuevo.
        if (IsBoxShown && _timerMode == 1)
        {
            RefreshTimerUI();
            ApplyTimerContentVisibility();
            SyncMeasuredHeight();
            RefreshAppearance();
            return;
        }
        // SIN media no hay vista musical; con cuenta viva el clic abre el
        // temporizador (RF-13). Never anchoring an empty music box.
        var snap = MediaContentAvailable() ? _music : null;
        bool active = snap != null &&
            (snap.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
             || (SettingsManager.Current.IslandPauseCountsActive
                 && snap.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
        if (SettingsManager.Current.IslandVisibilityMode == 0 && active)
        {
            var session = Current() ?? NewestPlaying() ?? FirstAllowed();
            if (session != null) ShowMusicCompact(session);
        }
        else if (TimerKeepsAlive()) ShowTimerCompact();
        else if (IsBoxShown && !_expanded)
        {
            // Reposo re-resuelto al cambiar ajustes (pieza o nada).
            ShowInactiveOrHidden();
        }
        RefreshAppearance();
    }

    public void RefreshVisibilityState()
    {
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        if (IsBoxShown && _timerMode == 1)
        {
            // Temporizador visible: la actualización re-aplica su estado sin
            // reconstruir paneles ni ceder la vista a música sin evento nuevo
            // (001 ADDED RF-1, 002 ADDED RF-1).
            RefreshTimerUI();
            ApplyTimerContentVisibility();
            SyncMeasuredHeight();
            return;
        }
        // Sin media no hay vista por estado musical: el clic con el temporizador
        // lo cubre (RF-13); salir sin tocar el control multimedia.
        var snapshot = _music;
        if (snapshot == null)
        {
            if (!TimerKeepsAlive() && IsBoxShown && !_expanded) ShowInactiveOrHidden();
            return;
        }
        var session = Current() ?? NewestPlaying() ?? FirstAllowed();
        if (session == null) return;

        var status = snapshot.Status;
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
    }

    public void RefreshAppearance()
    {
        ApplyStyle();
        ApplyAlbumArtRadius();
        ApplyIslandTextStyle();
        UpdateLine();
        ApplyFrame();
    }

    // Apple-ish: muelle subamortiguado suave, escalado con la duración global.
    private void GetSpring(out double kP, out double cP, out double kQ, out double cQ)
    {
        double configuredDuration = MainWindow.getDuration();
        double durationScale = configuredDuration > 0 ? configuredDuration / 300.0 : 1.0;
        double frequencyScale = 1.0 / (durationScale * durationScale);
        double dampingScale = 1.0 / durationScale;
        kP = 520 * frequencyScale; cP = 34 * dampingScale;
        kQ = 200 * frequencyScale; cQ = 28 * dampingScale; // ambos estilos emergen desde el centro como Island
        if (IsNotch) kP *= 1.05;
    }

    private void EnsureLoop()
    {
        if (_loopOn) return;
        _loopOn = true;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopLoop()
    {
        if (!_loopOn) return;
        _loopOn = false;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering -= OnFrame;
    }

    private TimeSpan _lastTick = TimeSpan.Zero;

    private void OnFrame(object? s, EventArgs e)
    {
        var args = e as RenderingEventArgs;
        TimeSpan now = args?.RenderingTime ?? TimeSpan.Zero;
        if (now == TimeSpan.Zero) now = TimeSpan.FromTicks(Environment.TickCount64 * 10000);
        double dt;
        if (_lastTick == TimeSpan.Zero || now <= _lastTick) dt = 1.0 / 60.0;
        else dt = Math.Clamp((now - _lastTick).TotalSeconds, 1.0 / 240.0, 1.0 / 25.0);
        _lastTick = now;

        GetSpring(out double kP, out double cP, out double kQ, out double cQ);
        Step(ref _p, ref _pv, _pT, kP, cP, dt);
        Step(ref _q, ref _qv, _qT, kQ, cQ, dt);
        // La altura del expandido persigue a su objetivo: cambios de contenido glideas, no saltos.
        _hexpShown += (_hexp - _hexpShown) * Math.Clamp(dt * 10, 0, 1);
        if (Math.Abs(_hexp - _hexpShown) < 0.5) _hexpShown = _hexp;
        // Micro-crecimiento vivo del hover (pieza inactiva y caja en reposo).
        double hotTarget = _inactiveHot ? 1 : 0;
        if (_inactiveHotT != hotTarget)
        {
            _inactiveHotT += (hotTarget - _inactiveHotT) * Math.Clamp(dt * 12, 0, 1);
            if (Math.Abs(_inactiveHotT - hotTarget) < 0.01) _inactiveHotT = hotTarget;
        }
        if (_popPlaying) StepPop(dt);
        ApplyFrame(dt);

        bool pSettled = Math.Abs(_p - _pT) < 0.002 && Math.Abs(_pv) < 0.02;
        bool qSettled = Math.Abs(_q - _qT) < 0.002 && Math.Abs(_qv) < 0.02;
        if (pSettled) { _p = _pT; _pv = 0; }
        if (qSettled) { _q = _qT; _qv = 0; }

        bool popSettled = !_popPlaying;
        bool hSettled = _hexpShown == _hexp;
        bool hotSettled = _inactiveHotT == (_inactiveHot ? 1 : 0);

        if (pSettled && qSettled && popSettled && hSettled && hotSettled)
        {
            StopLoop();
            _lastTick = TimeSpan.Zero;
            if (_pendingRestAfterCollapse && _pT == 0 && _p == 0 && _qT == 1)
            {
                // El compacto ya asentó: resolver el reposo final (pieza o nada).
                _pendingRestAfterCollapse = false;
                ShowInactiveOrHidden();
                return;
            }
            if (_qT == 0 && _q == 0)
            {
                IslandBox.Visibility = Visibility.Collapsed;
                if (_wasSuppressed) Visibility = Visibility.Collapsed;
                UpdateLine();
            }
            // Si llegamos a compacto vía hidingViaCompact y no hay q pendiente, ya se ocultó arriba
        }
        else if (_qT == 0 && _q <= 0.12)
        {
            _hidingViaCompact = false;
            IslandBox.Visibility = Visibility.Collapsed;
            UpdateLine();
        }
    }

    private static void Step(ref double x, ref double v, double target, double k, double c, double dt)
    {
        double a = (target - x) * k - v * c;
        v += a * dt;
        x += v * dt;
        x = Math.Clamp(x, -0.15, 1.15); // deja un poco de overshoot visible
    }

    private double _popV;
    private double _popPhase; // 0 ida, 1 vuelta
    private void StepPop(double dt)
    {
        // Ida 0->1 rápida, vuelta 1->0 amortiguada: un solo ciclo
        if (_popPhase == 0)
        {
            double a = (1 - _pop) * 900 - _popV * 28;
            _popV += a * dt;
            _pop += _popV * dt;
            _pop = Math.Clamp(_pop, 0, 1);
            if (_pop >= 0.995) { _pop = 1; _popV = 0; _popPhase = 1; }
        }
        else
        {
            double a = (0 - _pop) * 500 - _popV * 32;
            _popV += a * dt;
            _pop += _popV * dt;
            _pop = Math.Clamp(_pop, 0, 1);
            if (_pop <= 0.005) { _pop = 0; _popV = 0; _popPlaying = false; _popPhase = 0; }
        }
    }

    private void SyncMeasuredHeight()
    {
        try
        {
            // Medir la altura expandida real con el ancho configurado.
            // ExpandedLayer está siempre en el árbol (Opacity 0 cuando compacto),
            // así que es medible.
            ExpandedLayer.Measure(new Size(ExpandedIslandWidth, double.PositiveInfinity));
            double measuredHeight = ExpandedLayer.DesiredSize.Height; // DesiredSize ya incluye el Margin vertical
            // ponytail: el contenido timer manda por medida (sin huecos); música mantiene su ajuste fijo.
            bool timerContent = TimerExpanded.Visibility == Visibility.Visible
                || TimerRunPanel.Visibility == Visibility.Visible
                || TimerAlert.Visibility == Visibility.Visible;
            double h = IsNotch || timerContent ? measuredHeight : ExpandedIslandHeight;
            double old = _hexp;
            if (h > (timerContent ? 34 : 60) && h < 260) _hexp = h;
            // El objetivo manda: si cambió, correr frames (o snapping). Si no
            // cambió, ni se toca el loop: música en reposo ni se entera.
            if (_hexp != old)
            {
                if (!AnimationsEnabled || (!IsBoxShown && _qT == 0)) _hexpShown = _hexp;
                else EnsureLoop();
            }
        }
        catch { }
    }

    private static double Smooth01(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private void SnapFrame()
    {
        // Estado base coherente antes del primer frame
        _p = _pT; _q = _qT;
        _pv = _qv = 0;
        _hexpShown = _hexp;
        _inactiveHotT = _inactiveHot ? 1 : 0;
        ApplyFrame();
    }

    private void ApplyFrame(double dt = 0)
    {
        double p = Math.Clamp(_p, 0, 1);
        double q = Math.Clamp(_q, 0, 1);
        // Línea gris con el mismo reloj que la isla (p y q): la isla crece
        // centrada = de adentro hacia afuera, la línea encoge centrada = de
        // afuera hacia adentro. Sigue al más rápido (Max): p termina antes
        // que q al emerger expandido, así la línea es 0 cuando el expandido
        // ya salió. En compacto p=0 y queda igual que antes. Sin tween separado.
        bool allowed = SettingsManager.Current.IslandActivityLine && !_inactiveShown && IsAliveForLine();
        _lineW = allowed ? LineFullWidth * (1 - Math.Max(Smooth01(p), Smooth01(q))) : 0;
        ActivityLine.Width = _lineW;
        ActivityLine.Visibility = allowed && _lineW > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        // Pop de pista atenúa con q (invisible -> no pulsa)
        double pop = _popPlaying ? _pop * q : 0;
        double exitTailOpacity = _qT == 0
            ? Math.Pow(Smooth01(Math.Clamp((q - 0.12) / 0.20, 0, 1)), 3)
            : 1;
        if (_hidingViaCompact)
            exitTailOpacity *= Math.Pow(Smooth01(Math.Clamp((p - 0.12) / 0.38, 0, 1)), 3);

        bool notch = IsNotch;
        double w, h, notchFillet = 0;
        // Reposo vivo: la pieza inactiva y el compacto respiran con el hover
        // (001 MOD RF-3, RF-16): solo crecen en dimensiones, el contenedor
        // jamás se desplaza de su posición.
        double hotW = 10 * _inactiveHotT;
        if (notch)
        {
            // Notch: mismo reveal que Island: punto central -> compacto -> expandido.
            const double notchDot = 26;
            double compactW = _inactiveShown ? InactivePillWidth : 200;
            double dotT = Math.Clamp(q / 0.32, 0, 1);
            double stretchT = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double baseW = q < 0.32 ? notchDot : Lerp(notchDot, compactW, stretchT);
            w = Lerp(baseW, ExpandedIslandWidth, Smooth01(p));
            h = Lerp(34, _hexpShown, Smooth01(p));
            // Hover vivo: crece desde el punto y en reposo (también en compacto).
            w += hotW * (1 - Smooth01(p));
            double revealOpacity = Smooth01(Math.Clamp(q / 0.38, 0, 1));
            IslandBox.Opacity = revealOpacity * revealOpacity * exitTailOpacity;
            // El radio hace morph con p: compacto -> expandido sin saltos.
            // ponytail: el ANCHO de orejas lo fija el fillet expandido (constante por estado);
            // la CAÍDA de la cueva hace morph compacto->expandido: elipse tendida -> circular.
            double radius = Math.Min(Lerp(IslandCompactRadius, IslandExpandedRadius, Smooth01(p)), Math.Min(w, h) / 2);
            // ponytail: reach y drop usan la curva del estado actual; el extra (+18 = 25-30%)
            // solo aplica en expandido (escala con p), en compacto no se inyecta ancho.
            double filletNow = Lerp(Math.Clamp(SettingsManager.Current.IslandNotchFilletCompact, 0, 20), Math.Clamp(SettingsManager.Current.IslandNotchFilletExpanded, 0, 20), Smooth01(p));
            double earReach = (Math.Clamp(filletNow, 0, 20) + 18 * Smooth01(p)) * stretchT;
            earReach = Math.Min(earReach, Math.Max(0, (Width - w) / 2 - 2));
            notchFillet = Math.Clamp(filletNow, 0, 20) * stretchT;
            IslandBox.CornerRadius = new CornerRadius(0);
            IslandBox.Width = w + 2 * earReach;
            IslandBox.Height = h;
            // ponytail: las orejas son solo fondo; el contenido vive en el ancho lógico w.
            ExpandedLayer.Width = w;
            ExpandedLayer.Margin = new Thickness(0, _expandedMarginOrig.Top, 0, _expandedMarginOrig.Bottom);
            ExpandedLayer.HorizontalAlignment = HorizontalAlignment.Center;
            IslandBox.RenderTransformOrigin = new Point(0.5, 0);
            BoxScale.ScaleX = BoxScale.ScaleY = Lerp(0.68, 1, Smooth01(dotT));
            IslandBox.Clip = CreateNotchClip(IslandBox.Width, h, radius, earReach, notchFillet);
            LayoutBackground(IslandBox.Width, h);
        }
        else
        {
            // Pill: oculto -> punto 26px (circular) -> compacto (o pieza inactiva
            // más estrecha, 001 MOD RF-11) -> ancho expandido configurado.
            const double pillDot = 26;
            double dotT = Math.Clamp(q / 0.32, 0, 1);
            double stretchT = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double restW = _inactiveShown ? InactivePillWidth : CompactPillWidth;
            double baseW = q < 0.32 ? pillDot : Lerp(pillDot, restW, stretchT);
            w = Lerp(baseW, ExpandedIslandWidth, Smooth01(p));
            h = Lerp(34, _hexpShown, Smooth01(p));
            // Hover vivo: crece desde el punto y en reposo (también en compacto).
            w += hotW * (1 - Smooth01(p));
            IslandBox.Width = w;
            IslandBox.Height = h;
            ExpandedLayer.Width = double.NaN;
            ExpandedLayer.Margin = _expandedMarginOrig;
            ExpandedLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
            IslandBox.Opacity = Smooth01(Math.Clamp(q / 0.38, 0, 1)) * exitTailOpacity;
            // Radio: círculo perfecto mientras es punto, luego morph compacto->expandido.
            // En expandido (p>0.02) siempre pill con el radio de expandido.
            double morphR = Lerp(IslandCompactRadius, IslandExpandedRadius, Smooth01(p));
            double cr = baseW <= pillDot + 0.5 && p < 0.02
                ? pillDot / 2
                : Math.Min(morphR, Math.Min(w, h) / 2);
            IslandBox.CornerRadius = new CornerRadius(cr);
            BoxTranslate.Y = 0;
            BoxScale.ScaleX = BoxScale.ScaleY = Lerp(0.68, 1, Smooth01(dotT));
            IslandBox.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (!notch)
        {
            ApplyIslandClip(w, h, IslandBox.CornerRadius);
            LayoutBackground(w, h);
        }

        // Crossfade de capas + morph del contenido (Apple: el álbum y el título respiran)
        // En pill, los elementos divergen desde el centro durante el estiramiento
        double compactOp, expandedOp;
        if (notch)
        {
            double stretchT2 = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double contentT = Math.Clamp((stretchT2 - 0.42) / 0.58, 0, 1);
            double dotT2 = Math.Clamp(q / 0.32, 0, 1);
            compactOp = (1 - Smooth01(Math.Clamp(p * 2.2, 0, 1))) * Smooth01(contentT);
            if (q < 0.32) compactOp = 0;
            else compactOp *= Lerp(0.85, 1, dotT2);
            expandedOp = Smooth01(Math.Clamp((p - 0.12) / 0.88, 0, 1));
            CompactLayer.Opacity = compactOp;
            CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.88, 1, Smooth01(contentT));
            if (q < 0.32)
                CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.75, 0.88, dotT2);

            // El contenido también florece desde el centro durante el reveal.
            double diverge = Math.Pow(Smooth01(stretchT2), 1.25);
            CompactArtTranslate.X = Lerp(42, 0, diverge);
            CompactTitleTranslate.X = Lerp(6, 0, diverge);
            CompactEqTranslate.X = Lerp(-36, 0, diverge);
            CompactTitleScale2.ScaleX = CompactTitleScale2.ScaleY = Lerp(0.92, 1, diverge);
            double titleOp = Smooth01(Math.Clamp((stretchT2 - 0.50) / 0.50, 0, 1));
            double eqOp = Smooth01(Math.Clamp((stretchT2 - 0.55) / 0.45, 0, 1));
            CompactTitle.Opacity = q < 0.32 ? 0 : titleOp;
            CompactEq.Opacity = q < 0.32 ? 0 : eqOp;
            CompactArtWrap.Opacity = q < 0.15 ? 0 : (q < 0.32 ? Smooth01(dotT2) : 1);
            double pScale = Lerp(1, 0.92, Smooth01(p));
            CompactScale.ScaleX *= pScale;
            CompactScale.ScaleY *= pScale;
        }
        else
        {
            double stretchT2 = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double contentT = Math.Clamp((stretchT2 - 0.42) / 0.58, 0, 1);
            double dotT2 = Math.Clamp(q / 0.32, 0, 1);
            compactOp = (1 - Smooth01(Math.Clamp(p * 2.2, 0, 1))) * Smooth01(contentT);
            if (q < 0.32) compactOp = 0;
            else compactOp *= Lerp(0.85, 1, dotT2);
            expandedOp = Smooth01(Math.Clamp((p - 0.12) / 0.88, 0, 1));
            CompactLayer.Opacity = compactOp;
            CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.88, 1, Smooth01(contentT));
            if (q < 0.32)
                CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.75, 0.88, dotT2);

            // Diverge from center — más apiñado al centro
            double diverge = Math.Pow(Smooth01(stretchT2), 1.25);
            CompactArtTranslate.X = Lerp(42, 0, diverge);
            CompactTitleTranslate.X = Lerp(6, 0, diverge);
            CompactEqTranslate.X = Lerp(-36, 0, diverge);
            CompactTitleScale2.ScaleX = CompactTitleScale2.ScaleY = Lerp(0.92, 1, diverge);
            double titleOp = Smooth01(Math.Clamp((stretchT2 - 0.50) / 0.50, 0, 1));
            double eqOp = Smooth01(Math.Clamp((stretchT2 - 0.55) / 0.45, 0, 1));
            CompactTitle.Opacity = q < 0.32 ? 0 : titleOp;
            CompactEq.Opacity = q < 0.32 ? 0 : eqOp;
            CompactArtWrap.Opacity = q < 0.15 ? 0 : (q < 0.32 ? Smooth01(dotT2) : 1);
        }
        // En inactivo no hay contenido: la pieza negra va sola, sin vistas ni
        // restos de la funcionalidad anterior (001 MOD RF-11).
        double inactiveFade = _inactiveShown ? 0 : 1;
        compactOp *= inactiveFade;
        expandedOp *= inactiveFade;
        // El fondo (portada difuminada) también es contenido: sin esto la pieza
        // inactiva arrastraba la carátula de la última funcionalidad (001 MOD RF-11).
        bool bgWanted = !_inactiveShown;
        if ((BackgroundCanvas.Visibility == Visibility.Visible) != bgWanted)
            BackgroundCanvas.Visibility = bgWanted ? Visibility.Visible : Visibility.Collapsed;
        CompactLayer.IsHitTestVisible = p < 0.6 && q > 0.35 && !_inactiveShown;
        ExpandedLayer.Opacity = expandedOp * (notch ? q : 1);
        ExpandedLayer.IsHitTestVisible = p > 0.4 && q > 0.4 && !_inactiveShown;

        double artS = Lerp(0.88, 1, Smooth01(Math.Clamp((p - 0.05) / 0.95, 0, 1)));
        // Pop suma un leve bump al arte/título en cambio de pista
        artS += pop * 0.06;
        ExpandedArtScale.ScaleX = ExpandedArtScale.ScaleY = artS;

        double titleS = Lerp(0.90, 1, Smooth01(Math.Clamp((p - 0.08) / 0.9, 0, 1))) + pop * 0.05;
        SongTitleScale.ScaleX = SongTitleScale.ScaleY = titleS;
        SongTitle.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.12) / 0.7, 0, 1)));
        SongTitleTranslate.Y = Lerp(6, 0, Smooth01(Math.Clamp((p - 0.12) / 0.7, 0, 1)));

        SongArtist.Opacity = Lerp(0, _islandArtistOpacity, Smooth01(Math.Clamp((p - 0.22) / 0.6, 0, 1)));
        SongArtistTranslate.Y = Lerp(6, 0, Smooth01(Math.Clamp((p - 0.22) / 0.6, 0, 1)));

        ExpandedEq.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.18) / 0.6, 0, 1)));
        SeekRow.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.30) / 0.5, 0, 1)));
        SeekTranslate.Y = Lerp(8, 0, Smooth01(Math.Clamp((p - 0.30) / 0.5, 0, 1)));
        ControlsRow.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.38) / 0.5, 0, 1)));
        ControlsTranslate.Y = Lerp(8, 0, Smooth01(Math.Clamp((p - 0.38) / 0.5, 0, 1)));
    }

    private void PlayTrackPop()
    {
        if (!AnimationsEnabled || _expanded) return;
        if (!IsBoxShown || _q < 0.6) return;
        if (_popPlaying) return;
        _popVersion++;
        _pop = 0; _popV = 0; _popPhase = 0; _popPlaying = true;
        EnsureLoop();
    }

    // --- presentación ---

    private void RefreshUi(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null, bool forceAlbumFlip = false)
    {
        // Alerta modal del timer: los eventos de música esperan a X o reinicio.
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        // Evento multimedia: el contenido más reciente manda (spec 001 RF-24).
        _timerMode = 0;
        ApplyTimerContentVisibility();
        var status = knownStatus ?? SafeStatus(session) ?? _lastStatus;
        if (status != null) _lastStatus = status;
        PaintGlyph();
        BitmapImage? art = null;
        int thumbHash = 0;
        string title = "Título desconocido", artist = "Artista desconocido";
        try
        {
            var props = session.ControlSession.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
            if (props != null)
            {
                if (!string.IsNullOrWhiteSpace(props.Title)) title = props.Title;
                if (!string.IsNullOrWhiteSpace(props.Artist)) artist = props.Artist;
                if (props.Thumbnail != null)
                {
                    try { thumbHash = BitmapHelper.GetStableThumbnailHash(props.Thumbnail); } catch { thumbHash = 0; }
                    art = thumbHash != 0
                        ? BitmapHelper.GetThumbnailWithHash(props.Thumbnail, thumbHash)
                        : BitmapHelper.GetThumbnail(props.Thumbnail);
                    // Fallback if hash path missed the cache and re-read failed
                    if (art == null && thumbHash != 0)
                        art = BitmapHelper.GetThumbnail(props.Thumbnail);
                }
            }
        }
        catch { }
        SongTitle.Text = title;
        SongArtist.Text = artist;
        CompactTitle.Text = title;
        SetBackground(art);
        // Include actual thumbnail hash so a thumbnail-only change (Chrome fires
        // title first with stale art, then thumbnail late) is detected and not
        // swallowed while a flip animation is in flight.
        string trackKey = title + "\n" + artist + "\n" + thumbHash;
        bool trackChanged = _lastTrackKey != "" && trackKey != _lastTrackKey;
        bool artChanged = !ReferenceEquals(art, _displayedAlbumArt);
        _lastTrackKey = trackKey;
        if (trackChanged || artChanged || forceAlbumFlip)
            StartAlbumFlip(art);
        else if (!_albumFlipRunning)
            SetAlbumArt(art);
        if (trackChanged && SettingsManager.Current.IslandShowOnTrackChange) PlayTrackPop();
        BitmapHelper.GetDominantColors();
        ApplyCapabilities(session);
        UpdateSeek(session);
        UpdateEqButton();
        SyncMeasuredHeight();
        if (_expanded || _p > 0.05) ApplyFrame();
    }

    private void SetAlbumArt(BitmapImage? art)
    {
        StopAlbumFlip();
        CompactArt.Source = art;
        ExpandedArt.Source = art;
        _displayedAlbumArt = art;
        _hasAlbumCover = art != null;
        CompactNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
        ExpandedNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
        UpdateAlbumArtOverlay();
    }

    private void StartAlbumFlip(BitmapImage? art)
    {
        StopAlbumFlip();
        if (!AnimationsEnabled)
        {
            SetAlbumArt(art);
            return;
        }

        _albumFlipRunning = true;
        int version = _albumFlipVersion;
        CompactArtFlipScale.ScaleX = ExpandedArtFlipScale.ScaleX = 1;
        CompactArtFlipScale.ScaleY = ExpandedArtFlipScale.ScaleY = 1;
        _hasAlbumCover = _displayedAlbumArt != null;
        UpdateAlbumArtOverlay();

        var outgoing = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        outgoing.Completed += (_, _) =>
        {
            if (version != _albumFlipVersion) return;
            CompactArt.Source = art;
            ExpandedArt.Source = art;
            _displayedAlbumArt = art;
            _hasAlbumCover = art != null;
            CompactNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
            ExpandedNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
            UpdateAlbumArtOverlay();

            var incoming = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(170),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            incoming.Completed += (_, _) =>
            {
                if (version != _albumFlipVersion) return;
                _albumFlipRunning = false;
                CompactArtFlipScale.ScaleX = ExpandedArtFlipScale.ScaleX = 1;
                CompactArtFlipScale.ScaleY = ExpandedArtFlipScale.ScaleY = 1;
                UpdateAlbumArtOverlay();
            };
            CompactArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, incoming);
            ExpandedArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, incoming.Clone());
        };

        CompactArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, outgoing);
        ExpandedArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, outgoing.Clone());
    }

    private void StopAlbumFlip()
    {
        _albumFlipVersion++;
        _albumFlipRunning = false;
        CompactArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ExpandedArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactArtFlipScale.ScaleX = CompactArtFlipScale.ScaleY = 1;
        ExpandedArtFlipScale.ScaleX = ExpandedArtFlipScale.ScaleY = 1;
    }

    private void ApplyCapabilities(MediaSession session)
    {
        bool canPlay = false, canPrev = false, canNext = false, canSeek = false;
        try
        {
            var c = session.ControlSession.GetPlaybackInfo()?.Controls;
            if (c != null)
            {
                canPlay = c.IsPlayEnabled || c.IsPauseEnabled;
                canPrev = c.IsPreviousEnabled;
                canNext = c.IsNextEnabled;
            }
            canSeek = session.ControlSession.GetTimelineProperties().MaxSeekTime.TotalSeconds >= 1;
        }
        catch { }
        SeekRow.Visibility = canSeek ? Visibility.Visible : Visibility.Hidden;
        BtnPlay.IsEnabled = canPlay;
        BtnPlay.Opacity = canPlay ? 1 : 0.35;
        BtnPrev.IsEnabled = canPrev;
        BtnPrev.Opacity = canPrev ? 1 : 0.35;
        BtnNext.IsEnabled = canNext;
        BtnNext.Opacity = canNext ? 1 : 0.35;
    }

    private void PaintGlyph()
    {
        if (_lastStatus == null) return;
        PlayGlyph.Symbol = _lastStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? Wpf.Ui.Controls.SymbolRegular.Pause16 : Wpf.Ui.Controls.SymbolRegular.Play16;
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus? SafeStatus(MediaSession session)
    {
        try { return session.ControlSession?.GetPlaybackInfo()?.PlaybackStatus; }
        catch { return null; }
    }

    private static string Fmt(TimeSpan t) => t.ToString(t.Hours > 0 ? @"h\:mm\:ss" : @"m\:ss");

    private static string FmtRemaining(TimeSpan t) => "-" + Fmt(t < TimeSpan.Zero ? TimeSpan.Zero : t);

    private void UpdateTimelineVisual()
    {
        double maximum = Seekbar.Maximum;
        double ratio = maximum > 0 ? Math.Clamp(Seekbar.Value / maximum, 0, 1) : 0;
        TimelineProgress.Width = TimelineTrack.ActualWidth * ratio;
    }

    private void TimelineHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineVisual();

    private void Seekbar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTimelineVisual();
        if (!_drag || Seekbar.Maximum <= 0) return;
        var position = TimeSpan.FromSeconds(Math.Clamp(Seekbar.Value, 0, Seekbar.Maximum));
        PosText.Text = Fmt(position);
        DurText.Text = FmtRemaining(TimeSpan.FromSeconds(Seekbar.Maximum) - position);
    }

    private void UpdateSeek(MediaSession session)
    {
        try
        {
            var tl = session.ControlSession.GetTimelineProperties();
            if (tl.MaxSeekTime.TotalSeconds >= 1)
            {
                bool playing = session.ControlSession.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var pos = playing ? tl.Position + (DateTime.Now - tl.LastUpdatedTime.DateTime) : tl.Position;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (pos > tl.EndTime) pos = tl.EndTime;
                Seekbar.Maximum = tl.MaxSeekTime.TotalSeconds;
                if (!_drag) { Seekbar.Value = pos.TotalSeconds; PosText.Text = Fmt(pos); }
                DurText.Text = FmtRemaining(tl.MaxSeekTime - pos);
                UpdateTimelineVisual();
                return;
            }
        }
        catch { }
        Seekbar.Maximum = 100; Seekbar.Value = 0; PosText.Text = "0:00"; DurText.Text = "0:00";
        UpdateTimelineVisual();
    }

    private void Tick()
    {
        if (!SettingsManager.Current.IslandEnabled)
        {
            _wasSuppressed = false;
            SnapHidden();
            Visibility = Visibility.Collapsed;
            return;
        }
        // Exclusiva persistente (002 RF-2) atraviesa supresión (001 RF-8/14).
        if (Suppressed() && !HasExclusive())
        {
            if (!_wasSuppressed)
            {
                _wasSuppressed = true;
                if (AnimationsEnabled && IsBoxShown)
                {
                    Visibility = Visibility.Visible;
                    GoHidden();
                    return;
                }
                SnapHidden();
            }
            else if (IsBoxShown)
            {
                GoHidden();
            }
            if (!IsBoxShown && !_loopOn) Visibility = Visibility.Collapsed;
            return;
        }
        if (_wasSuppressed)
        {
            _wasSuppressed = false;
            // Al salir de supresión, la exclusiva ya estaba visible si atravesó.
            if (HasExclusive() && IsBoxShown) { }
            else if (_pendingTimerAlert && SettingsManager.Current.IslandEnabled) { _pendingTimerAlert = false; ShowTimerAlert(); }
            else if (TimerKeepsAlive()) ShowTimerCompact();
            else
                RefreshVisibilityState();
        }
        else if (Suppressed() && HasExclusive() && !IsBoxShown)
        {
            // Exclusiva llegó estando suprimido: desplegarla aunque siga la supresión.
            ShowTimerAlert();
        }
        SyncExistingMediaState();
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        if (_expanded && !_drag && !_reelDragging && !IsMouseOverBoxOrStrip()) LeaveHover();
        SyncEq();
        _timer.Poll(DateTime.UtcNow);
        UpdateArrows();
        if (_timerMode == 1 && IsBoxShown)
        {
            RefreshTimerUI();
            EnsureTimerContentShown();
        }
        var s = Current();
        if (s != null && _expanded) UpdateSeek(s);
        UpdateLine();
    }

    private bool IsMouseOverBoxOrStrip()
    {
        try
        {
            if (IsMouseOver) return true;
            if (IslandBox.IsMouseOver || HoverStrip.IsMouseOver) return true;
            if (!NativeMethods.GetCursorPos(out var p)) return false;
            var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
            if (primary.monitorArea.Width == 0) return false;
            double tolH = HoverTolH * primary.dpiX / 96.0;
            double tolV = HoverTolV * primary.dpiY / 96.0;
            double cx = primary.workArea.Left + primary.workArea.Width / 2;
            double halfW = (_expanded || _p > 0.2)
                ? ExpandedIslandWidth * 0.5 * primary.dpiX / 96.0 + tolH
                : LineFullWidth * 0.5 * primary.dpiX / 96.0 + tolH;
            if (Math.Abs(p.X - cx) > halfW) return false;
            if (_expanded || _p > 0.2)
            {
                double top = primary.workArea.Top;
                double islandOff = (IsNotch ? 0 : Math.Clamp(SettingsManager.Current.IslandTopOffset, 0, 80)) * primary.dpiY / 96.0;
                double bottom = top + (_hexp + 8) * primary.dpiY / 96.0 + islandOff + tolV;
                if (p.Y < top - 2 || p.Y > bottom) return false;
                return true;
            }
            double lineTop = primary.workArea.Top + (IsNotch ? 1 : Math.Clamp(SettingsManager.Current.IslandLineTopOffset, 0, 60)) * primary.dpiY / 96.0;
            if (p.Y < primary.monitorArea.Top - 2 || p.Y > lineTop + 3 + tolV) return false;
            return true;
        }
        catch { return false; }
    }

    private void SyncEq()
    {
        bool want = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandEqEnabled;
        if (want && !_eqRunning) { _eqRunning = true; _eqBars = -1; _eq.Start(); }
        else if (!want && _eqRunning) { _eqRunning = false; _eq.Stop(); }
        int bars = Math.Clamp(SettingsManager.Current.IslandEqBarCount, 1, 10);
        if (_eqRunning && bars != _eqBars) { _eqBars = bars; _eq.ResizeBarList(bars); }
    }

    private void UpdateLine()
    {
        ApplyStyle();
        // ponytail: offsets solo en flotante; notch queda pegado como antes
        int lineOff = IsNotch ? 1 : Math.Clamp(SettingsManager.Current.IslandLineTopOffset, 0, 60);
        int islandOff = IsNotch ? 0 : Math.Clamp(SettingsManager.Current.IslandTopOffset, 0, 80);
        ActivityLine.Margin = new Thickness(0, lineOff, 0, 0);
        MediaStatusDot.Margin = new Thickness(0, lineOff, 0, 0);
        IslandBox.Margin = new Thickness(0, islandOff, 0, 0);
        // Ventana arranca en workArea.Top, pero HoverStrip pilla desde el borde físico vía PollFringe;
        // aquí cubre al menos borde→línea + V abajo, centrado al ancho H.
        HoverStrip.Margin = new Thickness(0, 0, 0, 0);
        HoverStrip.Height = lineOff + 3 + HoverTolV;
        HoverStrip.Width = LineFullWidth + 2 * HoverTolH;
        HoverStrip.HorizontalAlignment = HorizontalAlignment.Center;
        HoverStrip.VerticalAlignment = VerticalAlignment.Top;
        var eqVis = SettingsManager.Current.IslandEqEnabled ? Visibility.Visible : Visibility.Collapsed;
        ExpandedEq.Visibility = eqVis;
        UpdateEqButton(); // arbitra CompactEq vs icono de pausa
        UpdateMediaStatusDot();
    }

    private bool IsAliveForLine() =>
        IsBoxShown || _qT > 0.02 || _music != null || TimerKeepsAlive();

    private const double LineFullWidth = 120;
    private double _lineW = LineFullWidth;

    /// <summary>El punto de estado solo con contenido visible y sin pieza inactiva.</summary>
    private void UpdateMediaStatusDot()
    {
        // Coupled to the activity line: if the line is off, the dot must not show either.
        if (!SettingsManager.Current.IslandActivityLine || _inactiveShown || !IsAliveForLine())
        {
            MediaStatusDot.Visibility = Visibility.Collapsed;
            return;
        }
        bool hidden = Visibility == Visibility.Visible && !IsBoxShown && !Suppressed();
        // Punto de estado estrictamente multimedia (001 MOD RF-20): solo aparece
        // con un snapshot musical real (reproducción o pausa); con solo el
        // temporizador disponible permanece oculto, sin consultar el control multimedia.
        var status = _music?.Status;
        if (!hidden || status == null)
        {
            MediaStatusDot.Visibility = Visibility.Collapsed;
            return;
        }

        MediaStatusDot.Background = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? MediaPlayingBrush
            : MediaPausedBrush;
        MediaStatusDot.Visibility = Visibility.Visible;
    }

    private int _appliedStyle = -1;

    private void ApplyStyle()
    {
        int style = Math.Clamp(SettingsManager.Current.IslandStyle, 0, 1);
        if (style != _appliedStyle)
        {
            _appliedStyle = style;
            if (style == 1)
            {
                IslandBox.BorderThickness = new Thickness(1, 0, 1, 1);
                CompactLayer.Width = 200;
            }
            else
            {
                IslandBox.BorderThickness = new Thickness(1);
                CompactLayer.Width = 240;
            }
        }

        IslandBox.BorderBrush = SettingsManager.Current.IslandBorderEnabled ? IslandBorderBrush : Brushes.Transparent;

        // CornerRadius lo gobierna ApplyFrame por frame (punto 26→cápsula)
        SyncMeasuredHeight();
        if (!_loopOn) ApplyFrame();
    }

    // Tipografía del Island (misma resolución que el widget: incluidas por pack URI,
    // resto como fuente del sistema). Una sola familia compartida por los 3 textos.
    private static FontFamily IslandFontFamily =>
        WidgetFonts.Resolve(SettingsManager.Current.IslandFontFamily);

    private static int IslandCompactTitleSize =>
        Math.Clamp(SettingsManager.Current.IslandCompactTitleFontSize, 10, 24);

    private static int IslandExpandedTitleSize =>
        Math.Clamp(SettingsManager.Current.IslandExpandedTitleFontSize, 10, 24);

    private static int IslandExpandedArtistSize =>
        Math.Clamp(SettingsManager.Current.IslandExpandedArtistFontSize, 10, 24);

    // Mismo mapping de preset que el widget (0 Moderno, 1 Clásico, 2 Audaz, 3 Suave).
    private static int IslandTitleWeight => SettingsManager.Current.IslandTextStyle switch
    {
        1 => 400,
        2 => 700,
        3 => 500,
        _ => 600,
    };

    private static int IslandArtistWeight =>
        SettingsManager.Current.IslandTextStyle == 2 ? 600 : 400;

    private static double IslandArtistOpacity => SettingsManager.Current.IslandTextStyle switch
    {
        1 => 0.5,
        2 => 0.85,
        3 => 0.6,
        _ => 0.65,
    };

    private static bool IslandArtistItalic =>
        SettingsManager.Current.IslandTextStyle == 3;

    private double _islandArtistOpacity = 0.65;

    private static FontWeight ToIslandFontWeight(int weight) => weight switch
    {
        >= 700 => FontWeights.Bold,
        >= 600 => FontWeights.SemiBold,
        >= 500 => FontWeights.Medium,
        _ => FontWeights.Normal,
    };

    /// <summary>
    /// Aplica la tipografía del Island (familia compartida, preset de estilo y los
    /// 3 tamaños: canción compacta, canción expandida, autor expandido). Se llama
    /// al arrancar y en cada <see cref="RefreshAppearance"/>; re-mide la altura
    /// expandida porque depende de la fuente.
    /// </summary>
    public void ApplyIslandTextStyle()
    {
        FontFamily family = IslandFontFamily;

        CompactTitle.FontFamily = family;
        SongTitle.FontFamily = family;
        SongArtist.FontFamily = family;

        CompactTitle.FontSize = IslandCompactTitleSize;
        SongTitle.FontSize = IslandExpandedTitleSize;
        SongArtist.FontSize = IslandExpandedArtistSize;

        FontWeight titleWeight = ToIslandFontWeight(IslandTitleWeight);
        CompactTitle.FontWeight = titleWeight;
        SongTitle.FontWeight = titleWeight;
        SongArtist.FontWeight = ToIslandFontWeight(IslandArtistWeight);
        SongArtist.FontStyle = IslandArtistItalic ? FontStyles.Italic : FontStyles.Normal;
        _islandArtistOpacity = IslandArtistOpacity;

        SyncMeasuredHeight();
    }

    private void ApplyAlbumArtRadius()
    {
        double radius = Math.Clamp(SettingsManager.Current.IslandAlbumArtRadius, 0, 32);
        double compactRadius = Math.Min(radius, 11);
        double expandedRadius = Math.Min(radius, 32);
        var compactCorners = new CornerRadius(compactRadius);
        var expandedCorners = new CornerRadius(expandedRadius);

        CompactArtWrap.CornerRadius = compactCorners;
        CompactAlbumOverlay.CornerRadius = compactCorners;
        CompactArtWrap.Clip = CreateAlbumArtClip(22, compactRadius);
        CompactAlbumOverlay.Clip = CreateAlbumArtClip(22, compactRadius);

        ExpandedArtWrap.CornerRadius = expandedCorners;
        ExpandedAlbumOverlay.CornerRadius = expandedCorners;
        ExpandedArtWrap.Clip = CreateAlbumArtClip(48, expandedRadius);
        ExpandedAlbumOverlay.Clip = CreateAlbumArtClip(48, expandedRadius);
    }

    private static RectangleGeometry CreateAlbumArtClip(double size, double radius)
    {
        var clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
        clip.Freeze();
        return clip;
    }

    public void UpdateBackgroundMode()
    {
        ApplyBackgroundSettings();

        bool enabled = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandBackgroundBlur;
        if (!enabled || _backgroundIcon == null)
        {
            StopBackgroundRotation();
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundImageNext.Visibility = Visibility.Collapsed;
            return;
        }

        BackgroundImage.Visibility = Visibility.Visible;
        if (SettingsManager.Current.IslandBackgroundRotate)
        {
            ApplyBackgroundRotation();
        }
        else
        {
            StopBackgroundRotation();
            LayoutBackgroundToFill();
            BeginBackgroundCrossfade(_backgroundIcon, Math.Max(MainWindow.getDuration(), 1));
        }

        UpdateRotationPauseState();
    }

    public void RefreshBackgroundRotationFrameRate()
    {
        if (!SettingsManager.Current.IslandBackgroundBlur ||
            !SettingsManager.Current.IslandBackgroundRotate ||
            !_backgroundRotationActive ||
            _backgroundRotateTransform == null)
            return;

        ApplyBackgroundRotation(_backgroundRotateTransform.Angle);
    }

    private void ApplyBackgroundSettings()
    {
        double opacity = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurIntensity, 0, 100) / 100.0;
        double radius = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurRadius, 0, 150);
        // ponytail: no tocar opacidades a mitad de fade, el snap se vería como parpadeo
        if (_backgroundCrossfadeTarget != null)
        {
            BackgroundImageBlurEffect.Radius = radius;
            BackgroundImageNextBlurEffect.Radius = radius;
            return;
        }
        BackgroundImage.Opacity = opacity;
        BackgroundImageNext.Opacity = 0;
        BackgroundImageBlurEffect.Radius = radius;
        BackgroundImageNextBlurEffect.Radius = radius;
    }

    private void ApplyBackgroundRotation(double? forcedStartAngle = null)
    {
        double width = IslandBox.Width > 0 ? IslandBox.Width : 240;
        double height = IslandBox.Height > 0 ? IslandBox.Height : 34;
        _backgroundRotationActive = true;

        _backgroundRotateTransform ??= new RotateTransform();
        BackgroundImage.RenderTransform = _backgroundRotateTransform;
        BackgroundImageNext.RenderTransform = _backgroundRotateTransform;
        BackgroundImage.Effect = null;
        BackgroundImageNext.Effect = null;
        BackgroundImage.CacheMode ??= new BitmapCache(0.5);
        BackgroundImageNext.CacheMode ??= new BitmapCache(0.5);

        double sizeMultiplier = Math.Max(SettingsManager.Current.IslandBackgroundRotateSize, 100) / 100.0;
        double discSide = Math.Max(Math.Max(ExpandedIslandWidth * sizeMultiplier, height * sizeMultiplier), ExpandedIslandWidth);
        double offsetX = discSide * 0.28;
        bool showLeftSide = SettingsManager.Current.IslandBackgroundRotateSide == 0;
        LayoutDiscLayer(BackgroundImage, width, height, discSide, offsetX, showLeftSide);
        LayoutDiscLayer(BackgroundImageNext, width, height, discSide, offsetX, showLeftSide);

        if (_backgroundIcon != null)
            UpdateBakedBackgroundAsync(_backgroundIcon, discSide);

        if (_backgroundRotationPaused)
            return;

        double durationSeconds = Math.Max(SettingsManager.Current.IslandBackgroundRotateDuration, 1);
        bool spinUp = SettingsManager.Current.IslandBackgroundRotateDirection == 1;
        int? desiredFrameRate = SettingsManager.Current.IslandBackgroundRotateHighRefreshRate ? null : 30;
        bool restart = forcedStartAngle.HasValue ||
                       !_backgroundRotationAnimationRunning ||
                       _backgroundRotationWasUp != spinUp ||
                       Math.Abs(_appliedRotationDurationSeconds - durationSeconds) > 0.01 ||
                       _appliedDesiredFrameRate != desiredFrameRate;
        if (!restart) return;

        double startAngle = forcedStartAngle ?? _backgroundRotateTransform.Angle;
        _backgroundRotationWasUp = spinUp;
        _backgroundRotationAnimationRunning = true;
        _appliedRotationDurationSeconds = durationSeconds;
        _appliedDesiredFrameRate = desiredFrameRate;
        var animation = new DoubleAnimation
        {
            From = startAngle,
            To = spinUp ? startAngle - 360 : startAngle + 360,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(animation, desiredFrameRate);
        _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void LayoutBackground(double width, double height)
    {
        BackgroundCanvas.Width = width;
        BackgroundCanvas.Height = height;
        if (_backgroundRotationActive)
        {
            double sizeMultiplier = Math.Max(SettingsManager.Current.IslandBackgroundRotateSize, 100) / 100.0;
            double discSide = Math.Max(Math.Max(ExpandedIslandWidth * sizeMultiplier, height * sizeMultiplier), ExpandedIslandWidth);
            double offsetX = discSide * 0.28;
            bool showLeftSide = SettingsManager.Current.IslandBackgroundRotateSide == 0;
            LayoutDiscLayer(BackgroundImage, width, height, discSide, offsetX, showLeftSide);
            LayoutDiscLayer(BackgroundImageNext, width, height, discSide, offsetX, showLeftSide);
        }
        else
        {
            LayoutBackgroundToFill();
        }
    }

    private void LayoutBackgroundToFill()
    {
        double width = IslandBox.Width > 0 ? IslandBox.Width : 240;
        double height = IslandBox.Height > 0 ? IslandBox.Height : 34;
        double side = Math.Max(Math.Max(width, height), 1);
        BackgroundCanvas.Width = width;
        BackgroundCanvas.Height = height;
        LayoutFillLayer(BackgroundImage, width, height, side);
        LayoutFillLayer(BackgroundImageNext, width, height, side);
    }

    private static void LayoutFillLayer(Image layer, double width, double height, double side)
    {
        layer.Width = side;
        layer.Height = side;
        layer.Margin = new Thickness(0);
        layer.Stretch = Stretch.UniformToFill;
        Canvas.SetLeft(layer, (width - side) / 2);
        Canvas.SetTop(layer, (height - side) / 2);
    }

    private static void LayoutDiscLayer(Image layer, double width, double height, double discSide, double offsetX, bool showLeftSide)
    {
        layer.Width = discSide;
        layer.Height = discSide;
        layer.Margin = new Thickness(0);
        layer.Stretch = Stretch.Fill;
        Canvas.SetLeft(layer, (width - discSide) / 2 + (showLeftSide ? offsetX : -offsetX));
        Canvas.SetTop(layer, (height - discSide) / 2);
    }

    private void SetBackground(BitmapImage? icon)
    {
        if (!ReferenceEquals(_backgroundIcon, icon))
        {
            _backgroundGeneration++;
            _bakingIcon = null;
        }
        _backgroundIcon = icon;
        if (icon == null)
        {
            StopBackgroundRotation();
            BackgroundImage.Source = null;
            ParkBackgroundNextLayer();
            BackgroundImage.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBackgroundMode();
    }

    private static BitmapSource? BakeBlurredBackground(BitmapImage icon, double discSide, double blurRadiusDips)
    {
        const int resolution = 256;
        double blurRadius = blurRadiusDips * resolution / Math.Max(discSide, 1);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawImage(icon, new Rect(0, 0, resolution, resolution));

        visual.Effect = new BlurEffect
        {
            Radius = blurRadius,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };

        var bitmap = new RenderTargetBitmap(resolution, resolution, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private async void UpdateBakedBackgroundAsync(BitmapImage icon, double discSide)
    {
        int version = _backgroundGeneration;
        double bakeSide = Math.Round(discSide / 16.0) * 16.0;
        int blurRadius = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurRadius, 0, 150);
        if (_bakedBackground != null && ReferenceEquals(_bakedIcon, icon) &&
            Math.Abs(_bakedSideDip - bakeSide) < 0.5 && _bakedBlurRadius == blurRadius)
        {
            if (_backgroundRotationActive && !ReferenceEquals(BackgroundImage.Source, _bakedBackground))
                BeginBackgroundCrossfade(_bakedBackground, Math.Max(MainWindow.getDuration(), 1));
            return;
        }

        if (ReferenceEquals(_bakingIcon, icon) && Math.Abs(_bakingSide - bakeSide) < 0.5 && _bakingBlurRadius == blurRadius)
            return;

        if (_bakedBackground == null)
        {
            BackgroundImage.Source = icon;
            BackgroundImage.Effect = BackgroundImageBlurEffect;
        }

        _bakingIcon = icon;
        _bakingSide = bakeSide;
        _bakingBlurRadius = blurRadius;
        BitmapSource? baked;
        try
        {
            baked = await Task.Run(() => BakeBlurredBackground(icon, bakeSide, blurRadius));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to bake Fluent Island background");
            if (ReferenceEquals(_bakingIcon, icon)) _bakingIcon = null;
            return;
        }

        if (_disposed || version != _backgroundGeneration || !ReferenceEquals(_backgroundIcon, icon) ||
            !ReferenceEquals(_bakingIcon, icon) || Math.Abs(_bakingSide - bakeSide) >= 0.5 || _bakingBlurRadius != blurRadius)
            return;
        if (ReferenceEquals(_bakingIcon, icon) && Math.Abs(_bakingSide - bakeSide) < 0.5)
            _bakingIcon = null;
        if (baked == null) return;

        _bakedIcon = icon;
        _bakedBackground = baked;
        _bakedSideDip = bakeSide;
        _bakedBlurRadius = blurRadius;
        if (_backgroundRotationActive)
            BeginBackgroundCrossfade(baked, Math.Max(MainWindow.getDuration(), 1));
    }

    private void BeginBackgroundCrossfade(BitmapSource target, int durationMs)
    {
        if (BackgroundImage.Source == null || !AnimationsEnabled || durationMs <= 1)
        {
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
            return;
        }

        if (ReferenceEquals(BackgroundImage.Source, target) && _backgroundCrossfadeTarget == null)
        {
            ParkBackgroundNextLayer();
            return;
        }

        if (ReferenceEquals(_backgroundCrossfadeTarget, target))
            return;

        _backgroundCrossfadeVersion++;
        int version = _backgroundCrossfadeVersion;
        _backgroundCrossfadeTarget = target;
        double bound = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurIntensity, 0, 100) / 100.0;
        BackgroundImage.BeginAnimation(OpacityProperty, null);
        BackgroundImageNext.BeginAnimation(OpacityProperty, null);
        BackgroundImage.Opacity = bound;
        BackgroundImageNext.Source = target;
        BackgroundImageNext.Visibility = Visibility.Visible;
        BackgroundImageNext.Opacity = 0;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = bound,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing
        };
        var fadeOut = new DoubleAnimation
        {
            From = bound,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing
        };
        fadeIn.Completed += (_, _) =>
        {
            if (version != _backgroundCrossfadeVersion) return;
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
        };
        BackgroundImage.BeginAnimation(OpacityProperty, fadeOut);
        BackgroundImageNext.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void ParkBackgroundNextLayer()
    {
        _backgroundCrossfadeTarget = null;
        double bound = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurIntensity, 0, 100) / 100.0;
        BackgroundImage.BeginAnimation(OpacityProperty, null);
        BackgroundImageNext.BeginAnimation(OpacityProperty, null);
        BackgroundImage.Opacity = bound;
        BackgroundImageNext.Visibility = Visibility.Collapsed;
        BackgroundImageNext.Opacity = 0;
    }

    private void CancelBackgroundCrossfade()
    {
        _backgroundCrossfadeVersion++;
        ParkBackgroundNextLayer();
    }

    private void UpdateRotationPauseState()
    {
        if (!_backgroundRotationActive) return;
        if (!SettingsManager.Current.IslandBackgroundRotate ||
            !SettingsManager.Current.IslandBackgroundBlur)
            return;

        if (_lastStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
            !IsVisible || !IsBoxShown)
            PauseBackgroundRotation();
        else
            ResumeBackgroundRotation();
    }

    private void PauseBackgroundRotation()
    {
        if (!_backgroundRotationAnimationRunning || _backgroundRotationPaused || _backgroundRotateTransform == null)
            return;

        _pausedRotationAngle = _backgroundRotateTransform.Angle;
        _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        _backgroundRotateTransform.Angle = _pausedRotationAngle;
        _backgroundRotationAnimationRunning = false;
        _backgroundRotationPaused = true;
    }

    private void ResumeBackgroundRotation()
    {
        if (!_backgroundRotationPaused) return;
        _backgroundRotationPaused = false;
        ApplyBackgroundRotation(_pausedRotationAngle);
    }

    private void StopBackgroundRotation()
    {
        _backgroundRotationActive = false;
        _backgroundRotationAnimationRunning = false;
        _backgroundRotationPaused = false;
        if (_backgroundRotateTransform != null)
        {
            _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            _backgroundRotateTransform.Angle = 0;
        }
        CancelBackgroundCrossfade();
        BackgroundImage.CacheMode = null;
        BackgroundImageNext.CacheMode = null;
        BackgroundImage.RenderTransform = Transform.Identity;
        BackgroundImageNext.RenderTransform = Transform.Identity;
        BackgroundImage.Effect = BackgroundImageBlurEffect;
        BackgroundImageNext.Effect = BackgroundImageNextBlurEffect;
    }

    private static Geometry CreateIslandClip(double width, double height, CornerRadius radius)
    {
        if (width <= 0 || height <= 0) return Geometry.Empty;
        double tl = Math.Clamp(radius.TopLeft, 0, Math.Min(width, height) / 2);
        double tr = Math.Clamp(radius.TopRight, 0, Math.Min(width, height) / 2);
        double br = Math.Clamp(radius.BottomRight, 0, Math.Min(width, height) / 2);
        double bl = Math.Clamp(radius.BottomLeft, 0, Math.Min(width, height) / 2);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(tl, 0), true, true);
            context.LineTo(new Point(width - tr, 0), true, false);
            AddCorner(context, new Point(width, tr), tr);
            context.LineTo(new Point(width, height - br), true, false);
            AddCorner(context, new Point(width - br, height), br);
            context.LineTo(new Point(bl, height), true, false);
            AddCorner(context, new Point(0, height - bl), bl);
            context.LineTo(new Point(0, tl), true, false);
            AddCorner(context, new Point(tl, 0), tl);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void AddCorner(StreamGeometryContext context, Point end, double radius)
    {
        if (radius <= 0.01)
            context.LineTo(end, true, false);
        else
            context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
    }

    // ponytail: notch pegado al borde con cueva (W incluye 2*reach de orejas).
    // reach = ancho horizontal (fijo: fillet expandido), drop = caída vertical (morph).
    private static Geometry CreateNotchClip(double width, double height, double bottomRadius, double reach, double drop)
    {
        if (width <= 0 || height <= 0) return Geometry.Empty;
        double br = Math.Clamp(bottomRadius, 0, Math.Min(width, height) / 2);
        double r = Math.Clamp(reach, 0, 24);
        double f = Math.Min(Math.Clamp(drop, 0, 20), Math.Max(0, height - br - 1));
        if (Math.Max(r, f) < 0.5)
            return CreateIslandClip(width, height, new CornerRadius(0, 0, br, br));
        double kx = 0.5523 * r, ky = 0.5523 * f; // aprox. cuarto de elipse
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(0, 0), true, true);
            context.LineTo(new Point(width, 0), true, false);
            context.BezierTo(new Point(width - kx, 0), new Point(width - r, f - ky), new Point(width - r, f), true, false);
            context.LineTo(new Point(width - r, height - br), true, false);
            AddCorner(context, new Point(width - r - br, height), br);
            context.LineTo(new Point(r + br, height), true, false);
            AddCorner(context, new Point(r, height - br), br);
            context.LineTo(new Point(r, f), true, false);
            context.BezierTo(new Point(r, f - ky), new Point(kx, 0), new Point(0, 0), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void ApplyIslandClip(double width, double height, CornerRadius radius)
    {
        IslandBox.Clip = CreateIslandClip(width, height, radius);
    }

    private void PositionTopCenter()
    {
        var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
        if (primary.monitorArea.Width == 0) return;
        double rawW = Width * primary.dpiX / 96.0;
        Left = (primary.workArea.Left + primary.workArea.Width / 2 - rawW / 2) * 96.0 / primary.dpiX;
        Top = primary.workArea.Top * 96.0 / primary.dpiY;
        WindowHelper.SetTopmost(this);
    }

    private bool HasExclusive() =>
        _features.Features.Any(f => f.State.Exclusive);

    private bool Suppressed()
    {
        if (FullscreenDetector.IsFullscreenApplicationRunning()) return true;
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && NativeMethods.GetWindowRect(fg, out var r))
            {
                // ponytail: el escritorio (Progman/WorkerW) cubre todo el monitor pero no es una app.
                var sb = new StringBuilder(256);
                if (NativeMethods.GetClassName(fg, sb, sb.Capacity) > 0)
                {
                    string cls = sb.ToString();
                    if (cls == "Progman" || cls == "WorkerW") return false;
                }
                var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
                if (primary.monitorArea.Width != 0 && r.Left <= primary.monitorArea.Left && r.Top <= primary.monitorArea.Top && r.Right >= primary.monitorArea.Right && r.Bottom >= primary.monitorArea.Bottom)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) { if (Current() is { } s) await s.ControlSession.TrySkipPreviousAsync(); }
    private async void PlayPause_Click(object sender, RoutedEventArgs e) { if (Current() is { } s) await s.ControlSession.TryTogglePlayPauseAsync(); }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (Current() is { } s) await s.ControlSession.TrySkipNextAsync(); }

    private void AlbumArt_MouseEnter(object sender, MouseEventArgs e)
    {
        _albumArtHovering = true;
        UpdateAlbumArtOverlay();
    }

    private void AlbumArt_MouseLeave(object sender, MouseEventArgs e)
    {
        _albumArtHovering = false;
        UpdateAlbumArtOverlay();
    }

    private void UpdateAlbumArtOverlay()
    {
        bool showChevron = _albumArtHovering && _hasAlbumCover && _main.GetTaskbarSessionCount() > 1;
        var visibility = showChevron ? Visibility.Visible : Visibility.Collapsed;
        CompactAlbumOverlay.Visibility = visibility;
        ExpandedAlbumOverlay.Visibility = visibility;
        CompactArt.Opacity = showChevron ? 0.4 : 1;
        ExpandedArt.Opacity = showChevron ? 0.4 : 1;
    }

    // En compacto manda el visualizador; en pausa lo reemplaza el icono
    // (clic al icono = reanudar + vuelve el visualizador).
    private void UpdateEqButton()
    {
        var s = AnySession();
        bool zone = !_expanded && SettingsManager.Current.IslandEqEnabled && s != null;
        bool paused = s != null && zone && SafeStatus(s) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        EqPlayPauseBtn.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        CompactEq.Visibility = paused ? Visibility.Collapsed
            : SettingsManager.Current.IslandEqEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EqZone_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PlayPause_Click(sender, e);
        // Flip optimista de vista; UpdateLine/Tick lo confirman.
        bool toIcon = EqPlayPauseBtn.Visibility != Visibility.Visible;
        EqPlayPauseBtn.Visibility = toIcon ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsManager.Current.IslandEqEnabled)
            CompactEq.Visibility = toIcon ? Visibility.Collapsed : Visibility.Visible;
    }

    // Clic en el título del compacto: abre el expandido de la última usable
    // (001 MOD RF-3); el burbujeo lo resolvería igual, pero se marca a mano.
    private void CompactMiddle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_expanded) return;
        e.Handled = true;
        ExpandLastUsable();
    }

    private void IslandBox_Wheel(object sender, MouseWheelEventArgs e)
    {
        // Temporizador: en expandido la rueda cambia de funcionalidad (002 MOD RF-3);
        // hacia arriba compacta sin cambiar de funcionalidad.
        if (_expanded && TimerModeAvailable())
        {
            if (e.OriginalSource is DependencyObject wheelSrc && (Seekbar.IsAncestorOf(wheelSrc) || TimerPresetList.IsAncestorOf(wheelSrc) || TimerConfigGrid.IsAncestorOf(wheelSrc))) return;
            if (e.Delta < 0) CycleMode();
            else if (e.Delta > 0) LeaveHover();
            e.Handled = true;
            return;
        }
        if (_expanded) return;
        // En compacto, la rueda hacia abajo es selección explícita de contenido
        // (equivale al clic, 001 MOD RF-3); hacia arriba ya está compacto.
        if (Math.Clamp(SettingsManager.Current.IslandExpandTrigger, 0, 2) is not (1 or 2)) return;
        if (e.OriginalSource is DependencyObject src && Seekbar.IsAncestorOf(src)) return;
        if (e.Delta < 0)
        {
            ExpandLastUsable();
            e.Handled = true;
        }
    }

    private void Album_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // el clic del álbum SOLO cambia de medio (001 MOD RF-5)
        var all = _main.mediaManager.CurrentMediaSessions.Values.Where(s => _main.IsSessionAllowed(s)).ToList();
        if (!MusicAvailable() || all.Count <= 1) return;
        int i = all.FindIndex(s => s.Id == _currentId);
        var next = all[(i + 1) % all.Count];
        _currentId = next.Id;
        _hideCts?.Cancel();
        if (_expanded) RefreshUi(next, null, true);
        else ShowMusicCompact(next, null, true);
    }

    private void Seekbar_Down(object sender, MouseButtonEventArgs e) { _drag = true; if (sender is Slider sl) { var p = e.GetPosition(sl); double ratio = sl.ActualWidth > 0 ? Math.Clamp(p.X / sl.ActualWidth, 0, 1) : 0; sl.Value = ratio * sl.Maximum; } }
    private async void Seekbar_Up(object sender, MouseButtonEventArgs e) { try { if (Current() is { } s && sender is Slider sl) { var pos = TimeSpan.FromSeconds(Math.Max(sl.Value, 0)); await s.ControlSession.TryChangePlaybackPositionAsync(pos.Ticks); } } catch { } finally { _drag = false; } }

    public void Dispose()
    {
        _disposed = true;
        _tick.Stop();
        _hoverPoll.Stop();
        _hideCts?.Cancel();
        StopLoop();
        StopBackgroundRotation();
        _eq.Dispose();
        HookMediaEvents(false);
    }
}
