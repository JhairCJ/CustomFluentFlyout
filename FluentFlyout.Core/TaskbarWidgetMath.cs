namespace FluentFlyout.Core;

/// <summary>
/// Pure math for the taskbar widget layout, ported from the WPF
/// <c>TaskbarWidgetControl</c> width pipeline so the WinUI port and the tests
/// share one implementation.
/// </summary>
public static class TaskbarWidgetMath
{
    /// <summary>Widget scale used by the WPF host; logical widths are divided by it.</summary>
    public const double Scale = 0.9;

    /// <summary>Maximum logical width, matching the Windows native widget.</summary>
    public const double MaxLogicalWidth = 216 / Scale;

    /// <summary>Distance between the album art edge and the text.</summary>
    public const double CoverImageMargin = 55;

    /// <summary>Left margins that stay on screen when the album art is hidden.</summary>
    public const double NoCoverReservedMargin = 12;

    /// <summary>Extra margin to avoid text clipping.</summary>
    public const double ExtraMarginForText = 6;

    /// <summary>Space added for the playback controls block when visible.</summary>
    public const double ControlsBlockWidth = 104;

    /// <summary>
    /// Computes the logical widget width from measured text widths and settings.
    /// Mirrors the WPF <c>ComputeTextLogicalWidth</c> + controls addendum.
    /// </summary>
    /// <param name="titleWidth">Measured title text width (DIPs).</param>
    /// <param name="artistWidth">Measured artist text width (DIPs).</param>
    /// <param name="showAlbumArt">Whether the album art is shown.</param>
    /// <param name="controlsEnabled">Whether the playback controls are visible.</param>
    /// <param name="fixedWidth">Whether the fixed-width mode is on.</param>
    /// <param name="fixedWidthPx">User-configured fixed width in physical pixels.</param>
    public static double ComputeLogicalWidth(
        double titleWidth,
        double artistWidth,
        bool showAlbumArt,
        bool controlsEnabled,
        bool fixedWidth,
        int fixedWidthPx)
    {
        double coverReserved = showAlbumArt ? CoverImageMargin : NoCoverReservedMargin;

        double logicalWidth;
        if (fixedWidth)
        {
            logicalWidth = fixedWidthPx / Scale;
        }
        else
        {
            logicalWidth = Math.Max(titleWidth, artistWidth) + coverReserved + ExtraMarginForText;
        }

        logicalWidth = Math.Min(logicalWidth, MaxLogicalWidth);

        if (controlsEnabled)
            logicalWidth += ControlsBlockWidth;

        return logicalWidth;
    }

    /// <summary>Width available for one text row, given the current settings.</summary>
    public static double TextRowWidth(double logicalWidth, bool showAlbumArt)
    {
        double coverReserved = showAlbumArt ? CoverImageMargin : NoCoverReservedMargin;
        return Math.Max(logicalWidth - coverReserved, 0);
    }

    /// <summary>
    /// Ping-pong marquee timeline (non-loop-forever mode). Reproduces the WPF keyframe
    /// timings: wait at start, scroll left, wait, scroll back, wait.
    /// </summary>
    /// <param name="scrollDistance">Text overflow distance in DIPs.</param>
    /// <param name="speedPixelsPerSecond">User-configured scroll speed.</param>
    public static MarqueeTimeline ComputePingPongTimeline(double scrollDistance, int speedPixelsPerSecond)
    {
        double speed = Math.Max(speedPixelsPerSecond, 1);
        double durationSeconds = scrollDistance / speed;
        const double pause = 2.0;
        double tWaitStart = pause;
        double tScrollEnd = tWaitStart + durationSeconds;
        double tWaitEnd = tScrollEnd + pause;
        double tScrollBackEnd = tWaitEnd + durationSeconds;
        double tTotalCycle = tScrollBackEnd + pause;
        // Fade time capped at 300ms and at half the scroll duration.
        double fadeMs = Math.Min(300, durationSeconds * 1000 / 2.0);
        return new MarqueeTimeline(tWaitStart, tScrollEnd, tWaitEnd, tScrollBackEnd, tTotalCycle, fadeMs);
    }

    /// <summary>Continuous loop (loop-forever mode): scroll distance with the spacer.</summary>
    public static double ComputeLoopScrollDistance(double textWidth, double spacerWidth) =>
        textWidth + Math.Max(spacerWidth, 0);

    public readonly record struct MarqueeTimeline(
        double WaitStart,
        double ScrollEnd,
        double WaitEnd,
        double ScrollBackEnd,
        double TotalCycle,
        double FadeMs);
}
