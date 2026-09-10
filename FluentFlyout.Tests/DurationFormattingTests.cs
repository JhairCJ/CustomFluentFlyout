using FluentFlyout.Core;
using Xunit;

namespace FluentFlyout.Tests;

public sealed class DurationFormattingTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(65_000, "01:05")]
    public void Format_UsesMinuteSecondLayout_ForSubHourDurations(long milliseconds, string expected)
    {
        var formatted = DurationFormatting.FormatMilliseconds(milliseconds);

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void Format_UsesHourMinuteSecondLayout_ForLongDurations()
    {
        var formatted = DurationFormatting.Format(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3));

        Assert.Equal("01:02:03", formatted);
    }

    [Fact]
    public void Format_ClampsNegativeDurations_ToZero()
    {
        var formatted = DurationFormatting.Format(TimeSpan.FromSeconds(-5));

        Assert.Equal("00:00", formatted);
    }
}
