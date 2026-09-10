namespace FluentFlyout.Core;

/// <summary>
/// Pure spectrum analyzer used by both the taskbar renderer and tests.
/// Ported from the WPF visualizer DSP: log-spaced bands over 40 Hz - 8 kHz with a
/// progressive high-frequency boost, dB-domain normalization driven by sensitivity /
/// peak settings, and an instantaneous RMS silence gate. Audio capture and bitmap
/// presentation stay outside this module.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int FftSize = 4096;
    public const int FftOrder = 12; // log2(FftSize)

    private const double MinFreq = 40;   // Hz
    private const double MaxFreq = 8000; // Hz

    private readonly float[] _windowTable;
    private readonly float[] _targets;

    private struct BandRange
    {
        public int StartBin;
        public int EndBin;
        public float Boost;
    }

    private BandRange[] _bandTable = [];
    private int _bandKeyBars = -1;
    private int _bandKeyRate = -1;
    private int _bandKeySens = -1;
    private int _bandKeyPeak = -1;
    private float _bandMinDb;
    private float _bandMaxDb;

    public SpectrumAnalyzer(int barCount = 10)
    {
        if (barCount < 1)
            throw new ArgumentOutOfRangeException(nameof(barCount));

        BarCount = barCount;
        _targets = new float[barCount];
        _windowTable = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
            _windowTable[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
    }

    public int BarCount { get; }

    /// <summary>Raw RMS below which the frame is considered silence.</summary>
    public float GateThreshold { get; set; } = 1e-4f; // ~ -80 dBFS

    /// <summary>Audio sensitivity setting (0..10). Lowers the dB floor.</summary>
    public int AudioSensitivity { get; set; } = 2;

    /// <summary>Peak level setting (0..10). Sets the dB ceiling.</summary>
    public int AudioPeakLevel { get; set; } = 3;

    /// <summary>
    /// Analyzes one FFT window (already in chronological order, mono or interleaved —
    /// the WPF visualizer deliberately consumed interleaved stereo without a downmix,
    /// and the band response was tuned against that, so the same quirk is preserved).
    /// Returns raw per-bar intensities in 0..1 with no smoothing applied: all easing
    /// lives in the renderer so smoothing never stacks.
    /// </summary>
    public VisualizerFrame Analyze(ReadOnlySpan<float> samples, DateTimeOffset? timestamp = null)
    {
        // Silence gate is authoritative and immediate: real loopback silence is
        // exact zeros, so a short-window RMS cut is unambiguous.
        double sumSquares = 0;
        for (int i = 0; i < samples.Length; i++)
            sumSquares += (double)samples[i] * samples[i];
        double rms = samples.Length == 0 ? 0 : Math.Sqrt(sumSquares / samples.Length);

        var bars = new float[BarCount];
        if (rms < GateThreshold)
            return new VisualizerFrame(bars, 0, false, timestamp ?? DateTimeOffset.UtcNow);

        // Copy + window + in-place radix-2 FFT.
        Span<double> real = stackalloc double[FftSize];
        Span<double> imaginary = stackalloc double[FftSize];
        int length = Math.Min(samples.Length, FftSize);
        for (int i = 0; i < length; i++)
        {
            real[i] = samples[i] * _windowTable[i];
            imaginary[i] = 0;
        }
        Transform(real, imaginary);

        EnsureBandTable();

        float peak = 0;
        int bins = FftSize / 2;
        for (int i = 0; i < BarCount; i++)
        {
            int startBin = _bandTable[i].StartBin;
            int endBin = Math.Min(_bandTable[i].EndBin, bins);

            float maxAmplitude = 0;
            for (int j = startBin; j < endBin; j++)
            {
                float amplitude = (float)Math.Sqrt(real[j] * real[j] + imaginary[j] * imaginary[j]);
                if (amplitude > maxAmplitude)
                    maxAmplitude = amplitude;
            }

            maxAmplitude *= _bandTable[i].Boost;
            if (maxAmplitude < 0.001f)
                maxAmplitude = 0.001f;

            float db = 20f * (float)Math.Log10(maxAmplitude);
            float intensity = (db - _bandMinDb) / (_bandMaxDb - _bandMinDb);
            intensity = Math.Clamp(intensity, 0f, 1f);

            bars[i] = intensity;
            if (intensity > peak)
                peak = intensity;
        }

        // Raw targets: the renderer owns ALL smoothing (frame-rate independent
        // attack/release), so the pipeline never stacks release times.
        for (int i = 0; i < BarCount; i++)
            _targets[i] = bars[i];

        return new VisualizerFrame(bars, peak, peak > 0.01f, timestamp ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Last computed raw targets (no smoothing applied).</summary>
    public ReadOnlySpan<float> Targets => _targets;

    public void Reset() => Array.Clear(_targets);

    /// <summary>
    /// Rebuilds the per-bar FFT bin ranges, boosts and dB range only when the
    /// inputs change. The per-FFT path then performs no Math.Pow calls.
    /// </summary>
    private void EnsureBandTable()
    {
        int bars = BarCount;
        int sens = AudioSensitivity;
        int peak = AudioPeakLevel;

        if (_bandTable.Length == bars
            && _bandKeyBars == bars
            && _bandKeySens == sens
            && _bandKeyPeak == peak)
            return;

        double frequencyPerBin = 48000.0 / FftSize; // nominal; boost shape is rate-independent
        double ratio = MaxFreq / MinFreq;

        var table = new BandRange[bars];
        for (int i = 0; i < bars; i++)
        {
            double startFreq = MinFreq * Math.Pow(ratio, (double)i / bars);
            double endFreq = MinFreq * Math.Pow(ratio, (double)(i + 1) / bars);

            int startBin = (int)(startFreq / frequencyPerBin);
            int endBin = (int)(endFreq / frequencyPerBin);

            if (endBin <= startBin) endBin = startBin + 1;
            if (endBin >= FftSize / 2) endBin = FftSize / 2 - 1;
            if (startBin < 0) startBin = 0;

            float progress = bars > 0 ? (float)i / bars : 0f;
            table[i] = new BandRange
            {
                StartBin = startBin,
                EndBin = endBin,
                Boost = 1.0f + (progress * 75.0f),
            };
        }

        _bandTable = table;
        _bandKeyBars = bars;
        _bandKeySens = sens;
        _bandKeyPeak = peak;
        _bandMinDb = (sens * -10f) - 30f;
        _bandMaxDb = (peak * 10f) - 30f;
    }

    private static void Transform(Span<double> real, Span<double> imaginary)
    {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var wLengthReal = Math.Cos(angle);
            var wLengthImaginary = Math.Sin(angle);
            for (var offset = 0; offset < n; offset += length)
            {
                var wReal = 1d;
                var wImaginary = 0d;
                var half = length / 2;
                for (var i = 0; i < half; i++)
                {
                    var even = offset + i;
                    var odd = even + half;
                    var oddReal = real[odd] * wReal - imaginary[odd] * wImaginary;
                    var oddImaginary = real[odd] * wImaginary + imaginary[odd] * wReal;
                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;

                    (wReal, wImaginary) = (
                        wReal * wLengthReal - wImaginary * wLengthImaginary,
                        wImaginary * wLengthReal + wReal * wLengthImaginary);
                }
            }
        }
    }
}
