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
    // ¿El último cambio de media trajo metadata nueva (título/portada)? Se
    // consume en la reconciliación para decidir el activador de cambio de pista
    // sin inspeccionar eventos ya coalescidos (001 MOD RF-1).
    private bool _mediaMetadataChanged;
    // Identidad de la pista presentada (título + autor). Un cambio aquí ES un
    // cambio de canción, con independencia del estado de reproducción que
    // reporte el reproductor: algunos no emiten metadata y solo anuncian la pista
    // nueva por el estado, y otros transicionan por None/Opened y un cambio que
    // llegaba en ese estado no mostraba nada.
    private string _lastSongTitle = "";
    private string _lastSongArtist = "";
    // ¿El último evento de media trajo una canción DISTINTA (nombre o autor)?
    private bool _pendingTrackChange;
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
        // El buzón reconcilia el estado final una sola vez (001 MOD RF-1).
        PostActivity(IslandActivityReason.Settings);
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

    private string? DisplayedMediaId => _mediaPinnedSessionId ?? _currentId ?? _music?.Id;

    private bool IsDisplayedSession(MediaSession session) =>
        string.Equals(DisplayedMediaId, session.Id, StringComparison.Ordinal);

    private void PinMediaSession(MediaSession session)
    {
        _mediaPinnedSessionId = session.Id;
        _currentId = session.Id;
    }

    private MediaSession? Current()
    {
        // La sesión de control debe ser exactamente la que está dibujada. El
        // pin cubre el caso en que Windows mueve el foco a otra app al pausar,
        // reproducir o saltar una pista.
        var id = DisplayedMediaId;
        if (id == null) return null;
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
            if (s.Id == id && _main.IsSessionAllowed(s)) return s;
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
            _currentId = null;
            // Sin sesión: cero datos musicales residuales, la vista la decide
            // el contenedor (timer disponible u oculto), nunca música vacía.
            if (existed) ClearMusicResidue();
            return;
        }
        _currentId = s.Id;
        PaintGlyph();
    }

    // El gestor multimedia arranca antes que el Island, así que la primera
    // reproducción puede no emitir un evento que el Island alcance a ver.
    private void SyncExistingMediaState()
    {
        if (!MediaContentAvailable() || Suppressed()) return;
        var before = _music?.Id;
        SyncMediaSnapshotFromSessions();
        // Adoptar una reproducción en curso sin evento: solo entonces se presenta
        // por la ruta normal de la actividad (001 MOD RF-4/RF-28).
        if (_music != null && _music.Id != before
            && _music.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            bool mode0 = SettingsManager.Current.IslandVisibilityMode == 0;
            if (mode0 || SettingsManager.Current.IslandShowOnPlayPause)
                PresentMediaSnapshot(_music.Status);
        }
    }

    /// <summary>
    /// Concilia el snapshot musical con las sesiones actuales SIN presentar: la
    /// sesión presentada que siga viva conserva su identidad (aunque esté
    /// pausada); si desapareció se libera, y sin snapshot se adopta la que esté
    /// reproduciendo AHORA (evento de arranque perdido). Es la base de la
    /// reconciliación, así que no toca la vista (001 MOD RF-1/RF-11/RF-13).
    /// </summary>
    private void SyncMediaSnapshotFromSessions()
    {
        if (_music != null)
        {
            // La sesión presentada sigue viva y permitida: conservarla tal cual
            // (el punto de estado ya refleja play/pausa). NADA de revocarla por
            // no estar reproduciendo ahora mismo.
            var held = Current();
            if (held != null)
            {
                var heldStatus = SafeStatus(held);
                if (heldStatus != null && heldStatus != _music.Status)
                    ApplyMediaSnapshot(new IslandMediaSnapshot(held.Id, heldStatus.Value));
                // Si la sesión presentada no reproduce pero otra sí, la actividad
                // vigente es la que reproduce: adoptarla (salvo selección fijada).
                // Así un medio NUEVO que empieza a sonar sin evento acaba
                // mostrándose y el Island no se queda con el anterior
                // (001 MOD RF-4/RF-11/RF-28).
                if (_mediaPinnedSessionId == null
                    && (heldStatus ?? _music.Status) != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && NewestPlaying() is { } newer)
                {
                    NotePlay(newer.Id);
                    ApplyMediaSnapshot(new IslandMediaSnapshot(newer.Id,
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
                }
                return;
            }
            // Ya no existe (o no está permitida): liberar y limpiar residuos.
            OnMusicUnavailable();
        }
        // Sin snapshot: adoptar algo que se esté reproduciendo AHORA (evento de
        // arranque perdido); una sesión pausada NO se adopta sola para no
        // robarle la vista al temporizador ni sorprender al usuario.
        var playing = NewestPlaying();
        if (playing != null)
        {
            NotePlay(playing.Id);
            ApplyMediaSnapshot(new IslandMediaSnapshot(playing.Id,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
        }
    }

    /// <summary>
    /// Registra la identidad de la canción (título + autor) y marca un cambio de
    /// pista cuando difiere de la anterior. Es el detector que pide el
    /// comportamiento de «Aviso temporal»: el cambio de canción se reconoce por
    /// su nombre o su autor, no por el estado de reproducción. Un evento sin
    /// título ni autor no marca nada (un reproductor que publique vacío no debe
    /// contar como cambio).
    /// </summary>
    private void NoteTrackIdentity(string? title, string? artist)
    {
        string t = (title ?? "").Trim();
        string a = (artist ?? "").Trim();
        // Sin título no hay canción identificable: los reproductores que lo vacían
        // un instante entre pistas no deben marcar un falso cambio. El autor
        // conocido se conserva si el evento llega sin él.
        if (t.Length == 0)
        {
            if (a.Length > 0) _lastSongArtist = a;
            return;
        }
        // El título manda (nombre distinto = canción distinta); el autor cuenta
        // solo cuando ambos lados lo aportan, para no confundir a un reproductor
        // que rellena el autor con retraso con un cambio de canción.
        bool changed = _lastSongTitle.Length > 0
            && (!string.Equals(_lastSongTitle, t, StringComparison.Ordinal)
                || (_lastSongArtist.Length > 0 && a.Length > 0
                    && !string.Equals(_lastSongArtist, a, StringComparison.Ordinal)));
        _lastSongTitle = t;
        if (a.Length > 0) _lastSongArtist = a;
        if (changed) _pendingTrackChange = true;
    }

    /// <summary>
    /// Lee la metadata actual de la sesión para registrar su identidad. Cubre a
    /// los reproductores que no emiten el evento de metadata: anuncian la pista
    /// nueva solo con el estado de reproducción.
    /// </summary>
    private void TryNoteTrackIdentity(MediaSession session)
    {
        try
        {
            var props = session.ControlSession.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
            if (props != null) NoteTrackIdentity(props.Title, props.Artist);
        }
        catch { }
    }

    /// <summary>
    /// Presentación musical reconciliada (001 MOD RF-1): se llama UNA vez por
    /// ráfaga de eventos de media, con el snapshot ya conciliado. Decide la vista
    /// con las mismas reglas que los eventos individuales, pero sin repetir el
    /// trabajo por evento ni perder el estado final.
    ///
    /// <para>El cambio de CANCIÓN (título o autor distintos) es un evento con
    /// entidad propia: en «Aviso temporal» muestra el aviso aunque el reproductor
    /// no reporte reproducción o el activador de play/pausa esté apagado. Antes,
    /// un cambio de pista cuyo estado no fuera Playing/Paused (algunos
    /// reproductores pasan por None/Opened al saltar de canción) caía al final y
    /// no enseñaba nada.</para>
    /// </summary>
    /// <param name="recoveryOnly">
    /// La pasada viene de la red de recuperación (sin evento de media): solo
    /// reacciona si apareció una sesión reproduciendo que no se estaba mostrando,
    /// y NUNCA re-despliega lo que el usuario ya ocultó. Sin esto, el aviso
    /// temporal reaparecía cada 5 s reiniciando su plazo (001 MOD RF-2/RF-28).
    /// </param>
    private void ReconcileMediaState(bool recoveryOnly = false)
    {
        bool metadata = _mediaMetadataChanged;
        bool trackChanged = _pendingTrackChange;
        _mediaMetadataChanged = false;
        _pendingTrackChange = false;
        if (_disposed) return;
        // La alerta del temporizador es exclusiva: los eventos de música esperan.
        if (_timer.State == IslandTimerState.Alerting) return;
        // La supresión la resuelve ReconcileCore (ya se llamó con el motivo Context).
        if (Suppressed() && !HasExclusive()) return;
        if (!MediaContentAvailable())
        {
            // Contenido musical deshabilitado: el snapshot se vacía y la vista
            // queda para el temporizador (o nada).
            if (_music != null) OnMusicUnavailable();
            return;
        }
        var beforeId = _music?.Id;
        SyncMediaSnapshotFromSessions();
        bool sessionChanged = _music != null
            && !string.Equals(_music.Id, beforeId, StringComparison.Ordinal);

        var snap = _music;
        if (snap == null)
        {
            // Sin actividad musical no hay vista musical que presentar. Solo se
            // toca la vista si era la música la que estaba delante (una caja
            // musical vacía no vale, RF-13); el resto de funcionalidades mandan
            // sobre su propia vista y su aviso.
            if (MediaOwnsView()) DropMediaView();
            return;
        }

        // Pasada de recuperación sin cambio de sesión: no hay nada nuevo que
        // mostrar. Re-desplegar aquí reiniciaría el plazo del aviso cada 5 s
        // (001 MOD RF-2/RF-28); solo se refresca lo que ya está a la vista.
        if (recoveryOnly && !sessionChanged)
        {
            RefreshVisibleMediaIfShown(metadata);
            return;
        }

        var status = snap.Status;
        bool mode0 = SettingsManager.Current.IslandVisibilityMode == 0;
        bool pauseCounts = SettingsManager.Current.IslandPauseCountsActive;
        bool knownStatus = status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;

        // Cambio de canción (nombre o autor): el aviso temporal se muestra por sí
        // mismo, sin depender del estado que reporte el reproductor (algunos no
        // emiten metadata y solo anuncian la pista por el estado).
        if (trackChanged && !mode0 && SettingsManager.Current.IslandShowOnTrackChange)
        {
            PresentMediaSnapshot(knownStatus ? status : null, trackChanged: true);
            return;
        }

        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            if (!mode0 && !SettingsManager.Current.IslandShowOnPlayPause)
            {
                // Activador apagado: no se interrumpe la vista vigente, pero si la
                // vista musical ya estaba delante se re-pinta su metadata nueva.
                RefreshVisibleMediaIfShown(metadata);
                return;
            }
            PresentMediaSnapshot(status);
            return;
        }

        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
        {
            // Pausa desde el expandido (botón o visualizador): se queda expandido
            // mostrando el estado de pausa con sus controles.
            if (_expanded)
            {
                if (Current() is { } expandedSession) RefreshUi(expandedSession, status);
                return;
            }
            bool show = SettingsManager.Current.IslandShowOnPause || (pauseCounts && mode0)
                || (trackChanged && SettingsManager.Current.IslandShowOnTrackChange);
            if (show) { PresentMediaSnapshot(status); return; }
            // Pausa que no cuenta como activa: no sostiene una vista compacta, en
            // los DOS modos (001 MOD RF-4/RF-7).
            if (MediaOwnsView())
            {
                bool forceHideFromCompact = !pauseCounts && !SettingsManager.Current.IslandShowOnPause;
                if (forceHideFromCompact) DropMediaView();
                else HidePerMode();
            }
            return;
        }

        // Detenida o desconocida sin cambio de pista: no hay actividad musical
        // nueva que sostenga la vista (en «Visible mientras activo» el estado real
        // manda y la siguiente reproducción la presenta; en «Aviso temporal» el
        // cambio de canción ya se atendió arriba para cualquier estado). Solo se
        // repliega la propia vista musical; una vista de otra funcionalidad
        // (temporizador, cajón, estante, calendario) no se toca.
        if (MediaOwnsView() && !_expanded) DropMediaView();
    }

    /// <summary>
    /// ¿La vista vigente es la música? Vale con su vista rica delante y también cuando
    /// la música vive dentro de una PANTALLA que la contiene (change island-pantallas):
    /// en los dos casos un evento de música la afecta a ella y no a otra vista.
    /// </summary>
    private bool MediaOwnsView() =>
        _contentMode == IslandContentMode.Media || ScreenOwnsMediaView();

    /// <summary>
    /// La música dejó de sostener la vista: se repliega a lo que corresponda. Con una
    /// PANTALLA delante, la vista la sostiene la pantalla entera, así que se queda si
    /// alguna otra de sus funcionalidades sigue sosteniéndola (el temporizador contando,
    /// un aviso vivo) y, si no, se repliega igual que su vista rica.
    /// </summary>
    private void DropMediaView()
    {
        if (_contentMode == IslandContentMode.Screen)
        {
            if (ScreenSustainsView())
            {
                if (_expanded) { RefreshScreenMembers(); return; }
                if (ShowCurrentScreenCompact()) return;
            }
            if (!_expanded) ShowInactiveOrHidden();
            return;
        }
        if (TimerKeepsAlive()) ShowTimerCompact();
        else if (!_expanded) ShowInactiveOrHidden();
    }

    /// <summary>
    /// Con la vista musical ya delante, un cambio de metadata re-pinta su
    /// título/portada sin re-desplegar nada ni tocar el plazo del aviso
    /// (001 MOD RF-1/RF-2). Con la música dentro de una pantalla, repinta su ficha y
    /// su panel sin tocar la composición.
    /// </summary>
    private void RefreshVisibleMediaIfShown(bool metadataChanged)
    {
        if (!metadataChanged || !IsBoxShown) return;
        if (!MediaOwnsView()) return;
        if (Current() is { } shown) RefreshUi(shown);
    }

    /// <summary>
    /// Presenta la vista musical vigente (compacto o expandido) de la sesión
    /// actual, adoptándola si el snapshot no la tenía (001 MOD RF-4/RF-11/RF-13).
    /// Con <paramref name="status"/> en null se deja que la tarjeta lea el estado
    /// real de la sesión: es el caso del cambio de canción que llega con un estado
    /// intermedio que no debe pintarse.
    /// </summary>
    private void PresentMediaSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status, bool trackChanged = false)
    {
        var session = Current() ?? ActiveMediaSession();
        if (session == null) return;
        _currentId = session.Id;
        if (_expanded)
        {
            RefreshUi(session, status);
            return;
        }
        // El aviso de Bluetooth está delante con su plazo vivo: la media no se lo
        // lleva por delante con un evento ordinario (play/pausa o metadata), porque
        // ese aviso es una notificación más reciente y corta. Un cambio de canción
        // sí es un evento nuevo y toma la vista (change island-bluetooth-conectado
        // RF-1).
        if (!trackChanged && IsBoxShown && _contentMode == IslandContentMode.Bluetooth
            && _noticeUntil > DateTime.UtcNow)
            return;
        // Ya a la vista con su aviso vigente: un evento repetido del reproductor no
        // debe reiniciar el plazo ni hacer REAPARECER el aviso cada pocos segundos
        // (001 MOD RF-2). Un cambio de canción sí estrena aviso. Con la música dentro de
        // una pantalla vale lo mismo: su vista ya está delante.
        if (IsBoxShown && MediaOwnsView()
            && _noticeUntil > DateTime.UtcNow && !trackChanged)
        {
            RefreshUi(session, status);
            return;
        }
        ShowMusicCompact(session, status);
    }

    /// <summary>
    /// Cambio de estado de reproducción (001 MOD RF-1): solo actualiza la
    /// identidad ligera (fijación explícita y último-play) y publica el motivo de
    /// actividad. La presentación la resuelve UNA reconciliación con el último
    /// estado, así una ráfaga no repinta por evento ni pierde el estado final.
    /// </summary>
    private void OnPlayState(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo? info)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (!_main.IsSessionAllowed(session)) return;
            var status = info?.PlaybackStatus ?? session.ControlSession?.GetPlaybackInfo()?.PlaybackStatus;
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                // Una reproducción nueva sí suelta la sesión fijada, pero el foco
                // que Windows mueve al pausar, reanudar o saltar no debe romper
                // una selección explícita.
                if (_mediaPinnedSessionId != null && _mediaPinnedSessionId != session.Id)
                    _mediaPinnedSessionId = null;
                NotePlay(session.Id);
                // Una reproducción nueva PASA a ser la sesión mostrada: sin adoptar
                // el snapshot, el Island seguiría enseñando el medio anterior
                // (001 MOD RF-4/RF-11).
                ApplyMediaSnapshot(new IslandMediaSnapshot(session.Id,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
                // Algunos reproductores no emiten cambio de metadata y solo
                // anuncian la pista nueva por el estado: comparar aquí la
                // identidad cubre ese caso (001 MOD RF-1).
                TryNoteTrackIdentity(session);
            }
            PostActivity(IslandActivityReason.Media);
        }));
    }

    /// <summary>
    /// Cambio de metadata (título, artista, portada): registra la identidad de la
    /// pista —de la que sale el detector de cambio de canción— y publica
    /// actividad; el snapshot y la presentación los resuelve la reconciliación con
    /// la metadata MÁS RECIENTE (001 MOD RF-1). Así una ráfaga del reproductor
    /// (título primero, miniatura después) pinta una vez y con el estado final.
    /// </summary>
    private void OnMediaProp(MediaSession session, GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (!_main.IsSessionAllowed(session)) return;
            // Eventos de sesiones ajenas no pueden secuestrar una selección fijada.
            var sessionStatus = SafeStatus(session);
            bool playingNow = sessionStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            // Una reproducción nueva suelta la selección fijada; el simple cambio de
            // metadata de una sesión ajena no puede secuestrarla.
            if (_mediaPinnedSessionId != null && !IsDisplayedSession(session))
            {
                if (!playingNow) return;
                _mediaPinnedSessionId = null;
            }
            NoteTrackIdentity(props?.Title, props?.Artist);
            _mediaMetadataChanged = true;
            // La sesión que anuncia la pista es la actividad vigente: adoptarla como
            // snapshot para que el aviso muestre el medio NUEVO y no el anterior,
            // aunque este reproductor no emita el estado de reproducción
            // (001 MOD RF-4/RF-11/RF-13). Con OTRA sesión reproduciendo manda esa.
            if (playingNow || (_music?.Status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                && NewestPlaying() == null))
            {
                NotePlay(session.Id);
                ApplyMediaSnapshot(new IslandMediaSnapshot(session.Id,
                    sessionStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
            }
            PostActivity(IslandActivityReason.Media);
        }));
    }

    /// <summary>
    /// Cierre de sesión: suelta la identidad ligera y publica actividad para que
    /// la reconciliación resuelva la vista siguiente (otra reproducción, el
    /// temporizador o el reposo) una sola vez (001 MOD RF-1/RF-24).
    /// </summary>
    private void OnClosed(MediaSession session)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            _lastPlay.Remove(session.Id);
            if (IsDisplayedSession(session) && _mediaPinnedSessionId == session.Id)
                _mediaPinnedSessionId = null;
            PostActivity(IslandActivityReason.Media);
        }));
    }

    // La música desapareció (cierre de la última sesión o candidata inválida):
    // sin compacto musical vacío ni datos muertos. El temporizador disponible
    // puede recuperar la vista inmediatamente (001 MOD RF-24, 002 MOD RF-14).
    private void OnMusicUnavailable()
    {
        _mediaPinnedSessionId = null;
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
        _contentMode = IslandContentMode.Media;
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
        if (_mediaPinnedSessionId != null)
        {
            if (snap != null) return snap;
            // La sesión fijada desapareció: solo entonces se permite recuperar
            // la sesión que esté reproduciendo más recientemente.
            _mediaPinnedSessionId = null;
        }
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
        ShowCompactView(IslandContentMode.Media, MediaFeature,
            () => RefreshUi(session, knownStatus, forceAlbumFlip));
    }

    private void ExpandSession(MediaSession session)
    {
        if (HasExclusive()) return;
        if (!MusicAvailable()) return; // sin snapshot musical no hay vista musical (RF-13)
        _currentId = session.Id;
        // guard: la exclusiva se re-comprueba tras cancelar el reposo inactivo —
        // una alerta de temporizador que llegara en ese mismo turno manda (002 RF-2).
        ShowExpandedView(IslandContentMode.Media, MediaFeature, () => RefreshUi(session),
            guard: () => !HasExclusive());
    }

    // --- presentación ---

    private void RefreshUi(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null, bool forceAlbumFlip = false)
    {
        // Alerta modal del timer: los eventos de música esperan a X o reinicio.
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        // Con una PANTALLA combinada delante que contiene la música, la composición la
        // manda ella: la música solo repinta su contenido (fichas y paneles de cada
        // columna). Si no, el evento multimedia adopta su vista de siempre: el contenido
        // más reciente manda (spec 001 RF-24).
        if (!ScreenOwnsMediaView())
        {
            _contentMode = IslandContentMode.Media;
            ApplyContentVisibility();
        }
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

    /// <summary>
    /// Resumen de una línea de la música para las pantallas combinadas
    /// (change island-pantallas RF-3): el título que está sonando.
    /// </summary>
    internal IslandFeatureSummary MediaSummary() => new(
        Wpf.Ui.Controls.SymbolRegular.MusicNote2Play20,
        string.IsNullOrWhiteSpace(CompactTitle.Text) ? "Sin reproducción" : CompactTitle.Text);

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

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (Current() is not { } s) return;
        PinMediaSession(s);
        await s.ControlSession.TrySkipPreviousAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (Current() is not { } s) return;
        PinMediaSession(s);
        await s.ControlSession.TryTogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (Current() is not { } s) return;
        PinMediaSession(s);
        await s.ControlSession.TrySkipNextAsync();
    }

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
        int i = all.FindIndex(s => s.Id == (Current()?.Id ?? DisplayedMediaId));
        var next = all[(i + 1) % all.Count];
        PinMediaSession(next);
        var nextStatus = SafeStatus(next) ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        ApplyMediaSnapshot(new IslandMediaSnapshot(next.Id, nextStatus));
        if (_expanded) RefreshUi(next, nextStatus, true);
        else ShowMusicCompact(next, nextStatus, true);
    }

    private void Seekbar_Down(object sender, MouseButtonEventArgs e) { _drag = true; if (sender is Slider sl) { var p = e.GetPosition(sl); double ratio = sl.ActualWidth > 0 ? Math.Clamp(p.X / sl.ActualWidth, 0, 1) : 0; sl.Value = ratio * sl.Maximum; } }
    private async void Seekbar_Up(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (Current() is not { } s || sender is not Slider sl) return;
            PinMediaSession(s);
            var pos = TimeSpan.FromSeconds(Math.Max(sl.Value, 0));
            await s.ControlSession.TryChangePlaybackPositionAsync(pos.Ticks);
        }
        catch { }
        finally { _drag = false; }
    }
}
