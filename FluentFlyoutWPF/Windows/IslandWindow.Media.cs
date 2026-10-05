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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using static WindowsMediaController.MediaManager;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Island music content: media session tracking, snapshot of the presented
/// session, presentation (title, artist, artwork, capabilities
/// y seek) y controles.
///
/// <para>The Island does NOT depend on the media pipeline to exist: its music
/// state lives ONLY in the snapshot (<see cref="IslandMediaSnapshot"/>), updated
/// by session events. Rest, the hover strip, the status dot and the animation
/// engine never query the media manager; without sessions the Island only has
/// the timer as available content (001 MOD RF-11, RF-13).</para>
/// </summary>
public partial class IslandWindow
{
    // ------------------------------------------------------------------
    // Media control decoupling (change island-contenedor-refactor)
    // ------------------------------------------------------------------
    private sealed record IslandMediaSnapshot(
        string Id,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus Status);

    private IslandMediaSnapshot? _music;
    // Did the last media change bring new metadata (title/artwork)? It is consumed
    // during reconciliation to decide the track-change trigger without inspecting
    // events that are already coalesced (001 MOD RF-1).
    private bool _mediaMetadataChanged;
    // Identity of the presented track (title + artist): a change here IS a
    // song change, regardless of the playback state the player reports - some emit no
    // metadata and only announce the new track through the state, and others pass
    // through None/Opened, where a change arriving in that state showed nothing. The
    // detector rules live in MediaTrackIdentity (pure and tested).
    private readonly MediaTrackIdentity _trackIdentity = new();
    // Did the last media event bring a DIFFERENT song (name or artist)?
    private bool _pendingTrackChange;
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastStatus;

    // --- memos de lectura de SMTC ---
    // Everything the Island asks the media system is a cross-process call: each
    // session's playback state, its properties (title/artist/artwork) and its
    // capabilities. Every repaint used to ask for them again - with a BLOCKING call
    // on the UI thread - and a single reconciliation asked several times (current
    // active item, equalizer, equalizer button, capabilities). Now they are read once
    // and reused until something could have changed them:
    // - the state lives as long as the PASS (one reconciliation or one event);
    // - the properties come from the metadata event itself, which already carries them.
    // Neither of the two extends a datum's life past the next event, so playback that
    // emits no event is still detected exactly as before.
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSessionPlaybackStatus?> _statusMemo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IslandMediaProps> _propsMemo = new(StringComparer.Ordinal);
    private MediaArtworkPublication? _artworkPublication;
    private MediaArtworkSnapshot? _lastPresentedTrack;

    /// <summary>
    /// A session's metadata with its decoded artwork.
    /// </summary>
    private sealed record IslandMediaProps(string Title, string Artist, BitmapImage? Artwork);

    private bool MusicAvailable() => _music != null;

    /// <summary>
    /// Invalidates the read memos: called at the start of a reconciliation pass and on
    /// receiving a system event, which is when the state may have changed.
    /// </summary>
    private void InvalidateMediaReads(bool props = false)
    {
        _statusMemo.Clear();
        if (props) _propsMemo.Clear();
    }

    /// <summary>
    /// Hooks/unhooks the Island media control events.
    /// The Island ALWAYS follows sessions (they are its own content); the
    /// parameter exists to unhook cleanly on close.
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
    /// Re-hooks session tracking depending on whether it can be used: with no music
    /// content available (Island off or "Media control" off) nothing from the system
    /// is listened to - no events, no memos, no artwork. For someone who only wants
    /// temporary notices (Bluetooth, dictation) the media manager stops waking the
    /// Island altogether.
    /// </summary>
    private void SyncMediaHooks() => HookMediaEvents(MediaContentAvailable());

    /// <summary>
    /// Change of the Island "Media control" setting: with content off the snapshot is
    /// released (no empty music view) and the view goes back to the timer or hides;
    /// with it on, it reconciles.
    /// </summary>
    public void RefreshMediaContent() => Dispatcher.Invoke(() =>
    {
        SyncMediaHooks();
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
        // The mailbox reconciles the final state exactly once (001 MOD RF-1).
        PostActivity(IslandActivityReason.Settings);
    });

