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
/// Contenido musical del Island: seguimiento de sesiones multimedia, snapshot
/// de la sesión presentada, presentación (título, autor, carátula, capacidades
/// y seek) y controles.
///
/// <para>El Island NO depende del pipeline de media para existir: su estado
/// musical vive SOLO en el snapshot (<see cref="IslandMediaSnapshot"/>),
/// actualizado por los eventos de sesión. El reposo, la franja de hover, el
/// punto de estado y el motor de animación jamás consultan el gestor
/// multimedia; sin sesiones el Island solo tiene el temporizador como
/// contenido disponible (001 MOD RF-11, RF-13).</para>
/// </summary>
public partial class IslandWindow
{
    // ------------------------------------------------------------------
    // Desacoplamiento del control multimedia (change island-contenedor-refactor)
    // ------------------------------------------------------------------
    private sealed record IslandMediaSnapshot(
        string Id,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus Status);

    private IslandMediaSnapshot? _music;
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastStatus;

    private bool MusicAvailable() => _music != null;

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
                bool pauseCounts = SettingsManager.Current.IslandPauseCountsActive;
                bool keep = SettingsManager.Current.IslandVisibilityMode == 0
                    && pauseCounts && IsBoxShown;
                // Pausar desde compacto con pausa!=activa va a inactivo/nada aunque
                // el cursor siga encima (HidePerMode retorna por
                // IsMouseOverBoxOrStrip, y en Aviso temporal el aviso pediría plazo).
                // Vale en los DOS modos: la pausa configurada como inactiva no
                // sostiene una vista compacta (001 MOD RF-4, RF-7).
                bool forceHideFromCompact = !_expanded && !pauseCounts
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
        _contentMode = 0;
        ApplyContentVisibility();
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
        // OJO: _lastTrackKey NO se limpia aquí a propósito. Es la identidad de la
        // última canción PRESENTADA, no un dato de la vista: limpiarlo hacía que
        // la misma canción, al volver del reposo inactivo, se leyera como cambio
        // de pista y la carátula se volteara sola (001 RF-22: el volteo es solo
        // en cambio de canción).
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

