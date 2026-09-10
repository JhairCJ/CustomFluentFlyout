namespace FluentFlyout.Core;

public sealed record VisualizerFrame(
    IReadOnlyList<float> Bars,
    float Peak,
    bool HasSignal,
    DateTimeOffset Timestamp);
