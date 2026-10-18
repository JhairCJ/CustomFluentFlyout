using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Controls;
using FluentFlyout.Windows;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.ViewModels;

internal static class TaskbarHostChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Read(object target, string field) => target.GetType().GetField(field, Private)!.GetValue(target);
    private static void Write(object target, string field, object? value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    internal static void Run()
    {
        BitmapImage Cover(byte red)
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 40, 70, red, 255 }, 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new System.IO.MemoryStream(); encoder.Save(stream); stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        Run(Cover(200), Cover(30));
    }
    private static void Run(BitmapImage a, BitmapImage b)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new MicaWPF.Styles.ThemeDictionary());
        app.Resources.MergedDictionaries.Add(new MicaWPF.Styles.ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/FluentFlyout;component/Resources/Localization/Dictionary-en-US.xaml") });
        app.Resources["InstantContextMenu"] = new Style(typeof(ContextMenu));
        app.Resources["PercentageToOpacityConverter"] = new FluentFlyoutWPF.Classes.Converters.PercentageToOpacityConverter();
        app.Resources["BoolToVisibleCollapsedConverter"] = new FluentFlyoutWPF.Classes.Converters.BoolToVisibleCollapsedConverter();
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
        MicaWPF.Core.Services.MicaWPFServiceUtility.ThemeService.ChangeTheme(MicaWPF.Core.Enums.WindowsTheme.Dark);
        SettingsManager.Current = new UserSettings
        {
            TaskbarWidgetEnabled = true, TaskbarWidgetAnimated = true, TaskbarWidgetExpandOnClick = true,
            TaskbarWidgetAutoHide = false, TaskbarWidgetSongChangeAnimation = 1,
            TaskbarWidgetScrollingEnabled = true, TaskbarWidgetScrollingTextLoopForever = true,
            TaskbarWidgetBackgroundBlur = true, TaskbarWidgetBackgroundRotate = true,
            TaskbarWidgetManualPadding = 500,
        };
        var focus = NativeMethods.GetForegroundWindow();
        var window = new TaskbarWindow();
        try
        {
            Pump(100);
            var widget = (TaskbarWidgetControl)window.FindName("Widget");
            var compact = (Canvas)window.FindName("WidgetCanvas");
            var expanded = (TaskbarWidgetExpandedContent)widget.FindName("ExpandedContent");
            int originalTitleSize = SettingsManager.Current.TaskbarWidgetTitleFontSize;
            int originalArtistSize = SettingsManager.Current.TaskbarWidgetArtistFontSize;
            foreach (var sizes in new[] { (13, 12), (16, 10), (12, 16) })
            {
                SettingsManager.Current.TaskbarWidgetTitleFontSize = sizes.Item1;
                SettingsManager.Current.TaskbarWidgetArtistFontSize = sizes.Item2;
                widget.ApplyTextStyle();
                var title = (System.Windows.Controls.TextBlock)widget.FindName("SongTitle");
                var artist = (System.Windows.Controls.TextBlock)widget.FindName("SongArtist");
                var expandedTitle = (System.Windows.Controls.TextBlock)expanded.FindName("TitleText");
                var expandedArtist = (System.Windows.Controls.TextBlock)expanded.FindName("ArtistText");
                Check(expandedTitle.FontSize > title.FontSize && expandedArtist.FontSize > artist.FontSize
                    && Math.Abs(expandedTitle.FontSize / title.FontSize - expandedArtist.FontSize / artist.FontSize) < 0.001,
                    "Expansion must enlarge both text rows proportionally, including an artist larger than the title");
            }
            SettingsManager.Current.TaskbarWidgetTitleFontSize = originalTitleSize;
            SettingsManager.Current.TaskbarWidgetArtistFontSize = originalArtistSize;
            widget.ApplyTextStyle();
            var handle = new WindowInteropHelper(window).Handle;
            Check(NativeMethods.GetParent(handle) != IntPtr.Zero
                && (NativeMethods.GetWindowLong(handle, -16) & 0x40000000) != 0,
                "Compact host is not attached as an Explorer child");
            var source = new object();
            var initial = new MediaArtworkSnapshot(source, "First title", "Artist", a);
            Call(widget, "CommitPendingSong", initial);
            Write(window, "_hasPublishedMedia", true);
            window.Show();
            Call(window, "UpdatePosition", true); Pump(100);
            Write(window, "_hasEverBeenPositioned", false);
            Call(window, "CalculateAndSetPosition", NativeMethods.GetParent(handle), handle, true);
            widget.UpdateLayout();
            var publication = new MediaArtworkPublication(Dispatcher.CurrentDispatcher,
                _ => Task.FromResult<MediaArtworkSnapshot?>(initial with { Title = "Pending title", Artwork = b }),
                song => Call(widget, "CommitPendingSong", song));
            Write(widget, "_artworkPublication", publication);
            publication.Observe(initial);
            publication.Observe(initial with { Title = "Pending title", Artwork = null });
            int version = widget.SongCommitVersion;
            window.ToggleWidgetExpansion();
            var handoff = Read(window, "_handoffImage") as Image;
            Check(handoff?.Source is BitmapSource, "Opening must preserve the outgoing frame during transfer");
            Check(((TaskbarExpansionWindow)Read(window, "_expansionHost")!).Opacity == 0
                && widget.ExpansionProgress == 0,
                "A reused popup must be prepared before revealing or advancing its first frame");
            var handoffBitmap = (BitmapSource)handoff!.Source;
            var handoffPixels = new byte[handoffBitmap.PixelWidth * handoffBitmap.PixelHeight * 4];
            handoffBitmap.CopyPixels(handoffPixels, handoffBitmap.PixelWidth * 4, 0);
            Check(Enumerable.Range(0, handoffPixels.Length / 4).Any(i => handoffPixels[i * 4 + 3] > 0),
                "The outgoing transfer frame is blank");
            var firstFrame = (EventHandler)Read(window, "_handoffRendering")!;
            var startMotion = (Action)Read(window, "_pendingExpansionAnimation")!;
            bool motionStarted = false;
            Write(window, "_pendingExpansionAnimation", (Action)(() => { motionStarted = true; startMotion(); }));
            firstFrame(window, EventArgs.Empty);
            Check(motionStarted && Read(window, "_pendingExpansionAnimation") == null
                && ((TaskbarExpansionWindow)Read(window, "_expansionHost")!).Opacity == 1,
                "Expansion still waits for a second frame or a background queue before starting");
            Check(ReferenceEquals(Read(window, "_handoffImage"), handoff),
                "Starting motion discarded the transfer snapshot before the receiving host presented");
            Pump(30);
            var host = (TaskbarExpansionWindow?)Read(window, "_expansionHost");
            Check(host != null && host.Surface.Children.Contains(widget) && !compact.Children.Contains(widget),
                "Expansion duplicated/lost the interactive widget instance");
            Check(ReferenceEquals(Read(widget, "_artworkPublication"), publication), "Transfer cleaned pending publication");
            Check(NativeMethods.GetParent(new WindowInteropHelper(host!).Handle) == IntPtr.Zero,
                "Expanded host is clipped by Explorer");
            Check(NativeMethods.GetForegroundWindow() == focus, "Expansion stole foreground focus");
            var union = (Rect)Read(window, "_expansionFlightRect")!;
            Check(union.Contains((Rect)Read(window, "_expansionTargetRect")!)
                && union.Contains((Rect)Read(window, "_expandedAnchorRect")!), "Expansion clipped its flight union");
            CheckMorphContinuity(window, widget, expanded);
            window.CloseWidgetExpansion();
            CheckMorphContinuity(window, widget, expanded);
            Pump(80);
            window.ToggleWidgetExpansion();
            Pump(400);
            int popupMovesDuringReturn = 0;
            bool returning = false;
            host!.LocationChanged += (_, _) => { if (returning && host.IsVisible) popupMovesDuringReturn++; };
            window.CloseWidgetExpansion();
            var returnHost = (Rect)Read(window, "_nativeHostScreenRect")!;
            returning = true;
            int returnVersion = (int)Read(window, "_expansionVersion")!;
            window.CloseWidgetExpansion();
            Check((int)Read(window, "_expansionVersion")! == returnVersion,
                "Duplicate outside-close restarted the return animation");
            for (int frame = 0; frame < 100 && (bool)Read(window, "_expansionOwnsLayout")!; frame++)
            {
                Pump(10);
                if (!(bool)Read(window, "_expansionOwnsLayout")!) break;
                Check((Rect)Read(window, "_nativeHostScreenRect")! == returnHost,
                    "Return moved the popup instead of animating inside its flight bounds");
                var live = (Rect)Call(window, "LiveWidgetRect")!;
                var bar = (Rect)Read(window, "_taskbarScreenRect")!;
                var screen = widget.PointToScreen(new Point());
                Check(Math.Abs(screen.X - bar.Left - live.Left) <= 2
                    && Math.Abs(screen.Y - bar.Top - live.Top) <= 2,
                    "Animated widget teleported away from its taskbar anchor");
            }
            returning = false;
            Check(popupMovesDuringReturn == 0, "Closing moved a visible popup at the handoff");
            window.ToggleWidgetExpansion(); Pump(30);
            // A shell/automation positioning pass may already own the geometry gate.
            Write(window, "_positionUpdateInProgress", true);
            window.CloseWidgetExpansion(false); Pump(80);
            Check(compact.Children.Contains(widget) && host!.Surface.Children.Count == 0 && !host.IsVisible,
                "Collapse did not return the same instance to its child host");
            CheckCompactRegion(window, widget, handle);
            Write(window, "_positionUpdateInProgress", false);
            Pump(500);
            Check(widget.SongCommitVersion == version + 1 && (string?)Read(widget, "_actualTitle") == "Pending title",
                "Pending publication was lost during transfer");

            for (int cycle = 0; cycle < 100; cycle++)
            {
                SettingsManager.Current.TaskbarWidgetAnimated = cycle % 4 < 2;
                SettingsManager.Current.TaskbarWidgetSongChangeAnimation = cycle % 2;
                window.ToggleWidgetExpansion(); Pump(4);
                if (cycle % 5 == 0)
                {
                    window.CloseWidgetExpansion(); Pump(4);
                    window.ToggleWidgetExpansion(); Pump(4);
                }
                Call(widget, "CommitPendingSong", initial with
                { Title = cycle % 2 == 0 ? $"Track {cycle}" : $"Track {cycle}: a much longer title during return", Artwork = cycle % 2 == 0 ? a : b });
                Write(window, "_hasEverBeenPositioned", false);
                Call(window, "CalculateAndSetPosition", NativeMethods.GetParent(handle), handle, true);
                Write(window, "_positionUpdateInProgress", true);
                window.CloseWidgetExpansion(cycle % 3 != 0);
                for (int wait = 0; wait < 150 && (bool)Read(window, "_expansionOwnsLayout")!; wait++) Pump(20);
                Check(!(bool)Read(window, "_expansionOwnsLayout")! && compact.Children.Contains(widget),
                    "Cycle settled in an obsolete host");
                CheckCompactRegion(window, widget, handle);
                try { CheckPresented(window, compact, handle, cycle == 99); }
                catch { Console.WriteLine($"Presentation cycle: {cycle}"); throw; }
                Write(window, "_positionUpdateInProgress", false);
            }
            SettingsManager.Current.TaskbarWidgetAnimated = true;
            Console.WriteLine("Collapse regression passed: 100 cycles, interrupted returns, both styles, animated/instant, busy positioning, native region and real desktop pixels.");
            SettingsManager.Current.TaskbarWidgetAutoHide = true;
            Write(window, "_lastPlaybackStatus", Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
            window.ToggleWidgetExpansion(); Pump(30); window.CloseWidgetExpansion(false); Pump(1400);
            Check(window.Visibility == Visibility.Collapsed && !host!.IsVisible,
                "Paused auto-hide retained a detached or compact surface");
            Write(window, "_lastPlaybackStatus", Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            SettingsManager.Current.TaskbarWidgetAutoHide = false;
            Call(window, "UpdatePosition", true); Pump(150);
            CheckCompactRegion(window, widget, handle);

            foreach (int style in new[] { 0, 1 })
            {
                SettingsManager.Current.TaskbarWidgetSongChangeAnimation = style;
                Call(widget, "CommitPendingSong", initial with
                { Title = $"Style {style}: A long new title to exercise scrolling and row transitions", Artwork = style == 0 ? a : b });
                if (style == 1) Check((bool)Read(widget, "_songChangeSlideActive")!, "Slide transfer check did not start a real slide");
                int current = widget.SongCommitVersion;
                window.ToggleWidgetExpansion(); Pump(10);
                window.CloseWidgetExpansion(); Pump(10);
                window.ToggleWidgetExpansion(); Pump(10); // reverse collapse while its clock is running
                window.CloseWidgetExpansion(false); Pump(500);
                Check(widget.SongCommitVersion == current && Read(widget, "_lastIcon") != null,
                    "Transfer lost identity/cover during a song transition");
                Check(!(bool)Read(widget, "_songChangeSlideActive")!, "Slide remained stuck after transfer");
                Check(!(bool)Read(window, "_expansionOwnsLayout")! && compact.Children.Contains(widget),
                    "Reversed expansion settled in an obsolete host");
                Check(Read(widget, "_backgroundRotateTransform") != null && Read(widget, "_bakedBackground") != null,
                    "Transfer dropped rotating-background resources");
            }
            window.ToggleWidgetExpansion(); Pump(300);
            var destination = (Rect)Read(window, "_expansionTargetRect")!;
            destination.Offset(((Rect)Read(window, "_taskbarScreenRect")!).X, ((Rect)Read(window, "_taskbarScreenRect")!).Y);
            var nativeHost = (Rect)Read(window, "_nativeHostScreenRect")!;
            Check(nativeHost.Contains(destination), "Stable expanded surface clips its destination");
            window.DetectFullscreen = _ => true;
            Call(window, "UpdatePosition", true); Pump(50);
            Check(window.Visibility == Visibility.Collapsed && !host!.IsVisible,
                "Fullscreen suppression left the detached widget visible");
            window.DetectFullscreen = _ => false;
            Call(window, "UpdatePosition", true); Pump(100);
            window.ToggleWidgetExpansion();
            Call(window, "QueueTaskbarDisplayRefresh"); Pump(300);
            Check(!(bool)Read(window, "_expansionOwnsLayout")!, "DPI/shell geometry refresh left the obsolete expanded host active");
            Check(Read(window, "_handoffImage") == null && Read(window, "_handoffRendering") == null
                && Read(window, "_pendingExpansionAnimation") == null,
                "Settling retained a transfer bitmap, rendering subscription or deferred clock");
            TaskbarContentChecks.Run(window, widget, expanded, a, b);
            int recovered = 0;
            window.RecoveryRequested = () => recovered++;
            Call(window, "QueueRecovery"); Call(window, "QueueRecovery"); Pump(20);
            Check(recovered == 1, "Native child loss queued duplicate recovery");
            Console.WriteLine("Taskbar WPF checks passed: real child attachment, no activation, same-instance expansion/collapse reversal, pending publication, both styles, geometry refresh and unique recovery.");
        }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void CheckMorphContinuity(TaskbarWindow window, TaskbarWidgetControl widget,
        TaskbarWidgetExpandedContent expanded)
    {
        var clock = (DependencyProperty)typeof(TaskbarWindow).GetField("ExpansionFrameProperty",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        window.BeginAnimation(clock, null);
        var root = (FrameworkElement)widget.FindName("RootGrid");
        var border = (Border)widget.FindName("MainBorder");
        var elements = ((FrameworkElement element, Rect from, Rect to, double scale, bool visible)[])
            Read(widget, "_expansionElements")!;
        Check(elements.Length == 6, "Shared-element endpoints were not prepared");
        widget.UpdateLayout();
        var contentSize = root.RenderSize;
        Check(contentSize.Width >= expanded.Width && contentSize.Height >= expanded.Height,
            "Expansion content was not laid out at full size before the flight");
        var slots = elements.Select(e => LayoutInformation.GetLayoutSlot(e.element)).ToArray();
        var surface = (Rect)Read(window, "_nativeHostScreenRect")!;
        var samples = new List<(BitmapSource bitmap, Rect rect)>();
        var flight = (Rect)Read(window, "_expansionFlightRect")!;
        double dpi = (double)Read(window, "_positionDpiScale")!;
        bool opening = (bool)Read(window, "_widgetExpanded")!;
        for (int sample = 0; sample <= 40; sample++)
        {
            Call(window, "ApplyExpansionFrame", sample / 40.0);
            widget.UpdateLayout();
            Check(root.RenderSize == contentSize && border.RenderSize == contentSize,
                "Resizing the viewport reflowed the widget's content");
            var viewport = ((RectangleGeometry)border.Clip).Rect;
            Check(Math.Abs(viewport.Width - widget.Width) < 0.01
                && Math.Abs(viewport.Height - widget.Height) < 0.01,
                "The rounded viewport stopped following the animated widget size");
            foreach (string name in new[] { "SongTitle", "SongArtist" })
                Check(!((FrameworkElement)widget.FindName(name)).RenderTransform.HasAnimatedProperties,
                    "An invisible compact marquee kept running during expansion");
            for (int i = 0; i < elements.Length; i++)
            {
                var (element, from, to, _, _) = elements[i];
                var point = element.TransformToAncestor(root).Transform(new Point());
                double p = widget.ExpansionProgress;
                var expected = new Point(from.Left + (to.Left - from.Left) * p,
                    from.Top + (to.Top - from.Top) * p);
                Check((point - expected).Length < 0.05,
                    $"Shared element {i} wobbled at sample {sample}: {point} vs {expected}");
                Check(LayoutInformation.GetLayoutSlot(element) == slots[i],
                    $"Shared element {i} was reflowed during motion");
            }
            foreach (string name in new[] { "PreviousIcon", "NextIcon" })
                Check(((Wpf.Ui.Controls.SymbolIcon)expanded.FindName(name)).FontSize == 22,
                    "Animating a glyph's font size invalidates its centered layout");
            Check((Rect)Read(window, "_nativeHostScreenRect")! == surface,
                "The compositor surface moved during the shared-element animation");
            if (sample % 10 == 0)
            {
                var rect = (Rect)Call(window, "LiveWidgetRect")!;
                var frame = new RenderTargetBitmap((int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height),
                    96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                    context.DrawRectangle(new VisualBrush(widget) { Stretch = Stretch.Fill }, null, new Rect(widget.RenderSize));
                frame.Render(drawing); frame.Freeze(); samples.Add((frame, rect));
            }
        }
        var strip = new DrawingVisual();
        double cellWidth = flight.Width + 16;
        using (var context = strip.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null,
                new Rect(0, 0, cellWidth * samples.Count, flight.Height + 16));
            for (int i = 0; i < samples.Count; i++)
            {
                var (bitmap, rect) = samples[i];
                context.DrawImage(bitmap, new Rect(i * cellWidth + rect.Left - flight.Left + 8,
                    rect.Top - flight.Top + 8, rect.Width, rect.Height));
            }
        }
        var evidence = new RenderTargetBitmap((int)Math.Ceiling(cellWidth * samples.Count),
            (int)Math.Ceiling(flight.Height + 16), 96, 96, PixelFormats.Pbgra32);
        evidence.Render(strip);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(evidence));
        string evidencePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            opening ? "FluentFlyout-motion-open.png" : "FluentFlyout-motion-close.png");
        using (var output = System.IO.File.Create(evidencePath)) encoder.Save(output);
        Console.WriteLine($"Motion evidence: {evidencePath}");
        FrameworkElement[] nativeContent = [
            (FrameworkElement)widget.FindName("SongImageBorder"),
            (FrameworkElement)widget.FindName("SongTitle"),
            (FrameworkElement)widget.FindName("SongArtist")];
        var landingBounds = nativeContent.Select(element => element.TransformToAncestor(root)
            .TransformBounds(new Rect(element.RenderSize))).ToArray();
        var landingPixels = !opening ? nativeContent.Select(element => ReadVisualPixels(element, dpi)).ToArray() : null;
        if (!opening)
        {
            Check(((FrameworkElement)widget.FindName("MainGrid")).Opacity == 1
                && elements.Take(3).All(e => e.element.Opacity == 0),
                "The last collapse frame still substitutes scaled artwork or text for native compact content");
        }
        Call(window, "FinishWidgetExpansion", (int)Read(window, "_expansionVersion")!);
        if (!opening)
        {
            widget.UpdateLayout();
            for (int i = 0; i < nativeContent.Length; i++)
            {
                var settled = nativeContent[i].TransformToAncestor(root)
                    .TransformBounds(new Rect(nativeContent[i].RenderSize));
                Check((settled.TopLeft - landingBounds[i].TopLeft).Length < 0.01
                    && Math.Abs(settled.Width - landingBounds[i].Width) < 0.01
                    && Math.Abs(settled.Height - landingBounds[i].Height) < 0.01,
                    $"Compact content {i} jumped or changed size at the final handoff");
                Check(landingPixels![i].SequenceEqual(ReadVisualPixels(nativeContent[i], dpi)),
                    $"Compact content {i} changed rasterization at the final handoff");
            }
        }
        Check(double.IsNaN(border.Width) && double.IsNaN(border.Height),
            "The transition retained its fixed layout after settling");
        Console.WriteLine("Motion continuity passed: 41 samples, six shared elements, fixed layout and one clock.");
    }

    private static byte[] ReadVisualPixels(FrameworkElement element, double dpi)
    {
        int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi));
        int height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, new Rect(element.RenderSize));
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static void CheckCompactRegion(TaskbarWindow window, TaskbarWidgetControl widget, IntPtr handle)
    {
        var rect = (Rect)Call(window, "LiveWidgetRect")!;
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            Check(GetWindowRgn(handle, region) > 1, "Compact native region is empty after collapse");
            foreach (double fraction in new[] { 0.05, 0.5, 0.95 })
                Check(PtInRegion(region, (int)(rect.Left + rect.Width * fraction), (int)(rect.Top + rect.Height / 2)),
                    "Compact native region clips the returned widget");
        }
        finally { DeleteObject(region); }
    }
    private static void CheckPresented(TaskbarWindow window, Canvas canvas, IntPtr handle, bool save)
    {
        var rect = (Rect)Read(window, "_compactWidgetRect")!;
        double dpi = (double)Read(window, "_positionDpiScale")!;
        var markers = new List<Border>();
        var origin = new NativeMethods.POINT(); ClientToScreen(handle, ref origin);
        foreach (double fraction in new[] { 0.05, 0.5, 0.95 })
        {
            var marker = new Border { Width = 6, Height = 6,
                Background = System.Windows.Media.Brushes.Magenta, IsHitTestVisible = false };
            Canvas.SetLeft(marker, (rect.Left + rect.Width * fraction) / dpi - 3);
            Canvas.SetTop(marker, (rect.Top + rect.Height / 2) / dpi - 3);
            Canvas.SetZIndex(marker, 1000); canvas.Children.Add(marker); markers.Add(marker);
        }
        try
        {
            canvas.UpdateLayout(); Pump(100);
            var dc = GetDC(IntPtr.Zero);
            try
            {
                foreach (double fraction in new[] { 0.05, 0.5, 0.95 })
                {
                    int x = origin.X + (int)(rect.Left + rect.Width * fraction);
                    int y = origin.Y + (int)(rect.Top + rect.Height / 2);
                    uint actual = 0;
                    // WPF commits its layered surface asynchronously to the desktop.
                    // A dispatcher tick alone does not guarantee compositor presentation.
                    for (int attempt = 0; attempt < 25; attempt++)
                    {
                        var displayed = CapturePixels(dc, x, y, 1, 1);
                        actual = (uint)(displayed[2] | displayed[1] << 8 | displayed[0] << 16);
                        if (actual == 0x00FF00FF) break;
                        Pump(20);
                    }
                    if (actual != 0x00FF00FF) Console.WriteLine($"Presentation failure: pixel={actual:X8}, point={x},{y}, marker={markers[0].PointToScreen(new Point(3,3))}, dpi={System.Windows.Media.VisualTreeHelper.GetDpi(canvas)}, hit={WindowFromPoint(new NativeMethods.POINT { X = x, Y = y })}, expected={handle}, parent={NativeMethods.GetParent(handle)}, opacity={window.Opacity}, region={rect}");
                    Check(actual == 0x00FF00FF, "Real desktop hides part of the compact surface behind another window");
                    Check(WindowFromPoint(new NativeMethods.POINT { X = x, Y = y }) == handle,
                        "Returned compact does not own its native hit-test points");
                }
                if (save)
                {
                    int width = (int)rect.Width, height = (int)rect.Height;
                    byte[] pixels = CapturePixels(dc, origin.X + (int)rect.Left, origin.Y + (int)rect.Top, width, height);
                    var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, width * 4);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    string path = System.IO.Path.Combine(Environment.CurrentDirectory, "docs", "audits", "collapse-return-desktop.png");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    using var stream = System.IO.File.Create(path); encoder.Save(stream);
                    Console.WriteLine($"Desktop evidence: {path}");
                }
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
        finally { foreach (var marker in markers) canvas.Children.Remove(marker); }
    }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativeMethods.POINT point);
    private static byte[] CapturePixels(IntPtr screen, int x, int y, int width, int height)
    {
        var dc = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(dc, bitmap);
        try
        {
            Check(BitBlt(dc, 0, 0, width, height, screen, x, y, 0x40CC0020), "Desktop capture failed");
            SelectObject(dc, previous);
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            var pixels = new byte[width * height * 4];
            Check(GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0) == height, "Desktop bitmap read failed");
            return pixels;
        }
        finally { SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, Bits;
        public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important, Color;
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
