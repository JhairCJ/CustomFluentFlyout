namespace FluentFlyout.Core;

public sealed class PlaybackChangedEventArgs : EventArgs
{
    public PlaybackChangedEventArgs(string sessionId, MediaPlaybackState state, MediaSnapshot? snapshot = null)
    {
        SessionId = sessionId;
        State = state;
        Snapshot = snapshot;
    }

    public string SessionId { get; }

    public MediaPlaybackState State { get; }

    public MediaSnapshot? Snapshot { get; }
}
