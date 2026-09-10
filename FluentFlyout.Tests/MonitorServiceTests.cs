using FluentFlyout.Platform.Windows;
using Xunit;

namespace FluentFlyout.Tests;

public class MonitorServiceTests
{
    [Fact]
    public void GetMonitors_ReturnsAtLeastOneMonitor()
    {
        var service = new MonitorService();
        var monitors = service.GetMonitors();

        Assert.NotEmpty(monitors);
        Assert.True(monitors[0].WorkArea.Width > 0);
        Assert.True(monitors[0].WorkArea.Height > 0);
    }

    [Theory]
    [InlineData(0)] // BottomLeft
    [InlineData(1)] // BottomCenter
    [InlineData(2)] // BottomRight
    [InlineData(3)] // TopLeft
    [InlineData(4)] // TopCenter
    [InlineData(5)] // TopRight
    public void CalculatePosition_ReturnsCoordinatesWithinMonitor(int position)
    {
        var service = new MonitorService();
        var monitor = service.GetSelectedMonitor(0);
        var (x, y) = service.CalculatePosition(0, position, 300, 100, 20);

        Assert.True(x >= monitor.WorkArea.Left);
        Assert.True(x + 300 <= monitor.WorkArea.Right + 10);
        Assert.True(y >= monitor.WorkArea.Top);
        Assert.True(y + 100 <= monitor.WorkArea.Bottom + 10);
    }
}