    /// <summary>
    /// The Island's music content can be disabled independently (just like the timer
    /// with IslandTimerEnabled).
    /// </summary>
    private bool MediaContentAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandMediaEnabled;

    private MediaSession? NewestPlaying()
    {
        MediaSession? best = null;
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
        {
            if (!_main.IsWidgetSessionAllowed(s)) continue;
            if (SafeStatus(s) != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
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
        // The control session must be exactly the one on screen. The pin covers the
        // case where Windows moves focus to another app on pause, play or track skip.
        var id = DisplayedMediaId;
        if (id == null) return null;
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
            if (s.Id == id && _main.IsWidgetSessionAllowed(s)) return s;
        return null;
    }

    private MediaSession? FirstAllowed()
    {
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
            if (_main.IsWidgetSessionAllowed(s)) return s;
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
            // Without a session: zero leftover music data, the view is decided by the
            // container (timer available or hidden), never empty music.
            if (existed) ClearMusicResidue();
            return;
        }
        _currentId = s.Id;
        PaintGlyph();
    }

    // The media manager starts before the Island, so the first playback may not emit
    // an event the Island gets to see.
    private void SyncExistingMediaState()
    {
        if (!MediaContentAvailable() || Suppressed()) return;
        var before = _music?.Id;
        SyncMediaSnapshotFromSessions();
        // Adopting playback in progress with no event: only then is it presented through
        // the normal activity route (001 MOD RF-4/RF-28).
        if (_music != null && _music.Id != before
            && _music.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            bool mode0 = SettingsManager.Current.IslandVisibilityMode == 0;
            if (mode0 || SettingsManager.Current.IslandShowOnPlayPause)
                PresentMediaSnapshot(_music.Status);
        }
    }

    /// <summary>
    /// Reconciles the music snapshot with the current sessions WITHOUT presenting: a
    /// presented session that is still alive keeps its identity (even if paused); if
    /// it is gone the snapshot is released, and with no snapshot the one playing NOW
    /// is adopted (a lost startup event). It is the base of reconciliation, so it
    /// does not touch the view (001 MOD RF-1/RF-11/RF-13).
    /// </summary>
    private void SyncMediaSnapshotFromSessions()
    {
        if (_music != null)
        {
            // The presented session is still alive and allowed: keep it as is
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
            // It no longer exists (or is not allowed): release it and clear the residue.
            OnMusicUnavailable();
        }
        // Without a snapshot: adopt whatever is playing NOW (a lost startup event). With
        // "pause counts as active" a PAUSED session is adopted too: it is current
        // activity and its compact view needs data to draw (title, artwork). Without
        // that setting a pause is NOT adopted on its own: it does not steal the view
        // from the timer nor surprise the user (001 MOD RF-6/RF-7).
        var adopt = NewestPlaying();
        if (adopt == null && SettingsManager.Current.IslandPauseCountsActive) adopt = PausedAllowed();
        if (adopt != null)
        {
            NotePlay(adopt.Id);
            ApplyMediaSnapshot(new IslandMediaSnapshot(adopt.Id, SafeStatus(adopt)
                ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
        }
    }

    /// <summary>
    /// Records the song identity (title + artist) and flags a track change when it
    /// differs from the previous one. It is the detector behind the "Temporary notice"
    /// behavior: a song change is recognized by its name or its artist, not by the
    /// playback state. An event with no title and no artist flags nothing (a player
    /// contar como cambio).
    /// </summary>
    private void NoteTrackIdentity(string? title, string? artist)
    {
        if (_trackIdentity.Observe(title, artist)) _pendingTrackChange = true;
    }

    /// <summary>
    /// Reads the session's current metadata to record its identity. It covers the
    /// players that do not emit the metadata event: they announce the new track only
    /// through the playback state.
    /// </summary>
    private void TryNoteTrackIdentity(MediaSession session)
    {
        if (MediaPropsOf(session) is { } props) NoteTrackIdentity(props.Title, props.Artist);
    }

    /// <summary>
    /// The session's properties, from the memo if they are already known. A memo miss
    /// does read from the system (once) and remembers it: the asker is a repaint or a
    /// song-change detector, and it cannot wait for an event that may never come.
    /// </summary>
    private IslandMediaProps? MediaPropsOf(MediaSession session)
    {
        if (_propsMemo.TryGetValue(session.Id, out var known)) return known;
        var fetched = FetchMediaProps(session);
        if (fetched != null) _propsMemo[session.Id] = fetched;
        return fetched;
    }

    /// <summary>System read of a session's properties (the only blocking route).</summary>
    private static IslandMediaProps? FetchMediaProps(MediaSession session)
    {
        try
        {
            var props = session.ControlSession.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
            return props == null ? null : FromProperties(props);
        }
        catch { return null; }
    }

    /// <summary>
    /// Converts the properties the system delivers (or the event, which carries the
    /// same ones) into the value the view consumes, resolving the artwork only once.
    /// </summary>
    private static IslandMediaProps FromProperties(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        => new(props.Title ?? "", props.Artist ?? "", BitmapHelper.GetThumbnail(props.Thumbnail));

    /// <summary>
    /// Reconciled music presentation (001 MOD RF-1): called ONCE per burst of media
    /// events, with the snapshot already reconciled. It decides the view by the same
    /// rules as the individual events, but without repeating the work per event or
    /// losing the final state.
    ///
    /// <para>A SONG change (different title or artist) is an event in its own right: in
    /// "Temporary notice" it shows the notice even if the player reports no playback
    /// or the play/pause trigger is off. Before, a track change whose state was not
    /// Playing/Paused (some players pass through None/Opened when skipping) fell
    /// through to the end and showed nothing.</para>
    /// </summary>
    /// <param name="recoveryOnly">
    /// The pass comes from the recovery net (no media event): it only reacts if a
    /// playing session appeared that was not being shown, and it NEVER re-expands
    /// what the user already hid. Without this the temporary notice came back every
    /// 5 s, restarting its deadline (001 MOD RF-2/RF-28).
    /// </param>
    private void ReconcileMediaState(bool recoveryOnly = false)
    {
        bool metadata = _mediaMetadataChanged;
        bool trackChanged = _pendingTrackChange;
        _mediaMetadataChanged = false;
        _pendingTrackChange = false;
        if (_disposed) return;
        // The timer alert is exclusive: music events wait.
        if (_timer.State == IslandTimerState.Alerting) return;
        // Suppression is resolved by ReconcileCore (it was already called with the Context reason).
        if (Suppressed() && !HasExclusive()) return;
        if (!MediaContentAvailable())
        {
            // Music content disabled: the snapshot is emptied and the view is left to the
            // timer (or to nothing).
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
            // Without music activity there is no music view to present. The view is only
            // touched if music was what was in front (an empty music box does not
            // count, RF-13); every other feature takes precedence
            // sobre su propia vista y su aviso.
            if (MediaOwnsView()) DropMediaView();
            return;
        }

        // Recovery pass with no session change: there is nothing new to show.
        // Re-expanding here would restart the notice deadline every 5 s
        // (001 MOD RF-2/RF-28); only what is already on screen is refreshed.
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

        // Song change (name or artist): the temporary notice shows on its own,
        // regardless of the state the player reports (some emit no metadata and
        // only announce the track through the state).
        if (trackChanged && !mode0 && SettingsManager.Current.IslandShowOnTrackChange)
        {
            PresentMediaSnapshot(knownStatus ? status : null, trackChanged: true);
            return;
        }

        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
        {
            if (!mode0 && !SettingsManager.Current.IslandShowOnPlayPause)
            {
                // Trigger off: the current view is not interrupted, but if the
                // music view was already in front its new metadata is repainted.
                RefreshVisibleMediaIfShown(metadata);
                return;
            }
            PresentMediaSnapshot(status);
            return;
        }

        if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
        {
            // Pause from the expanded view (button or visualizer): it stays expanded,
            // showing the paused state with its controls.
            if (_expanded)
            {
                if (Current() is { } expandedSession) RefreshUi(expandedSession, status);
                return;
            }
            bool show = SettingsManager.Current.IslandShowOnPause || (pauseCounts && mode0)
                || (trackChanged && SettingsManager.Current.IslandShowOnTrackChange);
            // With "pause counts as active" a pause is a STATE and holds the view on its
            // own; if the setting is off and the pause is shown through "show on
            // pause", its view is one more notice and needs its deadline in BOTH
            // modes: without forcing it, the container re-resolved and hid it in the
            // same turn (change island-lista-de-activos).
            if (show) { PresentMediaSnapshot(status, forceNotice: !pauseCounts); return; }
            // A pause that does not count as active: it holds no compact view, in
            // BOTH modes (001 MOD RF-4/RF-7).
            if (MediaOwnsView())
            {
                bool forceHideFromCompact = !pauseCounts && !SettingsManager.Current.IslandShowOnPause;
                if (forceHideFromCompact) DropMediaView();
                else HidePerMode();
            }
            return;
        }

        // Stopped or unknown with no track change: there is no new music activity to
        // hold the view (in "Visible while active" the real state wins and the next
        // playback presents it; in "Temporary notice" the song change was already
        // handled above for any state). Only the music view itself folds back; the
        // view of another feature (timer, tray, shelf, calendar) is left alone.
        if (MediaOwnsView() && !_expanded) DropMediaView();
    }

    /// <summary>
    /// Is the current view the music? It holds with its rich view in front and also
    /// when the music lives inside a SCREEN that contains it (change island-pantallas):
    /// in both cases a music event affects it and not some other view.
    /// </summary>
    private bool MediaOwnsView() =>
        _contentMode == IslandContentMode.Media || ScreenOwnsMediaView();

    /// <summary>
    /// The music stopped holding the view: it folds back to whatever applies. With the
    /// EXPANDED view of a screen in front, that whole screen holds the view, so it
    /// stays if any other of its features is still holding it (the timer counting, a
    /// live notice) and, if not, it folds back just like its view
    /// rica.
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
    /// With the music view already in front, a metadata change repaints its
    /// title/artwork without re-expanding anything or touching the notice deadline
    /// (001 MOD RF-1/RF-2). With the music inside a screen, it repaints its card and
    /// its panel without touching the composition.
    /// </summary>
    private void RefreshVisibleMediaIfShown(bool metadataChanged)
    {
        if (!metadataChanged || !IsBoxShown) return;
        if (!MediaOwnsView()) return;
        if (Current() is { } shown) RefreshUi(shown);
    }

    /// <summary>
    /// Presents the current music view (compact or expanded) of the active session,
    /// adopting it if the snapshot did not have it (001 MOD RF-4/RF-11/RF-13). With
    /// <paramref name="status"/> null the card is left to read the session's real
    /// state: that is the case of a song change arriving with an intermediate state
    /// that must not be painted.
    /// </summary>
    private void PresentMediaSnapshot(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status,
        bool trackChanged = false, bool forceNotice = false)
    {
        var session = Current() ?? ActiveMediaSession();
        if (session == null) return;
        _currentId = session.Id;
        if (_expanded)
        {
            RefreshUi(session, status);
            return;
        }
        // The Bluetooth notice is in front with its deadline alive: media does not take
        // it over with an ordinary event (play/pause or metadata), because that
        // notice is a more recent, shorter notification. A song change IS a new event
        // and takes the view (change island-bluetooth-conectado
        // RF-1).
        if (!trackChanged && IsBoxShown && _contentMode == IslandContentMode.Bluetooth
            && _noticeUntil > DateTime.UtcNow)
            return;
        // Already on screen with its notice in force: a repeated player event must not
        // restart the deadline or make the notice REAPPEAR every few seconds
        // (001 MOD RF-2). A song change does premiere a new notice. With the music
        // inside a screen it is the same: its view is already in front.
        if (IsBoxShown && MediaOwnsView()
            && _noticeUntil > DateTime.UtcNow && !trackChanged)
        {
            RefreshUi(session, status);
            return;
        }
        ShowMusicCompact(session, status, forceNotice: forceNotice);
    }

    /// <summary>
    /// Playback state change (001 MOD RF-1): only updates the light identity (explicit
    /// pin and last-play) and publishes the activity reason. Presentation is resolved
    /// by ONE reconciliation with the latest state, so a burst neither repaints per
    /// event nor loses the final state.
    /// </summary>
    private void OnPlayState(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo? info)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (!_main.IsWidgetSessionAllowed(session)) return;
            // An event is the only reliable notice that something changed: the memos are
            // dropped whole (properties too, because the player may have announced
            // the new track only through the state: 001 MOD RF-1).
            InvalidateMediaReads(props: true);
            var status = info?.PlaybackStatus ?? SafeStatus(session);
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                // New playback does release the pinned session, but the focus Windows moves
                // on pause, resume or skip must not break an explicit selection.
                if (_mediaPinnedSessionId != null && _mediaPinnedSessionId != session.Id)
                    _mediaPinnedSessionId = null;
                NotePlay(session.Id);
                // New playback BECOMES the shown session: without adopting the snapshot, the
                // Island would keep showing the previous media
                // (001 MOD RF-4/RF-11).
                ApplyMediaSnapshot(new IslandMediaSnapshot(session.Id,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
                // Some players emit no metadata change and only announce the new
                // track through the state: comparing here the
                // identidad cubre ese caso (001 MOD RF-1).
                TryNoteTrackIdentity(session);
            }
            PostActivity(IslandActivityReason.Media);
        }));
    }

    /// <summary>
    /// Metadata change (title, artist, artwork): records the track identity - which is
    /// where the song-change detector comes from - and publishes activity; the snapshot
    /// and the presentation are resolved by reconciliation with the LATEST metadata
    /// (001 MOD RF-1). That way a burst from the player (title first, thumbnail
    /// later) paints once and with the final state.
    /// </summary>
    private void OnMediaProp(MediaSession session, GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (!_main.IsWidgetSessionAllowed(session)) return;
            // The event ALREADY carries the new properties: they are stored without
            // asking the system anything, and the repaint behind finds them ready.
            if (props != null) _propsMemo[session.Id] = FromProperties(props);
            _statusMemo.Remove(session.Id);
            // Events from foreign sessions cannot hijack an explicit selection.
            var sessionStatus = SafeStatus(session);
            bool playingNow = sessionStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            // New playback releases the explicit selection; a plain metadata change from a
            // foreign session cannot hijack it.
            if (_mediaPinnedSessionId != null && !IsDisplayedSession(session))
            {
                if (!playingNow) return;
                _mediaPinnedSessionId = null;
            }
            NoteTrackIdentity(props?.Title, props?.Artist);
            _mediaMetadataChanged = true;
            // The session announcing the track is the current activity: adopt it as the
            // snapshot so the notice shows the NEW media and not the previous one,
            // even if this player emits no playback state
            // (001 MOD RF-4/RF-11/RF-13). With ANOTHER session playing, that one wins.
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
    /// Session closed: releases the light identity and publishes activity so
    /// reconciliation resolves the next view (other playback, the timer or rest) in
    /// a single pass (001 MOD RF-1/RF-24).
    /// </summary>
    private void OnClosed(MediaSession session)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            if (IsDisplayedSession(session)) _artworkPublication?.Cancel();
            _lastPlay.Remove(session.Id);
            _statusMemo.Remove(session.Id);
            _propsMemo.Remove(session.Id);
            if (IsDisplayedSession(session) && _mediaPinnedSessionId == session.Id)
                _mediaPinnedSessionId = null;
            PostActivity(IslandActivityReason.Media);
        }));
    }

    // The music is gone (last session closed or invalid candidate): no empty
    // music compact and no dead data. An available timer can recover the view
    // immediately (001 MOD RF-24, 002 MOD RF-14).
    private void OnMusicUnavailable()
    {
        _artworkPublication?.Cancel();
        _mediaPinnedSessionId = null;
        _currentId = null;
        // What was known about the sessions no longer holds (they may have changed with
        // no event): the memos are dropped along with the snapshot.
        InvalidateMediaReads(props: true);
        ApplyMediaSnapshot(null);
        // The strip poll fires ExpandFromHover every 150 ms; without this pause it
        // would re-open the box we just closed under the cursor.
        _hoverSnoozeUntil = DateTime.UtcNow.AddSeconds(TimerReshowSnoozeSeconds);
        ClearClosedMediaView();
    }

    private void ClearClosedMediaView()
    {
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        _expanded = false;
        // When the last session closes no music residue must be left:
        // artwork, background, title or stale playback state
        // (001 MOD RF-24, 002 MOD RF-14).
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        ClearMusicResidue();
        if (TimerKeepsAlive()) ShowTimerCompact();
        else ShowInactiveOrHidden();
    }

    /// <summary>
    /// Removes artwork, blurred background, titles and stale playback state so
    /// the music view without a session leaves no controls or
    /// fondos residuales (001 MOD RF-24).
    /// </summary>
    private void ClearMusicResidue()
    {
        _lastStatus = null;
        // NOTE: _lastPresentedTrack is deliberately NOT cleared here. It is the identity of
        // the last PRESENTED song, not view data: clearing it made the same song,
        // returning from inactive rest, read as a track change and flip the artwork
        // on its own (001 RF-22: the flip only happens on a song change).
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
    /// REAL music activity (001 MOD RF-4, RF-11): counts the snapshot's session if
    /// it is playing, any allowed session playing right now - even if the snapshot
    /// points at another paused one or at none - and a pause only if the
    /// "pause counts as active" setting says so.
    /// </summary>
    private bool IsMediaActiveForContract()
    {
        if (!SettingsManager.Current.IslandEnabled || !SettingsManager.Current.IslandMediaEnabled) return false;
        if (!MusicAvailable() && FirstAllowed() == null) return false;
        if (_music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return true;
        if (NewestPlaying() != null) return true;
        if (!SettingsManager.Current.IslandPauseCountsActive) return false;
        // Pause counts as active: the snapshot's pause counts, and so does ANY allowed
        // session's. Looking only at the snapshot left out the common case of music
        // paused before the Island started (still no snapshot): the container fell to
        // rest and, with "return to inactive" off, the Island disappeared entirely.
        if (_music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) return true;
        return PausedAllowed() != null;
    }

    /// <summary>
    /// Is the music PAUSED right now? The snapshot counts, and so does any allowed session
    /// (music paused before the Island started has no snapshot yet). It is what the
    /// active-items list queries to decide if the pause is a STATE that holds the
    /// vista (change island-lista-de-activos).
    /// </summary>
    private bool MediaPausedNow() =>
        _music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
        || PausedAllowed() != null;

    /// <summary>Allowed session paused right now ("pause counts as active").</summary>
    private MediaSession? PausedAllowed()
    {
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
        {
            if (!_main.IsWidgetSessionAllowed(s)) continue;
            if (SafeStatus(s) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) return s;
        }
        return null;
    }

    /// <summary>
    /// Session representing the current music activity (001 MOD RF-4, RF-11): the
    /// snapshot's if it is playing; if not, the one playing NOW - adopting it as the
    /// snapshot - so the compact view shows what is actually active and not a stale
    /// pause; if nothing is playing, the snapshot's (or the first allowed) holds its
    /// own view.
    /// </summary>
    private MediaSession? ActiveMediaSession()
    {
        var snap = Current();
        if (_mediaPinnedSessionId != null)
        {
            if (snap != null) return snap;
            // The pinned session disappeared: only then is it allowed to recover the
            // most recently playing session.
            _mediaPinnedSessionId = null;
        }
        if (snap != null && SafeStatus(snap) == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return snap;
        // Something is playing right now (even if it is not the snapshot's): that is the
        // current activity and it is adopted as the music view (001 MOD RF-4, RF-11).
        var playing = NewestPlaying();
        if (playing != null)
        {
            if (snap == null || playing.Id != snap.Id) AdoptKnownSession();
            return playing;
        }
        // Nothing playing: the snapshot (a pause that counts as active) holds its view
        // or, without it, a known allowed session does (RF-13).
        return snap ?? AdoptKnownSession();
    }

    /// <summary>
    /// Allowed session with no snapshot: it is adopted as the music view - the one
    /// playing now, or the first allowed one if none is - leaving the snapshot
    /// consistent, so a music view without data is never opened
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
        // The expanded music view shows the snapshot's session (the user's current
        // selection, 001 MOD RF-5); with no snapshot a known allowed session is
        // adopted so the click opens its controls instead of an empty music view
        // (001 MOD RF-11, RF-13).
        var session = Current();
        if (session == null && MediaContentAvailable()) session = AdoptKnownSession();
        if (session == null) return false;
        ExpandSession(session);
        return true;
    }

    internal bool ShowMediaCompactFromContract()
    {
        // The session the compact view shows is the current activity's (adopting the
        // one playing if the snapshot did not have it), never a music view without
        // data (001 MOD RF-4, RF-11, RF-13).
        var session = ActiveMediaSession();
        if (session == null) return false;
        ShowMusicCompact(session);
        return true;
    }

    private void ShowMusicCompact(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null,
        bool forceNotice = false)
    {
        if (_timer.State == IslandTimerState.Alerting) return;
        if (!MusicAvailable()) return; // sin snapshot musical no hay vista musical (RF-13)
        ShowCompactView(IslandContentMode.Media, MediaFeature,
            () => RefreshUi(session, knownStatus), forceNotice: forceNotice);
    }

    private void ExpandSession(MediaSession session)
    {
        if (HasExclusive()) return;
        if (!MusicAvailable()) return; // sin snapshot musical no hay vista musical (RF-13)
        _currentId = session.Id;
        // guard: exclusivity is re-checked after cancelling inactive rest - a timer
        // alert arriving in that same turn wins (002 RF-2).
        ShowExpandedView(IslandContentMode.Media, MediaFeature, () => RefreshUi(session),
            guard: () => !HasExclusive());
    }

    // --- presentation ---

    private void RefreshUi(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null)
    {
        // Modal timer alert: music events wait for X or restart.
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        // With a screen's EXPANDED view that contains the music in front, the screen
        // owns the composition: the music only repaints its column. Otherwise the media
        // event adopts the most recent content (spec 001 RF-24) inside ITS screen:
        // expanded and with a combined screen, the composition of its columns; in any
        // other case, its usual rich view (a single one, which is what fits compact).
        if (!ScreenOwnsMediaView())
        {
            if (_expanded && MediaFeature is { } mediaFeature && ScreenOfFeatureIsCombined(mediaFeature))
            {
                _screenIndex = ResolveScreenIndexFor(mediaFeature.Id);
                _contentMode = IslandContentMode.Screen;
                RefreshScreenMembers();
            }
            else
            {
                _contentMode = IslandContentMode.Media;
            }
            ApplyContentVisibility();
        }
        var status = knownStatus ?? SafeStatus(session) ?? _lastStatus;
        if (status != null) _lastStatus = status;
        PaintGlyph();
        // The properties come from the memo (or from a single read if not known yet):
        // the repaint stops crossing to the media system on every event (001 MOD RF-1).
        var mediaProps = MediaPropsOf(session);
        if (mediaProps != null)
        {
            var snapshot = new MediaArtworkSnapshot(session.ControlSession, mediaProps.Title, mediaProps.Artist, mediaProps.Artwork);
            _artworkPublication ??= new MediaArtworkPublication(Dispatcher, BitmapHelper.ReadMediaArtworkAsync, PublishIslandSong);
            _artworkPublication.Observe(snapshot);
            // Rest clears view data but retains publication history. Restore without a new track notice.
            if (_artworkPublication.Published == snapshot
                && (_lastPresentedTrack != snapshot || string.IsNullOrEmpty(SongTitle.Text)))
                PublishIslandSong(snapshot);
        }
        ApplyCapabilities(session);
        UpdateSeek(session);
        UpdateEqButton();
    }

    private void PublishIslandSong(MediaArtworkSnapshot snapshot)
    {
        if (_disposed || !MediaContentAvailable()) return;
        var session = Current() ?? ActiveMediaSession();
        if (session == null || !Equals(session.ControlSession, snapshot.Session)) return;
        _propsMemo[session.Id] = new IslandMediaProps(snapshot.Title, snapshot.Artist, snapshot.Artwork);
        if (_timer.State == Classes.IslandTimerState.Alerting) return;
        BitmapImage? art = snapshot.Artwork;
        string title = string.IsNullOrWhiteSpace(snapshot.Title)
            ? IslandStrings.Get("IslandUnknownTitle", "Unknown title")
            : snapshot.Title;
        string artist = string.IsNullOrWhiteSpace(snapshot.Artist) ? string.Empty : snapshot.Artist;
        BitmapHelper.GetDominantColors(art);
        SongTitle.Text = title;
        SongArtist.Text = artist;
        SongArtist.Visibility = string.IsNullOrEmpty(artist) ? Visibility.Collapsed : Visibility.Visible;
        CompactTitle.Text = title;
        SetBackground(art);
        bool trackChanged = _lastPresentedTrack != null && !snapshot.SameTrack(_lastPresentedTrack);
        _lastPresentedTrack = snapshot;
        StartAlbumFlip(art);
        if (trackChanged && SettingsManager.Current.IslandShowOnTrackChange) PlayTrackPop();
        if (MediaOwnsView())
        {
            SyncMeasuredHeight();
            if (_expanded || _p > 0.05) ApplyFrame();
        }
    }

    /// <summary>
    /// Paints the artwork (or its music-note placeholder) in compact and expanded
    /// without animation. It is the swap that happens at the flip's blind spot and
    /// also the path for the first artwork or with animations off.
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

    private AlbumArtTransition? _albumTransition;
    private AlbumArtTransition AlbumTransition => _albumTransition ??=
        new(ApplyAlbumArt, (CompactArtWrap, CompactArtFlipScale), (ExpandedArtWrap, ExpandedArtFlipScale));

    private void SetAlbumArt(BitmapImage? art) => AlbumTransition.Set(art);
    private void StopAlbumFlip() => _albumTransition?.Stop();

    private void StartAlbumFlip(BitmapImage? art) => AlbumTransition.Show(art, AnimationsEnabled,
        MainWindow.getDuration(), SettingsManager.Current.AlbumArtChangeAnimation == 0);

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

    /// <summary>
    /// A session's playback state, from the pass memo if it was already read: a single
    /// reconciliation asks for it many times (current active item, equalizer, card
    /// equalizer, capabilities) and each ask used to be a cross-process call.
    /// </summary>
    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? SafeStatus(MediaSession session)
    {
        if (session.ControlSession is null) return null;
        if (_statusMemo.TryGetValue(session.Id, out var known)) return known;
        GlobalSystemMediaTransportControlsSessionPlaybackStatus? status;
        try { status = session.ControlSession.GetPlaybackInfo()?.PlaybackStatus; }
        catch { status = null; }
        _statusMemo[session.Id] = status;
        return status;
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
        if (_drag) return;
        try
        {
            var tl = session.ControlSession.GetTimelineProperties();
            if (tl.MaxSeekTime.TotalSeconds >= 1)
            {
                bool playing = session.ControlSession.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var pos = playing ? tl.Position + (DateTimeOffset.UtcNow - tl.LastUpdatedTime) : tl.Position;
                if (pos < tl.MinSeekTime) pos = tl.MinSeekTime;
                if (pos > tl.MaxSeekTime) pos = tl.MaxSeekTime;
                Seekbar.Maximum = tl.MaxSeekTime.TotalSeconds;
                Seekbar.Minimum = tl.MinSeekTime.TotalSeconds;
                if (!_drag) { Seekbar.Value = pos.TotalSeconds; PosText.Text = Fmt(pos); }
                DurText.Text = FmtRemaining(tl.MaxSeekTime - pos);
                UpdateTimelineVisual();
                return;
            }
        }
        catch { }
        Seekbar.Minimum = 0; Seekbar.Maximum = 100; Seekbar.Value = 0; PosText.Text = "0:00"; DurText.Text = "0:00";
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
        var all = _main.mediaManager.CurrentMediaSessions.Values.Where(s => _main.IsWidgetSessionAllowed(s)).ToList();
        if (!MusicAvailable() || all.Count <= 1) return;
        int i = all.FindIndex(s => s.Id == (Current()?.Id ?? DisplayedMediaId));
        var next = all[(i + 1) % all.Count];
        PinMediaSession(next);
        var nextStatus = SafeStatus(next) ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
        ApplyMediaSnapshot(new IslandMediaSnapshot(next.Id, nextStatus));
        if (_expanded) RefreshUi(next, nextStatus);
        else ShowMusicCompact(next, nextStatus);
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