    /// <summary>
    /// Actividad musical REAL (001 MOD RF-4, RF-11): cuenta la sesión del
    /// snapshot reproduciendo, cualquier sesión permitida reproduciendo ahora
    /// mismo —aunque el snapshot apunte a otra pausada o a ninguna— y la pausa
    /// solo si el ajuste «pausa cuenta como activo» lo dice.
    /// </summary>
    private bool IsMediaActiveForContract()
    {
        if (!SettingsManager.Current.IslandEnabled || !SettingsManager.Current.IslandMediaEnabled) return false;
        if (!MusicAvailable() && FirstAllowed() == null) return false;
        if (_music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return true;
        if (NewestPlaying() != null) return true;
        return SettingsManager.Current.IslandPauseCountsActive
            && _music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
    }

    /// <summary>
    /// Sesión que representa la actividad musical vigente (001 MOD RF-4, RF-11):
    /// la del snapshot si reproduce; si no, la que reproduce AHORA —adoptándola
    /// como snapshot— para que el compacto muestre lo que de verdad está activo
    /// y no una pausa obsoleta; si no hay nada reproduciendo, la del snapshot (o
    /// la primera permitida) sostiene su propia vista.
    /// </summary>
    private MediaSession? ActiveMediaSession()
    {
        var snap = Current();
        if (snap != null && SafeStatus(snap) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return snap;
        // Algo reproduce ahora mismo (aunque no sea la del snapshot): esa es la
        // actividad vigente y se adopta como vista musical (001 MOD RF-4, RF-11).
        var playing = NewestPlaying();
        if (playing != null)
        {
            if (snap == null || playing.Id != snap.Id) AdoptKnownSession();
            return playing;
        }
        // Nada reproduciendo: el snapshot (pausa que cuenta como activo) sostiene
        // su vista o, sin él, una sesión permitida conocida (RF-13).
        return snap ?? AdoptKnownSession();
    }

    /// <summary>
    /// Sesión permitida sin snapshot: se adopta como vista musical —la que
    /// reproduce ahora o, si ninguna, la primera permitida— dejando el snapshot
    /// coherente, para no abrir nunca una vista musical sin datos
    /// (001 MOD RF-11, RF-13).
    /// </summary>
    private MediaSession? AdoptKnownSession()
    {
        var session = NewestPlaying() ?? FirstAllowed();
        if (session == null) return null;
        NotePlay(session.Id);
        ApplyMediaSnapshot(new IslandMediaSnapshot(session.Id,
            SafeStatus(session) ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
        return session;
    }

    internal bool ShowMediaExpandedFromContract()
    {
        // La vista musical expandida muestra la sesión del snapshot (la selección
        // vigente del usuario, 001 MOD RF-5); sin snapshot se adopta una sesión
        // permitida conocida para que el clic abra sus controles en vez de una
        // vista musical vacía (001 MOD RF-11, RF-13).
        var session = Current();
        if (session == null && MediaContentAvailable()) session = AdoptKnownSession();
        if (session == null) return false;
        ExpandSession(session);
        return true;
    }

    internal bool ShowMediaCompactFromContract()
    {
        // La sesión que muestra el compacto es la de la actividad vigente
        // (adoptando la que reproduce si el snapshot no la tenía), nunca una
        // vista musical sin datos (001 MOD RF-4, RF-11, RF-13).
        var session = ActiveMediaSession();
        if (session == null) return false;
        ShowMusicCompact(session);
        return true;
    }

    private void ShowMusicCompact(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null, bool forceAlbumFlip = false)
    {
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        if (!MusicAvailable()) return; // sin snapshot musical no hay vista musical (RF-13)
        // Repliegue desde el expandido (o desde su fase 1): el compacto se alcanza
        // pasando por la pieza inactiva, y el aviso temporal vigente conserva su
        // plazo en lugar de reiniciarse (001 MOD RF-16).
        bool collapsing = _expanded || _p > 0.02 || _pendingCompactFeature != null;
        _hidingViaCompact = false;
        SelectFeature("media");
        SetInactiveRest(false);
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) { SnapHidden(); return; }
        RefreshUi(session, knownStatus, forceAlbumFlip);
        _expanded = false;
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled) SnapCompact();
        else if (!collapsing || MediaFeature is not { } media || !BeginCollapseThroughInactive(media))
        {
            _pT = 0;
            _qT = 1;
            IslandBox.Visibility = Visibility.Visible;
            UpdateMediaStatusDot();
            UpdateRotationPauseState();
            EnsureLoop();
        }
        ArmTemporaryHide(restart: !collapsing);
    }

    private void ExpandSession(MediaSession session)
    {
        if (HasExclusive()) return;
        if (!MusicAvailable()) return; // sin snapshot musical no hay vista musical (RF-13)
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        // Expandir no cancela el aviso: solo pospone su repliegue conservando el
        // plazo que le quedaba (001 RF-2).
        HoldTemporaryNotice();
        _hidingViaCompact = false;
        _currentId = session.Id;
        SelectFeature("media");
        // Entrar al contenido SIEMPRE cancela el reposo inactivo: sin esto la
        // vista expandida se pintaba con el contenido ya desvanecido (caja negra)
        // porque el progreso de la pieza seguía en 1 (001 MOD RF-16).
        SetInactiveRest(false);
        if (HasExclusive()) return;
        _contentMode = 0;
        ApplyContentVisibility();
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

    // --- presentación ---

    private void RefreshUi(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null, bool forceAlbumFlip = false)
    {
        // Alerta modal del timer: los eventos de música esperan a X o reinicio.
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        // Evento multimedia: el contenido más reciente manda (spec 001 RF-24).
        _contentMode = 0;
        ApplyContentVisibility();
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
        _lastTrackKey = trackKey;
        // El volteo es EXCLUSIVO del cambio de canción (001 RF-22) y de un cambio
        // de medio explícito (forceAlbumFlip). Antes también disparaba cuando la
        // carátula mostrada no coincidía con la entrante, y como el reposo
        // inactivo limpia la carátula del estado, la MISMA canción se volteaba
        // sola al volver a mostrarse. No se voltea por re-pintar, ni por llegar
        // tarde la miniatura (eso ya cambia la clave), ni en el primer pintado.
        if (trackChanged || forceAlbumFlip)
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

    /// <summary>
    /// Pinta la carátula (o su placeholder de nota musical) en compacto y
    /// expandido sin animación. Es el intercambio que ocurre en el punto ciego
    /// del volteo y también el camino de la primera portada o sin animaciones.
    /// </summary>
    private void ApplyAlbumArt(BitmapImage? art)
    {
        CompactArt.Source = art;
        ExpandedArt.Source = art;
        _displayedAlbumArt = art;
        _hasAlbumCover = art != null;
        CompactNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
        ExpandedNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
        UpdateAlbumArtOverlay();
    }

    private void SetAlbumArt(BitmapImage? art)
    {
        StopAlbumFlip();
        ApplyAlbumArt(art);
    }

    /// <summary>
    /// Volteo de carátula en cambio de canción o de medio (001 RF-22): la
    /// carátula saliente se estrecha hasta desaparecer, se intercambia en el
    /// punto ciego y la entrante se abre. Manda la ÚLTIMA portada pedida, así
    /// una ráfaga del reproductor (título primero, miniatura después) no
    /// reinicia el volteo a medias ni deja pasar una carátula obsoleta.
    /// </summary>
    private void StartAlbumFlip(BitmapImage? art)
    {
        _albumFlipArt = art;
        if (!AnimationsEnabled)
        {
            SetAlbumArt(art);
            return;
        }
        if (_albumFlipRunning) return; // el volteo en vuelo ya aplicará la última portada pedida

        _albumFlipRunning = true;
        int version = _albumFlipVersion;
        CompactArtFlipScale.ScaleX = ExpandedArtFlipScale.ScaleX = 1;
        CompactArtFlipScale.ScaleY = ExpandedArtFlipScale.ScaleY = 1;
        _hasAlbumCover = _displayedAlbumArt != null;
        UpdateAlbumArtOverlay();

        // Cada fase dura una fracción de la duración global de animaciones: el
        // volteo se siente de la familia del resto del Island sin volverse lento.
        double halfMs = Math.Clamp(MainWindow.getDuration() * 0.35, 90, 200);
        var outgoing = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(halfMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        outgoing.Completed += (_, _) =>
        {
            if (version != _albumFlipVersion) return;
            ApplyAlbumArt(_albumFlipArt);

            var incoming = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(halfMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            incoming.Completed += (_, _) =>
            {
                if (version != _albumFlipVersion) return;
                _albumFlipRunning = false;
                CompactArtFlipScale.ScaleX = ExpandedArtFlipScale.ScaleX = 1;
                CompactArtFlipScale.ScaleY = ExpandedArtFlipScale.ScaleY = 1;
                UpdateAlbumArtOverlay();
                // Llegó otra canción mientras girábamos: otro volteo, ya limpio.
                if (!ReferenceEquals(_albumFlipArt, _displayedAlbumArt)) StartAlbumFlip(_albumFlipArt);
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

    // --- seek ---

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

    // --- controles ---

    private MediaSession? AnySession() => !MusicContentShown() || _music == null ? null : Current() ?? NewestPlaying() ?? FirstAllowed();

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

    private void Album_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // el clic del álbum SOLO cambia de medio (001 MOD RF-5)
        var all = _main.mediaManager.CurrentMediaSessions.Values.Where(s => _main.IsSessionAllowed(s)).ToList();
        if (!MusicAvailable() || all.Count <= 1) return;
        int i = all.FindIndex(s => s.Id == _currentId);
        var next = all[(i + 1) % all.Count];
        _currentId = next.Id;
        if (_expanded) RefreshUi(next, null, true);
        else ShowMusicCompact(next, null, true);
    }

    private void Seekbar_Down(object sender, MouseButtonEventArgs e) { _drag = true; if (sender is Slider sl) { var p = e.GetPosition(sl); double ratio = sl.ActualWidth > 0 ? Math.Clamp(p.X / sl.ActualWidth, 0, 1) : 0; sl.Value = ratio * sl.Maximum; } }
    private async void Seekbar_Up(object sender, MouseButtonEventArgs e) { try { if (Current() is { } s && sender is Slider sl) { var pos = TimeSpan.FromSeconds(Math.Max(sl.Value, 0)); await s.ControlSession.TryChangePlaybackPositionAsync(pos.Ticks); } } catch { } finally { _drag = false; } }
}
