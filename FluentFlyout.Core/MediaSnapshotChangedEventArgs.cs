namespace FluentFlyout.Core;

public sealed class MediaSnapshotChangedEventArgs : EventArgs
{
    public MediaSnapshotChangedEventArgs(MediaSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public MediaSnapshot Snapshot { get; }
}
