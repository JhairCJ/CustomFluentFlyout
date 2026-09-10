namespace FluentFlyout.Core;

public interface IMediaSessionService
{
    event EventHandler<MediaSnapshotChangedEventArgs>? SnapshotChanged;

    event EventHandler<PlaybackChangedEventArgs>? PlaybackChanged;

    event EventHandler<TimelineChangedEventArgs>? TimelineChanged;

    IReadOnlyList<MediaSnapshot> GetSessions();

    MediaSnapshot? GetFocusedSession();

    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task PlayPauseAsync(CancellationToken cancellationToken = default);

    Task NextAsync(CancellationToken cancellationToken = default);

    Task PreviousAsync(CancellationToken cancellationToken = default);

    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
}
