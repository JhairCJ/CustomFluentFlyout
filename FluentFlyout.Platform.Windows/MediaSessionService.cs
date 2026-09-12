using FluentFlyout.Core;
using Windows.Media.Control;
using WindowsMediaController;

namespace FluentFlyout.Platform.Windows;

/// <summary>Projects Windows SMTC sessions exposed by Dubya.WindowsMediaController into Core snapshots.</summary>
/// <remarks>
/// Publishes <see cref="IMediaSessionService.SnapshotChanged"/> for every meaningful change:
/// track metadata (title/artist/artwork) via <c>OnAnyMediaPropertyChanged</c>, new sessions via
/// <c>OnAnySessionOpened</c>, play/pause via <c>OnAnyPlaybackStateChanged</c> and session removal
/// via <c>OnAnySessionClosed</c>. Metadata bursts are debounced per tick so a skip-forward storm
/// does not flood the UI thread; the last snapshot of each burst wins.
/// </remarks>
public sealed class MediaSessionService : IMediaSessionService
{
    private const int MetadataDebounceMs = 150;

    private readonly MediaManager mediaManager = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly object snapshotsGate = new();
    private Dictionary<string, MediaSnapshot> snapshots = [];
    private readonly HashSet<string> pendingMetadataRefresh = [];
    private System.Threading.Timer? metadataDebounceTimer;
    private bool started;

    public MediaSessionService()
    {
        mediaManager.OnAnyMediaPropertyChanged += OnMediaPropertyChanged;
        mediaManager.OnAnyPlaybackStateChanged += OnPlaybackStateChanged;
        mediaManager.OnAnyTimelinePropertyChanged += OnTimelineChanged;
        mediaManager.OnAnySessionOpened += OnSessionOpened;
        mediaManager.OnAnySessionClosed += OnSessionClosed;
    }

    public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;

    public event EventHandler<MediaSnapshotChangedEventArgs>? SnapshotChanged;

    public event EventHandler<PlaybackChangedEventArgs>? PlaybackChanged;

    public event EventHandler<TimelineChangedEventArgs>? TimelineChanged;

    public IReadOnlyList<MediaSnapshot> GetSessions()
    {
        lock (snapshotsGate)
            return snapshots.Values.ToArray();
    }

    public MediaSnapshot? GetFocusedSession()
    {
        MediaManager.MediaSession? focused = mediaManager.GetFocusedSession();
        if (focused is null)
            return null;

        lock (snapshotsGate)
            return snapshots.GetValueOrDefault(focused.Id);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        mediaManager.ForceUpdate();

        Dictionary<string, MediaSnapshot> refreshed = [];
        foreach (MediaManager.MediaSession session in mediaManager.CurrentMediaSessions.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            refreshed[session.Id] = await CreateSnapshotAsync(session).ConfigureAwait(false);
        }

        lock (snapshotsGate)
        {
            snapshots = refreshed;
            pendingMetadataRefresh.Clear();
        }

        foreach (MediaSnapshot snapshot in refreshed.Values)
            SnapshotChanged?.Invoke(this, new MediaSnapshotChangedEventArgs(snapshot));
    }

