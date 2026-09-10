using FluentFlyout.Core;
using Xunit;

namespace FluentFlyout.Tests;

public sealed class SpectrumAnalyzerTests
{
    [Fact]
    public void Analyze_Silence_IsGatedAndResetsBars()
    {
        var analyzer = new SpectrumAnalyzer(8);

        var frame = analyzer.Analyze(new float[64], DateTimeOffset.UnixEpoch);

        Assert.False(frame.HasSignal);
        Assert.Equal(0, frame.Peak);
        Assert.All(frame.Bars, value => Assert.Equal(0, value));
        Assert.Equal(DateTimeOffset.UnixEpoch, frame.Timestamp);
    }

    [Fact]
    public void Analyze_ProducesStableBarCount_AndBoundedIntensities()
    {
        var analyzer = new SpectrumAnalyzer(8) { GateThreshold = 0 };
        var samples = Enumerable.Range(0, 64)
            .Select(i => (float)Math.Sin(2 * Math.PI * i / 8))
            .ToArray();

        var frame = analyzer.Analyze(samples);

        Assert.Equal(8, frame.Bars.Count);
        Assert.InRange(frame.Peak, 0, 1);
        Assert.All(frame.Bars, value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void Analyze_LoudTone_ProducesHigherIntensityThanQuietTone()
    {
        var analyzer = new SpectrumAnalyzer(10) { GateThreshold = 0, AudioSensitivity = 0, AudioPeakLevel = 10 };

        var loud = Enumerable.Range(0, 4096)
            .Select(i => 0.8f * (float)Math.Sin(2 * Math.PI * 220 * i / 48000.0))
            .ToArray();
        var quiet = loud.Select(v => v / 500f).ToArray();

        var loudFrame = analyzer.Analyze(loud);
        var quietFrame = analyzer.Analyze(quiet);

        Assert.True(loudFrame.Peak > quietFrame.Peak);
    }

    [Fact]
    public void Analyze_HighFrequencyTone_EnergizesHighBars()
    {
        var analyzer = new SpectrumAnalyzer(10) { GateThreshold = 0 };

        // 6 kHz tone: high-frequency content should light the upper (boosted) bars.
        var tone = Enumerable.Range(0, 4096)
            .Select(i => 0.8f * (float)Math.Sin(2 * Math.PI * 6000 * i / 48000.0))
            .ToArray();

        var frame = analyzer.Analyze(tone);

        float upperHalf = frame.Bars.Skip(5).Max();
        Assert.True(upperHalf > 0.05f);
    }
}
