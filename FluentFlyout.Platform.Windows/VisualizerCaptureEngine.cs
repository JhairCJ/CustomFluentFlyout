using NAudio.Wave;

namespace FluentFlyout.Platform.Windows;

/// <summary>Captures the Windows loopback mix without exposing NAudio to the App layer.</summary>
public sealed class VisualizerCaptureEngine : IDisposable
{
    private WasapiRecorder? capture;
    private bool disposed;

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsRunning)
            return;

        capture?.Dispose();
        capture = null;

        capture = new WasapiRecorderBuilder()
            .WithLoopbackCapture()
            .Build();
        capture.DataAvailable += (buffer, _, _, _) => CaptureOnDataAvailable(buffer);
        capture.RecordingStopped += (_, _) => IsRunning = false;
        capture.StartRecording();
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        capture?.StopRecording();
        IsRunning = false;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        Stop();
        if (capture is not null)
        {
            capture.Dispose();
            capture = null;
        }

        disposed = true;
        GC.SuppressFinalize(this);
    }

    private void CaptureOnDataAvailable(ReadOnlySpan<byte> buffer)
    {
        if (capture is null || buffer.Length <= 0)
            return;

        var format = capture.WaveFormat;
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample is not (2 or 4))
            return;

        var sampleCount = buffer.Length / bytesPerSample;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var offset = i * bytesPerSample;
            samples[i] = bytesPerSample == 4
                ? BitConverter.ToSingle(buffer.Slice(offset, sizeof(float)))
                : BitConverter.ToInt16(buffer.Slice(offset, sizeof(short))) / 32768f;
        }

        SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(samples, format.SampleRate, format.Channels));
    }

}
