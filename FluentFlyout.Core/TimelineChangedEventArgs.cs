namespace FluentFlyout.Core;

public sealed class TimelineChangedEventArgs : EventArgs
{
    public TimelineChangedEventArgs(
        string sessionId,
        TimeSpan position,
        TimeSpan duration,
        DateTimeOffset? lastUpdatedUtc = null,
        MediaSnapshot? snapshot = null)
    {
        SessionId = sessionId;
        Position = position;
        Duration = duration;
        LastUpdatedUtc = lastUpdatedUtc;
        Snapshot = snapshot;
    }

    public string SessionId { get; }

    public TimeSpan Position { get; }

    public TimeSpan Duration { get; }

    public DateTimeOffset? LastUpdatedUtc { get; }

    public MediaSnapshot? Snapshot { get; }
}
