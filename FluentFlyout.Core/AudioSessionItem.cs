namespace FluentFlyout.Core;

public sealed class AudioSessionItem
{
    public required int ProcessId { get; init; }
    public required string DisplayName { get; set; }
    public required float Volume { get; set; }
    public required bool IsMuted { get; set; }
    public bool IsActive { get; set; }
    public string? ExecutablePath { get; set; }
}
