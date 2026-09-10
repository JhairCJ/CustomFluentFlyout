namespace FluentFlyout.Platform.Windows;

public sealed class AudioSamplesEventArgs : EventArgs
{
    public AudioSamplesEventArgs(ReadOnlyMemory<float> samples, int sampleRate, int channels)
    {
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public ReadOnlyMemory<float> Samples { get; }

    public int SampleRate { get; }

    public int Channels { get; }
}
