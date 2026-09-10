namespace FluentFlyout.Core;

public static class DurationFormatting
{
    public static string Format(TimeSpan duration)
    {
        var clamped = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;

        return clamped.TotalHours >= 1
            ? clamped.ToString(@"hh\:mm\:ss")
            : clamped.ToString(@"mm\:ss");
    }

    public static string FormatMilliseconds(long milliseconds)
    {
        return Format(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)));
    }
}
