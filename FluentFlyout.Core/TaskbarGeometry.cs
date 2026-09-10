namespace FluentFlyout.Core;

public readonly record struct TaskbarRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

public readonly record struct TaskbarPlacement(int Left, int Top, int Width, int Height);

public static class TaskbarGeometry
{
    public static TaskbarPlacement PlaceWidget(
        TaskbarRect taskbar,
        int logicalWidth,
        int logicalHeight,
        int dpi,
        bool vertical,
        int position,
        int padding = 0)
    {
        var scale = Math.Max(1d, dpi) / 96d;
        var width = Math.Max(1, (int)Math.Round(logicalWidth * scale));
        var height = Math.Max(1, (int)Math.Round(logicalHeight * scale));
        var inset = (int)Math.Round(Math.Max(0, padding) * scale);
        position = Math.Clamp(position, 0, 2);

        if (vertical)
        {
            var top = position switch
            {
                0 => taskbar.Top + inset,
                1 => taskbar.Top + Math.Max(0, (taskbar.Height - height) / 2),
                _ => taskbar.Bottom - height - inset,
            };
            return new TaskbarPlacement(
                taskbar.Left + Math.Max(0, (taskbar.Width - width) / 2),
                top,
                width,
                height);
        }

        var left = position switch
        {
            0 => taskbar.Left + inset,
            1 => taskbar.Left + Math.Max(0, (taskbar.Width - width) / 2),
            _ => taskbar.Right - width - inset,
        };
        return new TaskbarPlacement(
            left,
            taskbar.Top + Math.Max(0, (taskbar.Height - height) / 2),
            width,
            height);
    }
}
