using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Controls;

internal static partial class IslandChecks
{
    private static void CheckPanelRestoreOrder()
    {
        var parent = new StackPanel();
        var column = new StackPanel();
        var original = Enumerable.Range(0, 5).Select(_ => new Border()).ToArray();
        foreach (var panel in original) parent.Children.Add(panel);
        var type = typeof(FluentFlyoutWPF.Windows.IslandWindow);
        var capture = type.GetMethod("CapturePanelHomes", BindingFlags.Static | BindingFlags.NonPublic)!;
        var restore = type.GetMethod("RestorePanelHome", BindingFlags.Static | BindingFlags.NonPublic)!;
        var moved = original.Skip(1).Take(3).Cast<UIElement>().ToArray();
        var homes = (Dictionary<UIElement, (Panel Parent, int Index)>)capture.Invoke(null, [moved])!;
        foreach (var panel in moved.Reverse()) { parent.Children.Remove(panel); column.Children.Add(panel); }
        foreach (var pair in homes.OrderBy(pair => pair.Value.Index)) restore.Invoke(null, [pair.Key, pair.Value.Parent, pair.Value.Index]);
        Check(parent.Children.Cast<UIElement>().SequenceEqual(original), "Combined screen restoration reordered the original panels");
    }

    private static void CheckTimerEditor()
    {
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load("FluentFlyoutWPF/Windows/IslandWindow.xaml");
        var source = new XElement(document.Descendants(w + "Grid").Single(element => (string?)element.Attribute(x + "Name") == "TimerConfigGrid"));
        foreach (var attribute in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            source.SetAttributeValue(attribute.Name, attribute.Value);
        string[] events = ["Click", "SelectionChanged", "MouseWheel", "PreviewMouseWheel", "PreviewMouseLeftButtonDown", "PreviewMouseMove", "PreviewMouseLeftButtonUp", "LostMouseCapture", "PreviewKeyDown", "LostKeyboardFocus", "TextChanged", "SizeChanged"];
        foreach (var attribute in source.DescendantsAndSelf().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)).ToArray()) attribute.Remove();
        foreach (var attribute in source.DescendantsAndSelf().Attributes("Style").Where(attribute => attribute.Value.Contains("MicaWPF")).ToArray()) attribute.Remove();
        var timerStyle = new XElement(document.Descendants(w + "Style").Single(element => (string?)element.Attribute(x + "Key") == "IslandTimerInput"));
        source.AddFirst(new XElement(w + "Grid.Resources", timerStyle));
        foreach (var button in source.Descendants().Where(element => element.Name.LocalName == "Button")) button.Name = w + "Button";
        var grid = (Grid)XamlReader.Parse(source.ToString());
        var seconds = (System.Windows.Controls.TextBox)grid.FindName("ReelS");
        var presets = (ListBox)grid.FindName("TimerPresetList");
        var picker = (ComboBox)grid.FindName("TimerPresetPicker");
        grid.Resources["IslandTimerHoursShort"] = "h";
        grid.Resources["IslandTimerMinutesShort"] = "min";
        grid.Resources["IslandTimerSecondsShort"] = "s";
        presets.ItemsSource = new[] { new FluentFlyoutWPF.Models.TimerPreset { Name = "25 minutes", DurationSeconds = 1500 },
            new FluentFlyoutWPF.Models.TimerPreset { Name = "Very long preset name that must not cover seconds", DurationSeconds = 30 } };
        var layout = typeof(FluentFlyoutWPF.Windows.IslandWindow).GetMethod("ConfigureTimerEditor", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (string value in new[] { "01", "30", "45", "59" })
        for (int cycle = 0; cycle < 100; cycle++)
        {
            double width = new[] { 200.0, 208, 236, 288, 320, 359, 360, 520, 600 }[cycle % 9];
            seconds.Text = value;
            layout.Invoke(null, [grid, presets, width]);
            grid.Measure(new Size(width, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
            var secondsBounds = seconds.TransformToAncestor(grid).TransformBounds(new Rect(seconds.RenderSize));
            FrameworkElement presetControl = width >= 360 ? presets : picker;
            var presetBounds = presetControl.TransformToAncestor(grid).TransformBounds(new Rect(presetControl.RenderSize));
            Check(seconds.ActualWidth > 0 && secondsBounds.Left >= 0 && secondsBounds.Right <= width + 0.5,
                "The seconds field was clipped by the timer editor");
            Check(!secondsBounds.IntersectsWith(presetBounds), "Presets covered the seconds hit target");
            Check(seconds.Text == value, "Relayout discarded the seconds value");
            Check(grid.DesiredSize.Height <= 102.5, "Timer presets inflated the common screen height");
            seconds.ApplyTemplate();
            var inputViewport = (ScrollViewer)seconds.Template.FindName("PART_ContentHost", seconds);
            Check(inputViewport.ComputedHorizontalScrollBarVisibility == Visibility.Collapsed
                && inputViewport.ComputedVerticalScrollBarVisibility == Visibility.Collapsed,
                "A timer input displayed its own scrollbars");
        }
    }

    private static void CheckExpandedContentLayout()
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var shortPanel = new Border { Height = 80 };
        var tallPanel = new Border { Height = 300, Visibility = Visibility.Collapsed };
        content.Children.Add(shortPanel);
        content.Children.Add(tallPanel);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load("FluentFlyoutWPF/Windows/IslandWindow.xaml");
        var viewportSource = new XElement(document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ExpandedViewport"));
        foreach (var attribute in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            viewportSource.SetAttributeValue(attribute.Name, attribute.Value);
        viewportSource.RemoveNodes();
        var viewport = (ScrollViewer)XamlReader.Parse(viewportSource.ToString());
        viewport.Content = content;
        viewport.Height = 102;
        foreach (bool tall in new[] { false, true, false })
        {
            shortPanel.Visibility = tall ? Visibility.Collapsed : Visibility.Visible;
            tallPanel.Visibility = tall ? Visibility.Visible : Visibility.Collapsed;
            viewport.Measure(new Size(236, 102));
            viewport.Arrange(new Rect(0, 0, 236, 102));
            viewport.UpdateLayout();
            Check(viewport.ActualHeight == 102, "Content changed the common viewport height");
            if (!tall)
            {
                var bounds = shortPanel.TransformToAncestor(viewport).TransformBounds(new Rect(shortPanel.RenderSize));
                Check(bounds.Top >= 0 && bounds.Bottom <= viewport.ActualHeight,
                    "The viewport clipped the start of its content");
            }
        }
        var outline = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "IslandOutline");
        Check(outline.Parent?.Name.LocalName == "Canvas", "The outline must not affect content measurement or margins");
        var box = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "IslandBox");
        Check((string?)box.Attribute("PreviewMouseWheel") == "IslandBox_Wheel", "ScrollViewer can swallow screen navigation before it reaches the Island");
        var screenHost = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ScreenViewport");
        Check(screenHost.Name.LocalName == "Border", "A horizontal screen ScrollViewer intercepted navigation");
        var rowSource = new XElement(document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == "ScreenExpandedRow"));
        Check(rowSource.Name.LocalName == "StackPanel" && (string?)rowSource.Attribute("Orientation") == "Horizontal",
            "Screen widgets must never wrap onto a second row");
        foreach (var attribute in document.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            rowSource.SetAttributeValue(attribute.Name, attribute.Value);
        rowSource.RemoveNodes();
        rowSource.SetAttributeValue("Visibility", "Visible");
        var fit = typeof(FluentFlyoutWPF.Windows.IslandWindow).GetMethod("FitScreenColumnWidths", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (double available in new[] { 252.0, 400, 552, 800 })
        {
            var row = (StackPanel)XamlReader.Parse(rowSource.ToString());
            var widths = (double[])fit.Invoke(null, [new double[] { 96, 192, 236 }, available, 14.0])!;
            for (int i = 0; i < widths.Length; i++)
                row.Children.Add(new Border { Width = widths[i], Height = 100, Margin = new Thickness(0, 0, i < widths.Length - 1 ? 14 : 0, 0) });
            row.Measure(new Size(available, double.PositiveInfinity));
            row.Arrange(new Rect(0, 0, available, row.DesiredSize.Height));
            double right = 0;
            foreach (FrameworkElement child in row.Children)
            {
                var bounds = child.TransformToAncestor(row).TransformBounds(new Rect(child.RenderSize));
                Check(bounds.Width > 0 && Math.Abs(bounds.Top) < 0.01 && bounds.Left >= right - 0.01 && bounds.Right <= available + 0.01,
                    "A screen widget wrapped, overlapped or exceeded the horizontal row");
                right = bounds.Right;
            }
            Check(row.DesiredSize.Height == 100, "Screen composition created a second row");
        }
    }

    private static void CheckTimerClockAndInput()
    {
        foreach (var (text, total) in new[] { ("1", 1), ("00:00:01", 1), ("00:00:30", 30), ("01:01:45", 3705), ("24:00:00", 86400) })
            Check(IslandTimer.TryParseDuration(text, out var value) && value.TotalSeconds == total, "Valid timer input was rejected");
        foreach (string text in new[] { "0", "00:00:00", "24:00:01", "25:00:00", "1:60", "9999999999999999999", "99999999999999999:00:00" })
            Check(!IslandTimer.TryParseDuration(text, out _), "Invalid or overflowing timer input was accepted");
        long elapsed = 0;
        var timer = new IslandTimer(() => elapsed);
        int alarms = 0;
        timer.Finished += () => alarms++;
        Check(timer.Start(TimeSpan.FromSeconds(30), "Test"), "Timer failed to start");
        elapsed = 10_000;
        timer.Poll(DateTime.MaxValue);
        Check(timer.Remaining == TimeSpan.FromSeconds(20) && alarms == 0, "Wall clock change expired the timer");
        timer.Pause();
        elapsed += 3_600_000;
        Check(timer.Remaining == TimeSpan.FromSeconds(20), "Paused time changed during sleep");
        timer.Resume();
        elapsed += 30_000;
        timer.Poll(DateTime.MinValue);
        timer.Poll(DateTime.MaxValue);
        Check(alarms == 1 && timer.State == IslandTimerState.Alerting, "Resume did not deliver exactly one elapsed alarm");
    }

    private static void CheckClipboardFidelity()
    {
        static BitmapSource Pixel(byte red, byte blue)
        {
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { blue, 0, red, 255 }, 4);
            image.Freeze();
            return image;
        }
        var hash = typeof(IslandClipboardService).GetMethod("ImageHash", BindingFlags.Static | BindingFlags.NonPublic)!;
        var red = Pixel(255, 0);
        var blue = Pixel(0, 255);
        var first = new IslandClipboardItem { Kind = IslandClipboardKind.Image, Image = red, ContentHash = (string)hash.Invoke(null, [red])! };
        var different = new IslandClipboardItem { Kind = IslandClipboardKind.Image, Image = blue, ContentHash = (string)hash.Invoke(null, [blue])! };
        var same = new IslandClipboardItem { Kind = IslandClipboardKind.Image, Image = Pixel(255, 0), ContentHash = first.ContentHash };
        Check(!first.SameAs(different) && first.SameAs(same), "Clipboard compared images by dimensions rather than pixels");
        string original = new('A', 30_000);
        var text = new IslandClipboardItem { Kind = IslandClipboardKind.Text, Text = original };
        Check(text.Text == original && text.Caption.Length < 150, "Clipboard preview truncated the saved original");
        using var history = new IslandClipboardService(_ => { }, () => 1, _ => Task.CompletedTask);
        typeof(IslandClipboardService).GetProperty("MemoryBudgetBytes", Private)!.SetValue(history, 1024L);
        var add = typeof(IslandClipboardService).GetMethod("Add", Private)!;
        for (int index = 0; index < 5; index++)
            add.Invoke(history, [new IslandClipboardItem { Kind = IslandClipboardKind.Text, Text = index + new string('x', 200) }]);
        Check(history.Items.Count == 2 && history.Items[0].Text.StartsWith('4'), "Clipboard memory budget did not evict the oldest originals");
        add.Invoke(history, [text]);
        Check(history.Items.Count == 2 && history.Error.Length > 0, "An oversized item was silently truncated or evicted valid history");
        int writes = 0;
        using var busy = new IslandClipboardService(_ => { writes++; throw new COMException(); }, () => 1, _ => Task.CompletedTask);
        Check(!busy.CopyAsync(text).GetAwaiter().GetResult() && writes == 3 && busy.Error.Length > 0,
            "Clipboard write failure was reported as a successful copy");
        writes = 0;
        using var ready = new IslandClipboardService(_ => writes++, () => 1, _ => Task.CompletedTask);
        Check(ready.CopyAsync(text).GetAwaiter().GetResult() && writes == 1, "Successful clipboard copy was not confirmed");
    }

    private static void CheckShelfRecoveryIntent()
    {
        string root = Path.Combine(Path.GetTempPath(), "FluentIslandChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string originals = Path.Combine(root, "originals"), shelf = Path.Combine(root, "shelf");
            Directory.CreateDirectory(originals);
            string original = Path.Combine(originals, "example.txt");
            File.WriteAllText(original, "preserve this content");
            var transfer = typeof(IslandTimer).Assembly.GetType("FluentFlyoutWPF.Classes.IslandShelfTransfer")!;
            var move = transfer.GetMethod("Move", BindingFlags.Static | BindingFlags.NonPublic)!;
            move.Invoke(null, [original, shelf, original, false, shelf]);
            string parked = Path.Combine(shelf, "example.txt");
            Check(!File.Exists(original) && File.ReadAllText(parked) == "preserve this content", "Shelf park lost its original content");
            Check(Directory.GetFiles(Path.Combine(shelf, ".operations"), "*.json").Length == 1, "Move completed without a recovery intent");
            File.WriteAllText(original, "do not overwrite me");
            move.Invoke(null, [parked, originals, original, true, shelf]);
            Check(File.ReadAllText(original) == "do not overwrite me" && File.ReadAllText(Path.Combine(originals, "example (2).txt")) == "preserve this content",
                "Shelf return overwrote a conflicting original");
            move.Invoke(null, [originals, Path.Combine(originals, "child"), originals, false, shelf]);
            Check(Directory.Exists(originals) && File.Exists(original), "Shelf accepted a directory moved inside itself");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("FluentIslandChecks-", StringComparison.Ordinal))
                Directory.Delete(resolved, true);
        }
    }

    private static void CheckRoundedActionSurface()
    {
        var surface = new IslandActionSurface
        {
            Width = 50, Height = 50, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.White, Child = new Grid { Background = Brushes.Red },
        };
        Geometry? initialClip = null;
        for (int cycle = 0; cycle < 100; cycle++)
        {
            surface.Background = cycle % 2 == 0 ? Brushes.Black : Brushes.White;
            surface.Measure(new Size(50, 50));
            surface.Arrange(new Rect(0, 0, 50, 50));
            Check(surface.BorderThickness == new Thickness(1) && surface.Child.Clip is RectangleGeometry { RadiusX: 11 },
                "Changing the background removed the tile outline or rounded content clip");
            initialClip ??= surface.Child.Clip;
            Check(ReferenceEquals(initialClip, surface.Child.Clip) && surface.Child.Clip.IsFrozen,
                "An unchanged tile rebuilt its clip during layout");
        }
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(surface)!;
        Check(peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) != null, "Action surface is inaccessible to automation");
    }
}
