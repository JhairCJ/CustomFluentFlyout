using FluentFlyout.Core;
using Xunit;

namespace FluentFlyout.Tests;

public sealed class TaskbarGeometryTests
{
    [Fact]
    public void PlaceWidget_HorizontalCenter_UsesDpiScaledSize()
    {
        var placement = TaskbarGeometry.PlaceWidget(
            new TaskbarRect(0, 1000, 1920, 1040),
            logicalWidth: 200,
            logicalHeight: 32,
            dpi: 144,
            vertical: false,
            position: 1);

        Assert.Equal(300, placement.Width);
        Assert.Equal(48, placement.Height);
        Assert.Equal(810, placement.Left);
        Assert.Equal(1000, placement.Top);
    }

    [Fact]
    public void PlaceWidget_VerticalRight_ClampsPositionAndPadding()
    {
        var placement = TaskbarGeometry.PlaceWidget(
            new TaskbarRect(0, 0, 48, 1080),
            logicalWidth: 160,
            logicalHeight: 32,
            dpi: 96,
            vertical: true,
            position: 9,
            padding: 8);

        Assert.Equal(0, placement.Left);
        Assert.Equal(1040, placement.Top);
        Assert.Equal(160, placement.Width);
        Assert.Equal(32, placement.Height);
    }
}
