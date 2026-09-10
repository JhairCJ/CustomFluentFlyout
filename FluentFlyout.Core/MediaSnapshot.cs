namespace FluentFlyout.Core;

public sealed record MediaSnapshot
{
    public required string SessionId { get; init; }

    public string AppId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public MediaPlaybackState PlaybackState { get; init; } = MediaPlaybackState.Unknown;

    public TimeSpan Position { get; init; } = TimeSpan.Zero;

    public TimeSpan Duration { get; init; } = TimeSpan.Zero;

    public DateTimeOffset? LastUpdatedUtc { get; init; }

    public bool CanSeek { get; init; }

    public bool IsMuted { get; init; }

    public double? Volume { get; init; }

    public byte[]? ArtworkBytes { get; init; }
}