    public async Task PlayPauseAsync(CancellationToken cancellationToken = default)
    {
        MediaManager.MediaSession? session = mediaManager.GetFocusedSession();
        if (session is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        await session.ControlSession.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync(CancellationToken cancellationToken = default)
    {
        MediaManager.MediaSession? session = mediaManager.GetFocusedSession();
        if (session is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        await session.ControlSession.TrySkipNextAsync();
    }

    public async Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        MediaManager.MediaSession? session = mediaManager.GetFocusedSession();
        if (session is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        await session.ControlSession.TrySkipPreviousAsync();
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        MediaManager.MediaSession? session = mediaManager.GetFocusedSession();
        if (session is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        await session.ControlSession.TryChangePlaybackPositionAsync(Math.Max(0, position.Ticks));
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (started)
            return;

        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!started)
            {
                // TODO: if the package API changes, isolate a replacement SMTC provider behind this service.
                await mediaManager.StartAsync();
                started = true;
            }
        }
        finally
        {
            startGate.Release();
        }
    }

    private async Task<MediaSnapshot> CreateSnapshotAsync(MediaManager.MediaSession session)
    {
        GlobalSystemMediaTransportControlsSession controlSession = session.ControlSession;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = controlSession.GetPlaybackInfo();
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = controlSession.GetTimelineProperties();
        GlobalSystemMediaTransportControlsSessionMediaProperties properties = await controlSession.TryGetMediaPropertiesAsync();

        byte[]? artwork = null;
        if (properties.Thumbnail != null)
        {
            try
            {
                using var stream = await properties.Thumbnail.OpenReadAsync();
                using var memoryStream = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(memoryStream);
                artwork = memoryStream.ToArray();
            }
            catch { }
        }

        return new MediaSnapshot
        {
            SessionId = session.Id,
            AppId = controlSession.SourceAppUserModelId,
            DisplayName = controlSession.SourceAppUserModelId,
            Title = properties.Title ?? string.Empty,
            Artist = properties.Artist ?? string.Empty,
            PlaybackState = ToCoreState(playbackInfo.PlaybackStatus),
            Position = timeline.Position,
            Duration = timeline.EndTime,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            CanSeek = playbackInfo.Controls.IsPlaybackPositionEnabled,
            ArtworkBytes = artwork,
        };
    }

    // ------------------------------------------------------------------
    // MediaManager event handlers (thread-pool callbacks)
    // ------------------------------------------------------------------

    private void OnMediaPropertyChanged(MediaManager.MediaSession session, GlobalSystemMediaTransportControlsSessionMediaProperties properties)
        => QueueMetadataRefresh(session.Id);

    private void OnSessionOpened(MediaManager.MediaSession session)
        => QueueMetadataRefresh(session.Id);

    /// <summary>
    /// Play/pause must surface instantly: merge the new state into the cached snapshot
    /// and publish it right away (no debounce), then let the metadata pipeline follow up.
    /// </summary>
    private void OnPlaybackStateChanged(MediaManager.MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        MediaPlaybackState state = ToCoreState(playbackInfo.PlaybackStatus);
        MediaSnapshot? snapshot;
        lock (snapshotsGate)
        {
            snapshot = snapshots.GetValueOrDefault(session.Id);
            if (snapshot is not null && snapshot.PlaybackState != state)
            {
                snapshot = snapshot with { PlaybackState = state };
                snapshots[session.Id] = snapshot;
            }
        }

        if (snapshot is null)
        {
            // Session not cached yet (opened before StartAsync finished): pull it in.
            QueueMetadataRefresh(session.Id);
        }
        else
        {
            SnapshotChanged?.Invoke(this, new MediaSnapshotChangedEventArgs(snapshot));
        }

        PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(session.Id, state, snapshot));
        PlaybackChanged?.Invoke(this, new PlaybackChangedEventArgs(session.Id, state, snapshot));
    }

    private void OnTimelineChanged(MediaManager.MediaSession session, GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        // Keep the cache fresh for GetSessions/GetFocusedSession consumers; do not
        // publish — timeline ticks arrive every second and would spam the UI.
        lock (snapshotsGate)
        {
            if (snapshots.GetValueOrDefault(session.Id) is { } existing)
            {
                snapshots[session.Id] = existing with
                {
                    Position = timeline.Position,
                    Duration = timeline.EndTime,
                    LastUpdatedUtc = DateTimeOffset.UtcNow,
                };
            }
        }

        TimelineChanged?.Invoke(this, new TimelineChangedEventArgs(
            session.Id,
            timeline.Position,
            timeline.EndTime,
            DateTimeOffset.UtcNow,
            GetSessionSnapshot(session.Id)));
    }

    private void OnSessionClosed(MediaManager.MediaSession session)
    {
        MediaSnapshot? removed;
        lock (snapshotsGate)
        {
            removed = snapshots.GetValueOrDefault(session.Id);
            snapshots.Remove(session.Id);
            pendingMetadataRefresh.Remove(session.Id);
        }

        // Publish the removal as a Closed snapshot so hosts can hide/collapse.
        if (removed is not null)
            SnapshotChanged?.Invoke(this, new MediaSnapshotChangedEventArgs(removed with { PlaybackState = MediaPlaybackState.Closed }));
    }

    private MediaSnapshot? GetSessionSnapshot(string sessionId)
    {
        lock (snapshotsGate)
            return snapshots.GetValueOrDefault(sessionId);
    }

    // ------------------------------------------------------------------
    // Debounced metadata publication
    // ------------------------------------------------------------------

    /// <summary>
    /// SMTC delivers a song change as a burst of property events. Rearm a one-shot
    /// debounce timer (last wins) and flush all pending sessions together.
    /// </summary>
    private void QueueMetadataRefresh(string sessionId)
    {
        lock (snapshotsGate)
        {
            pendingMetadataRefresh.Add(sessionId);
            if (metadataDebounceTimer is null)
            {
                metadataDebounceTimer = new System.Threading.Timer(
                    _ => _ = FlushPendingMetadataAsync(),
                    null,
                    MetadataDebounceMs,
                    Timeout.Infinite);
            }
            else
            {
                metadataDebounceTimer.Change(MetadataDebounceMs, Timeout.Infinite);
            }
        }
    }

    private async Task FlushPendingMetadataAsync()
    {
        string[] ids;
        lock (snapshotsGate)
        {
            if (pendingMetadataRefresh.Count == 0)
                return;
            ids = [.. pendingMetadataRefresh];
            pendingMetadataRefresh.Clear();
        }

        // Ensure the manager is running before touching its session list.
        if (!started)
        {
            try { await EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { return; }
        }

        foreach (MediaManager.MediaSession session in mediaManager.CurrentMediaSessions.Values.ToArray())
        {
            if (!ids.Contains(session.Id))
                continue;

            try
            {
                MediaSnapshot snapshot = await CreateSnapshotAsync(session).ConfigureAwait(false);
                bool publish;
                lock (snapshotsGate)
                {
                    publish = !SameTrackIdentity(snapshots.GetValueOrDefault(session.Id), snapshot);
                    if (publish)
                        snapshots[session.Id] = snapshot;
                }

                if (publish)
                    SnapshotChanged?.Invoke(this, new MediaSnapshotChangedEventArgs(snapshot));
            }
            catch
            {
                // Session may have closed mid-flight; the Closed event handles cleanup.
            }
        }
    }

    /// <summary>
    /// Identity dedupe: artwork bytes are re-read on every snapshot, so compare by
    /// presence rather than content; record equality would always report a change.
    /// </summary>
    private static bool SameTrackIdentity(MediaSnapshot? a, MediaSnapshot? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Title == b.Title
            && a.Artist == b.Artist
            && a.PlaybackState == b.PlaybackState
            && (a.ArtworkBytes is null) == (b.ArtworkBytes is null);
    }

    private static MediaPlaybackState ToCoreState(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.Closed,
        _ => MediaPlaybackState.Unknown,
    };
}
