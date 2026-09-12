using FluentFlyout.Core;
using Xunit;

namespace FluentFlyout.Tests;

public sealed class TaskbarWidgetMathTests
{
    [Fact]
    public void ComputeLogicalWidth_AutoWidth_FitsTextPlusReserved()
    {
        // text 100 + cover 55 + extra 6 = 161, under the 240 cap
        double width = TaskbarWidgetMath.ComputeLogicalWidth(100, 80, showAlbumArt: true, controlsEnabled: false, fixedWidth: false, fixedWidthPx: 216);
        Assert.Equal(100 + TaskbarWidgetMath.CoverImageMargin + TaskbarWidgetMath.ExtraMarginForText, width, 2);
    }

    [Fact]
    public void ComputeLogicalWidth_NoCover_ReservesOnlySmallMargin()
    {
        double width = TaskbarWidgetMath.ComputeLogicalWidth(100, 80, showAlbumArt: false, controlsEnabled: false, fixedWidth: false, fixedWidthPx: 216);
        Assert.Equal(100 + TaskbarWidgetMath.NoCoverReservedMargin + TaskbarWidgetMath.ExtraMarginForText, width, 2);
    }

    [Fact]
    public void ComputeLogicalWidth_ControlsEnabled_AddsBlock()
    {
        double without = TaskbarWidgetMath.ComputeLogicalWidth(100, 80, true, controlsEnabled: false, false, 216);
        double with = TaskbarWidgetMath.ComputeLogicalWidth(100, 80, true, controlsEnabled: true, false, 216);
        Assert.Equal(without + TaskbarWidgetMath.ControlsBlockWidth, with, 2);
    }

    [Fact]
    public void ComputeLogicalWidth_CappedAtMaxWidth()
    {
        double width = TaskbarWidgetMath.ComputeLogicalWidth(10_000, 0, true, false, false, 216);
        Assert.Equal(TaskbarWidgetMath.MaxLogicalWidth, width, 2);
    }

    [Fact]
    public void ComputeLogicalWidth_FixedWidth_PinsToConfiguredWidth()
    {
        double width = TaskbarWidgetMath.ComputeLogicalWidth(10, 10, true, false, fixedWidth: true, fixedWidthPx: 180);
        Assert.Equal(180 / TaskbarWidgetMath.Scale, width, 2);
    }

    [Fact]
    public void TextRowWidth_SubtractsCoverReservation()
    {
        Assert.Equal(106, TaskbarWidgetMath.TextRowWidth(161, showAlbumArt: true), 2);
        Assert.Equal(149, TaskbarWidgetMath.TextRowWidth(161, showAlbumArt: false), 2);
    }

    [Fact]
    public void PingPongTimeline_HasTwoSecondPausesAndCappedFade()
    {
        var t = TaskbarWidgetMath.ComputePingPongTimeline(scrollDistance: 40, speedPixelsPerSecond: 20);
        Assert.Equal(2.0, t.WaitStart, 3);
        Assert.Equal(4.0, t.ScrollEnd, 3);  // 2 + 40/20
        Assert.Equal(6.0, t.WaitEnd, 3);
        Assert.Equal(8.0, t.ScrollBackEnd, 3);
        Assert.Equal(10.0, t.TotalCycle, 3);
        Assert.Equal(300, t.FadeMs, 0); // capped at 300ms
    }

    [Fact]
    public void PingPongTimeline_ShortScroll_CapsFadeAtHalfScroll()
    {
        // 10px at 20px/s = 0.5s scroll => fade capped at 250ms (half), not 300ms.
        var t = TaskbarWidgetMath.ComputePingPongTimeline(10, 20);
        Assert.Equal(250, t.FadeMs, 1);
    }

    [Fact]
    public void LoopScrollDistance_AddsSpacer()
    {
        Assert.Equal(140, TaskbarWidgetMath.ComputeLoopScrollDistance(100, 40), 2);
        Assert.Equal(100, TaskbarWidgetMath.ComputeLoopScrollDistance(100, 0), 2);
    }
}
