using FluentFlyout.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FluentFlyout.Platform.Windows;

/// <summary>
/// WASAPI loopback -> ring buffer -> silence gate -> hop-FFT -> raw bar targets.
/// Ported from the WPF visualizer pipeline: the capture side writes raw targets
/// with NO smoothing (the renderer owns all easing), the silence gate zeroes them
/// within ~1-2 ms of the audio stopping, and a watchdog restarts a dead capture.
/// </summary>
public sealed class VisualizerEngine : IDisposable
{
    public const int FftLength = SpectrumAnalyzer.FftSize;
    private const int FftOrder = SpectrumAnalyzer.FftOrder;
    private const int FftHopHighRefresh = 512;

    private readonly SpectrumAnalyzer analyzer;
    private WasapiRecorder? capture;
    private MMDevice? renderDevice;
    private System.Timers.Timer? captureWatchdog;
    private DateTime lastDataAvailableUtc = DateTime.MinValue;
    private int restartInProgress;
    private bool disposed;

    private readonly float[] ring = new float[FftLength];
    private int ringPos;
    private int samplesSinceHop;
    private int fftHop;

    // Sliding silence gate: RMS over the newest GateWindow samples (~5 ms),
    // re-evaluated every GateTick samples (~1.3 ms) so the gate answers within
    // one tick instead of waiting for the ~85 ms FFT window to drain.
    private const int GateWindow = 256;
    private const int GateTick = 64;
    private readonly float[] gateSquares = new float[GateWindow];
    private float gateSum;
    private int gatePos;
    private int samplesSinceGate;
    private volatile bool gateSilent = true;

    private int bytesPerSample;
    private int sampleRate;
    private volatile bool isRunning;

    public VisualizerEngine(int barCount = 10)
    {
        analyzer = new SpectrumAnalyzer(barCount);
        fftHop = FftLength;
    }

    public event EventHandler<VisualizerFrame>? FrameAvailable;

    /// <summary>Raw per-bar targets, written by the capture thread with no smoothing.</summary>
    public ReadOnlySpan<float> Targets => analyzer.Targets;

    public bool IsRunning => isRunning;

    public int AudioSensitivity
    {
        get => analyzer.AudioSensitivity;
        set => analyzer.AudioSensitivity = value;
    }

    public int AudioPeakLevel
    {
        get => analyzer.AudioPeakLevel;
        set => analyzer.AudioPeakLevel = value;
    }

    public bool HighRefreshRate
    {
        get => fftHop == FftHopHighRefresh;
        set => fftHop = value ? FftHopHighRefresh : FftLength;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (isRunning)
            return;

        // Fresh capture state: stale audio in the ring would ghost into the first windows.
        Array.Clear(ring, 0, ring.Length);
        Array.Clear(gateSquares, 0, gateSquares.Length);
        ringPos = 0;
        samplesSinceHop = 0;
        gateSum = 0;
        gatePos = 0;
        samplesSinceGate = 0;
        gateSilent = true;
        lastDataAvailableUtc = DateTime.UtcNow;

        try
        {
            capture?.Dispose();
            capture = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .Build();

            bytesPerSample = capture.WaveFormat.BitsPerSample / 8;
            sampleRate = capture.WaveFormat.SampleRate;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            isRunning = true;

            var watchdog = new System.Timers.Timer(1000) { AutoReset = true };
            watchdog.Elapsed += (_, _) =>
            {
                if (isRunning && !disposed
                    && DateTime.UtcNow - lastDataAvailableUtc > TimeSpan.FromSeconds(2))
                {
                    RequestRestart("no capture callbacks for over 2s");
                }
            };
            watchdog.Start();
            var previousWatchdog = Interlocked.Exchange(ref captureWatchdog, watchdog);
            previousWatchdog?.Stop();
            previousWatchdog?.Dispose();
        }
        catch (Exception)
        {
            isRunning = false;
        }
    }

    public void Stop()
    {
        StopCapture();
    }

    private void StopCapture()
    {
        isRunning = false;

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            capture = null;
        }

        var watchdog = Interlocked.Exchange(ref captureWatchdog, null);
        if (watchdog is not null)
        {
            watchdog.Stop();
            watchdog.Dispose();
        }
    }

    private void RequestRestart(string reason)
    {
        if (disposed || Interlocked.Exchange(ref restartInProgress, 1) == 1)
            return;

        Task.Run(async () =>
        {
            try
            {
                StopCapture();
                int attempt = 0;
                while (!isRunning && !disposed)
                {
                    await Task.Delay(Math.Min(500 * (1 << Math.Min(attempt, 4)), 5000));
                    attempt++;
                    Start();
                    if (isRunning)
                        return;
                }
            }
            finally
            {
                Interlocked.Exchange(ref restartInProgress, 0);
            }
        });
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (!isRunning)
            return;

        // Any callback — even an empty one — proves the capture is alive.
        lastDataAvailableUtc = DateTime.UtcNow;
        if (buffer.IsEmpty)
            return;

        // Silent packets feed true zeros so the gate answers immediately.
        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            int silentSamples = buffer.Length / Math.Max(bytesPerSample, 1);
            for (int i = 0; i < silentSamples; i++)
                PushSample(0f);
            return;
        }

        // Every sample advances the ring, including the right channel of an
        // interleaved stereo stream: the WPF band response was tuned against
        // that and a mono downmix would shift everything an octave up.
        if (bytesPerSample == 4)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            for (int i = 0; i < samples.Length; i++)
                PushSample(samples[i]);
        }
        else if (bytesPerSample == 2)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer);
            for (int i = 0; i < samples.Length; i++)
                PushSample(samples[i] * (1f / 32768f));
        }
    }

    private void PushSample(float s)
    {
        ring[ringPos] = s;
        ringPos = (ringPos + 1) & (FftLength - 1);

        float sq = s * s;
        gateSum -= gateSquares[gatePos];
        gateSquares[gatePos] = sq;
        gateSum += sq;
        gatePos = (gatePos + 1) & (GateWindow - 1);

        if (++samplesSinceGate >= GateTick)
        {
            samplesSinceGate = 0;
            EvaluateGate();
        }

        if (++samplesSinceHop >= fftHop)
        {
            samplesSinceHop = 0;
            RunSpectrum();
        }
    }

    private void EvaluateGate()
    {
        float rms = (float)Math.Sqrt(Math.Max(gateSum, 0f) / GateWindow);
        gateSilent = rms < analyzer.GateThreshold;
    }

    private void RunSpectrum()
    {
        if (gateSilent)
        {
            // Gate is authoritative: keep targets at zero, skip the FFT entirely.
            var silentFrame = new VisualizerFrame(new float[analyzer.BarCount], 0, false, DateTimeOffset.UtcNow);
            FrameAvailable?.Invoke(this, silentFrame);
            return;
        }

        // Chronological window; the Core analyzer applies the same Hamming
        // curve and runs the FFT itself, so feed it the raw ring window.
        float[] windowed = new float[FftLength];
        for (int j = 0; j < FftLength; j++)
        {
            int src = ringPos + j;
            if (src >= FftLength) src -= FftLength;
            windowed[j] = ring[src];
        }

        var frame = analyzer.Analyze(windowed, DateTimeOffset.UtcNow);
        FrameAvailable?.Invoke(this, frame);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            RequestRestart("recording stopped with error");
        else if (isRunning)
            RequestRestart("recording stopped unexpectedly");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopCapture();
        GC.SuppressFinalize(this);
    }
}
