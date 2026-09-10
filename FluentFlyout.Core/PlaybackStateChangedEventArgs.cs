namespace FluentFlyout.Core;

public sealed class PlaybackStateChangedEventArgs : EventArgs
{
    public PlaybackStateChangedEventArgs(
        string sessionId,
        MediaPlaybackState state,
        MediaSnapshot? snapshot = null)
    {
        SessionId = sessionId;
        State = state;
        Snapshot = snapshot;
    }

    public string SessionId { get; }

    public MediaPlaybackState State { get; }

    public MediaSnapshot? Snapshot { get; }
}
