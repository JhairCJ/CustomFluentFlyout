using FluentFlyout.Core;
using Windows.Media.Control;
using WindowsMediaController;

namespace FluentFlyout.Platform.Windows;

/// <summary>Projects Windows SMTC sessions exposed by Dubya.WindowsMediaController into Core snapshots.</summary>
public sealed class MediaSessionService : IMediaSessionService
{
    private readonly MediaManager mediaManager = new();
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly object snapshotsGate = new();
    private Dictionary<string, MediaSnapshot> snapshots = [];
    private bool started;

    public MediaSessionService()
    {
        mediaManager.OnAnyPlaybackStateChanged += OnPlaybackStateChanged;
        mediaManager.OnAnyTimelinePropertyChanged += OnTimelineChanged;
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
            snapshots = refreshed;

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

    private void OnPlaybackStateChanged(MediaManager.MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        MediaSnapshot? snapshot;
        lock (snapshotsGate)
            snapshot = snapshots.GetValueOrDefault(session.Id);

        PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(
            session.Id,
            ToCoreState(playbackInfo.PlaybackStatus),
            snapshot));
        PlaybackChanged?.Invoke(this, new PlaybackChangedEventArgs(
            session.Id,
            ToCoreState(playbackInfo.PlaybackStatus),
            snapshot));
    }

    private void OnTimelineChanged(MediaManager.MediaSession session, GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        MediaSnapshot? snapshot;
        lock (snapshotsGate)
            snapshot = snapshots.GetValueOrDefault(session.Id);

        TimelineChanged?.Invoke(this, new TimelineChangedEventArgs(
            session.Id,
            timeline.Position,
            timeline.EndTime,
            DateTimeOffset.UtcNow,
            snapshot));
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
