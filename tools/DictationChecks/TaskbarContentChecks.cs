using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Controls;
using FluentFlyout.Windows;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using Windows.Media.Control;

// Included in the existing --taskbar checks. Runs only when explicitly requested.
internal static class TaskbarContentChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Read(object target, string field) => target.GetType().GetField(field, Private)!.GetValue(target);
    private static void Write(object target, string field, object? value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, Private)!.Invoke(target, args);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    internal static void Run(TaskbarWindow window, TaskbarWidgetControl widget,
        TaskbarWidgetExpandedContent expanded, BitmapImage a, BitmapImage b)
    {
        CheckTimelineRetention(a);
        var settings = SettingsManager.Current;
        var title = (TextBlock)widget.FindName("SongTitle");
        var artist = (TextBlock)widget.FindName("SongArtist");
        var titleSlot = (Canvas)widget.FindName("SongTitleContainer");
        var artistSlot = (Canvas)widget.FindName("SongArtistContainer");
        var main = (FrameworkElement)widget.FindName("MainGrid");
        var expandedTitle = (FrameworkElement)expanded.FindName("TitleMorph");
        var expandedArtist = (FrameworkElement)expanded.FindName("ArtistMorph");
        var expandedRoot = (FrameworkElement)expanded.FindName("TopRow");
        var visualizer = (FrameworkElement)window.FindName("TaskbarVisualizer");
        var initial = new MediaArtworkSnapshot(new object(), "Track", "Artist", a);
        int settle = Math.Max(100, MainWindow.getDuration() + 100);
        void Publish(string song, string author, BitmapImage? cover = null) =>
            Call(widget, "CommitPendingSong", initial with { Title = song, Artist = author, Artwork = cover ?? a });
        double Y(FrameworkElement element, FrameworkElement root) =>
            element.TransformToAncestor(root).Transform(new Point()).Y;

        settings.TaskbarWidgetHideCompletely = false;
        settings.TaskbarWidgetResizeAnimated = false;
        settings.TaskbarWidgetFixedWidth = false;
        settings.TaskbarWidgetScrollingEnabled = false;
        CheckPausedSelection(window, widget, a, settle);
        Call(widget, "ShowNoMediaPlaceholder"); Pump(settle);
        double emptyWidth = widget.Width;

        // No follow-up metadata or explicit positioning call: the deferred publication
        // itself must grow the visible widget and move the visualizer.
        var publication = new MediaArtworkPublication(Dispatcher.CurrentDispatcher,
            _ => Task.FromResult<MediaArtworkSnapshot?>(null), song => Call(widget, "CommitPendingSong", song));
        publication.Observe(initial with { Title = "A delayed song title that needs substantially more space", Artwork = null });
        Pump(MediaArtworkBuffer.WaitMs + settle + 300);
        Check(widget.Width > emptyWidth + 20 && widget.HasPublishedSong,
            "Deferred publication did not restore the compact widget width");
        Check(Math.Abs(widget.Width - widget.CalculateSize(1).logicalWidth) < 2,
            "Host width does not match the published content");

        foreach (bool animated in new[] { false, true })
        foreach (bool fixedWidth in new[] { false, true })
        {
            settings.TaskbarWidgetAnimated = animated;
            settings.TaskbarWidgetFixedWidth = fixedWidth;
            settings.TaskbarWidgetSongChangeAnimation = 1;
            Publish("Reference track", "Reference artist"); Pump(settle); widget.UpdateLayout();
            double titleY = Y(titleSlot, main), artistY = Y(artistSlot, main);
            window.ToggleWidgetExpansion(); Pump(settle); widget.UpdateLayout();
            double expandedTitleY = Y(expandedTitle, expandedRoot), expandedArtistY = Y(expandedArtist, expandedRoot);
            window.CloseWidgetExpansion(false); Pump(settle);

            foreach (var text in new[] { ("Song only", ""), ("", "Artist only"), ("Both", "Author"), (" ", "\t") })
            {
                Publish(text.Item1, text.Item2); Pump(20); widget.UpdateLayout();
                bool hasTitle = !string.IsNullOrWhiteSpace(text.Item1);
                bool hasArtist = !string.IsNullOrWhiteSpace(text.Item2);
                if (hasTitle || hasArtist)
                    Check(Math.Abs(Y(titleSlot, main) - titleY) < 0.1 && Math.Abs(Y(artistSlot, main) - artistY) < 0.1,
                        "A compact row moved during its text animation");
                Pump(settle); widget.UpdateLayout();
                Check(title.Text == (hasTitle ? text.Item1 : "") && artist.Text == (hasArtist ? text.Item2 : ""),
                    "Missing metadata gained a placeholder or stale text");
                Check(titleSlot.Visibility == (hasTitle ? Visibility.Visible : Visibility.Hidden)
                    && artistSlot.Visibility == (hasArtist ? Visibility.Visible : Visibility.Hidden),
                    "An empty text row did not reserve its height");
                if (hasTitle || hasArtist)
                    Check(Math.Abs(Y(titleSlot, main) - titleY) < 0.1 && Math.Abs(Y(artistSlot, main) - artistY) < 0.1,
                        "A compact text row moved when its sibling disappeared");
                if (fixedWidth)
                    Check(Math.Abs(titleSlot.Width - (Math.Min(settings.TaskbarWidgetFixedWidthPx, 216) - 49.5)) < 0.1,
                        "Missing metadata overrode the configured fixed text width");
                window.ToggleWidgetExpansion(); Pump(settle); widget.UpdateLayout();
                Check((bool)Read(window, "_widgetExpanded")!, "An untitled published session could not expand");
                Check(Math.Abs(Y(expandedTitle, expandedRoot) - expandedTitleY) < 0.1
                    && Math.Abs(Y(expandedArtist, expandedRoot) - expandedArtistY) < 0.1,
                    "An expanded text row moved when its sibling disappeared");
                window.CloseWidgetExpansion(false); Pump(settle);
            }
        }

        settings.TaskbarWidgetFixedWidth = false;
        settings.TaskbarWidgetAnimated = true;
        Publish("Expanded reference", "Expanded artist"); Pump(settle);
        window.ToggleWidgetExpansion(); Pump(settle); widget.UpdateLayout();
        double titleTop = Y(expandedTitle, expandedRoot), artistTop = Y(expandedArtist, expandedRoot);
        foreach (var text in new[] { ("Expanded song only", ""), ("", "Expanded artist only"), ("Restored", "Author") })
        {
            Publish(text.Item1, text.Item2); Pump(20); widget.UpdateLayout();
            Check(Math.Abs(Y(expandedTitle, expandedRoot) - titleTop) < 0.1
                && Math.Abs(Y(expandedArtist, expandedRoot) - artistTop) < 0.1,
                "An expanded row moved during its text animation");
            Pump(settle); widget.UpdateLayout();
            Check(Math.Abs(Y(expandedTitle, expandedRoot) - titleTop) < 0.1
                && Math.Abs(Y(expandedArtist, expandedRoot) - artistTop) < 0.1,
                "An expanded row recentered after its text animation");
        }
        window.CloseWidgetExpansion(false); Pump(settle);
        Publish("Outgoing", "Old artist"); Pump(settle);
        Publish("Incoming", "New artist");
        int slideVersion = (int)Read(widget, "_songChangeSlideVersion")!;
        Publish("Incoming", "New artist", b);
        Check((int)Read(widget, "_songChangeSlideVersion")! == slideVersion,
            "A late cover restarted or canceled the song's text transition");
        Call(widget, "ShowNoMediaPlaceholder");
        Publish("Latest", ""); Pump(settle);
        Check(title.Text == "Latest" && artist.Text == "" && !(bool)Read(widget, "_songChangeSlideActive")!,
            "An obsolete transition overwrote the song after a media gap");
        for (int i = 0; i < 12; i++) Publish($"Rapid {i}", i % 2 == 0 ? "Artist" : "");
        widget.ApplyTextStyle(); Pump(settle);
        Check(title.Text == "Rapid 11" && !(bool)Read(widget, "_songChangeSlideActive")!,
            "Restyling a rapid-skip transition left stale text or a stuck slide");

        settings.TaskbarWidgetScrollingEnabled = true;
        settings.TaskbarWidgetScrollingTextSpeed = 0;
        Publish(new string('W', 120), "Author"); Pump(settle);
        Check(double.IsFinite(((TranslateTransform)title.RenderTransform).X),
            "An invalid persisted scroll speed produced an invalid text transform");
        settings.TaskbarWidgetScrollingTextSpeed = 20;
        widget.UpdateMarquees();
        Check(title.RenderTransform.HasAnimatedProperties,
            "A visible long title did not start its marquee");
        window.ToggleWidgetExpansion();
        Check(!title.RenderTransform.HasAnimatedProperties,
            "The hidden compact marquee kept running during expansion");
        window.CloseWidgetExpansion(false); Pump(settle);
        Check(title.RenderTransform.HasAnimatedProperties,
            "Returning to the compact widget did not resume its marquee");
        settings.TaskbarWidgetScrollingEnabled = false;
        widget.UpdateMarquees();

        // Synthetic geometry exercises both orientations and fractional DPI without
        // changing the user's display configuration or querying Explorer's controls.
        settings.TaskbarWidgetAnimated = false;
        settings.TaskbarWidgetPosition = 0;
        settings.TaskbarWidgetPadding = false;
        foreach (double dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (bool vertical in new[] { false, true })
        foreach (int side in new[] { 0, 1 })
        foreach (int controls in new[] { 0, 1 })
        foreach (bool art in new[] { false, true })
        {
            settings.TaskbarWidgetShowAlbumArt = art;
            settings.TaskbarWidgetControlsPosition = controls;
            settings.TaskbarVisualizerPosition = side;
            widget.ReorderControls();
            var bar = new NativeMethods.RECT { Right = vertical ? 60 : 1920, Bottom = vertical ? 1080 : 60 };
            var widgetRect = (Rect)Call(window, "PositionWidget", IntPtr.Zero, bar, dpi, true, vertical, false)!;
            var vizRect = (Rect)Call(window, "PositionVisualizer", IntPtr.Zero, bar, dpi, true, vertical, false)!;
            Check(!vizRect.IsEmpty && visualizer.Width == 84, "Visualizer size changed");
            double gap = vertical
                ? (side == 0 ? widgetRect.Top - vizRect.Bottom : vizRect.Top - widgetRect.Bottom)
                : (side == 0 ? widgetRect.Left - vizRect.Right : vizRect.Left - widgetRect.Right);
            Check(Math.Abs(gap) <= 2, "Visualizer detached from the widget at a DPI/orientation change");
        }
        Call(window, "UpdatePosition", true); Pump(settle);
        CheckBackgroundPreparation(widget, a, b);
        Console.WriteLine("Taskbar content checks passed: deferred sizing, fixed rows, untitled expansion, interrupted slides, late art and visualizer geometry.");
    }

    private static void CheckTimelineRetention(BitmapImage artwork)
    {
        var content = new TaskbarWidgetExpandedContent();
        try
        {
            content.SetMorphProgress(1);
            Write(content, "_minimumSeconds", 0.0);
            Write(content, "_positionSeconds", 60.0);
            Write(content, "_maximumSeconds", 200.0);
            Write(content, "_positionUpdatedAt", DateTimeOffset.UtcNow);
            Write(content, "_progressSeekable", false);
            Call(content, "ApplyCachedProgress", DateTimeOffset.UtcNow);
            var row = (FrameworkElement)content.FindName("SeekRow");
            var seek = (System.Windows.Controls.Slider)content.FindName("Seekbar");
            Check(row.Visibility == Visibility.Visible && !seek.IsEnabled,
                "Available times must remain visible even when the player disables seeking");
            int version = (int)Read(content, "_progressVersion")!;
            Write(content, "_active", true);
            // No MainWindow is attached: the asynchronous refresh cannot read a player.
            // A cosmetic publication must not invalidate or erase a valid timeline.
            content.PublishSong("Same track", "Artist", artwork);
            Check((int)Read(content, "_progressVersion")! == version,
                "Metadata publication invalidated an in-flight timeline read");
            for (int attempt = 0; attempt < 80 && (int)Read(content, "_progressRefreshPending")! != 0; attempt++)
                Pump(25);
            Check(row.Visibility == Visibility.Visible && seek.Value == 60 && seek.Maximum == 200,
                "An unavailable timeline read erased the last valid taskbar position");
            Check((int)Read(content, "_progressRefreshPending")! == 0,
                "An unavailable player left the taskbar timeline refresh stuck");
        }
        finally { content.DisposeResources(); }
    }

    private static void CheckBackgroundPreparation(TaskbarWidgetControl widget, BitmapImage a, BitmapImage b)
    {
        var settings = SettingsManager.Current;
        int originalRadius = settings.TaskbarWidgetBackgroundBlurRadius;
        bool originalRotation = settings.TaskbarWidgetBackgroundRotate;
        bool originalAnimation = settings.TaskbarWidgetAnimated;
        var property = typeof(TaskbarWidgetControl).GetProperty("BackgroundBaker", Private)!;
        var originalBaker = property.GetValue(widget);
        var requests = new List<(double radius, TaskCompletionSource<BitmapSource?> completion)>();
        Func<BitmapImage, double, double, double, Task<BitmapSource?>> baker = (_, _, _, radius) =>
        {
            var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Add((radius, completion));
            return completion.Task;
        };
        property.SetValue(widget, baker);
        try
        {
            Call(widget, "CancelBackgroundCrossfade");
            Write(widget, "_bakedBackground", null);
            ((System.Collections.IDictionary)Read(widget, "_bakedBackgroundCache")!).Clear();
            Write(widget, "_currentIcon", a);
            Write(widget, "_backgroundRotationActive", true);
            settings.TaskbarWidgetBackgroundRotate = true;
            settings.TaskbarWidgetAnimated = false;
            void Request(int radius)
            {
                settings.TaskbarWidgetBackgroundBlurRadius = radius;
                Call(widget, "UpdateBakedBackgroundAsync", a, 512.0);
            }
            Request(17); Request(39);
            Check(requests.Count == 2, "Changing blur radius reused an old preparation request");
            requests[1].completion.SetResult(b); Pump(40);
            requests[0].completion.SetResult(a); Pump(40);
            Check((double)Read(widget, "_bakedBlurRadius")! == 39
                && ReferenceEquals(Read(widget, "_bakedBackground"), b),
                "An old blur result overwrote the current setting");
            Request(39);
            Check(requests.Count == 2, "The current texture was unnecessarily prepared again");

            Request(17);
            requests[2].completion.SetResult(a); Pump(40);
            Request(39);
            Check(requests.Count == 3 && ReferenceEquals(Read(widget, "_bakedBackground"), b),
                "Returning to a cached radius prepared a duplicate texture");

            Request(55); // Deliberately leave this request running while selecting a cache hit.
            Request(39);
            requests[3].completion.SetResult(a); Pump(40);
            Check((double)Read(widget, "_bakedBlurRadius")! == 39,
                "An in-flight result survived returning to a cached setting");
            for (int radius = 60; radius < 68; radius++)
            {
                Request(radius); requests[^1].completion.SetResult(a); Pump(40);
            }
            Check(((System.Collections.IDictionary)Read(widget, "_bakedBackgroundCache")!).Count <= 6,
                "The background cache exceeded its bound");

            // A third cover arriving mid-crossfade must preserve the visible blend.
            settings.TaskbarWidgetAnimated = true;
            var background = (Image)widget.FindName("BackgroundImage");
            var incoming = (Image)widget.FindName("BackgroundImageNext");
            Call(widget, "CancelBackgroundCrossfade");
            background.Source = a;
            background.Opacity = 0.35;
            incoming.Source = b; incoming.Visibility = Visibility.Visible; incoming.Opacity = 0.3;
            Write(widget, "_bgCrossfadeTarget", b);
            var third = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 200, 20, 50, 255 }, 4);
            third.Freeze();
            Call(widget, "BeginBackgroundCrossfade", third, 1000);
            Check(!ReferenceEquals(background.Source, a) && !ReferenceEquals(background.Source, b)
                && ReferenceEquals(incoming.Source, third), "A third cover replaced a partially visible layer");
            Call(widget, "CancelBackgroundCrossfade");
            background.ClearValue(UIElement.OpacityProperty);
            incoming.ClearValue(UIElement.OpacityProperty);
        }
        finally
        {
            property.SetValue(widget, originalBaker);
            settings.TaskbarWidgetBackgroundBlurRadius = originalRadius;
            settings.TaskbarWidgetBackgroundRotate = originalRotation;
            settings.TaskbarWidgetAnimated = originalAnimation;
            Call(widget, "SetBackground", b);
        }
        Console.WriteLine("Background checks passed: radius keys, reversed completions, cache hits, bounded storage and interrupted blends.");
    }

    private static void CheckPausedSelection(TaskbarWindow window, TaskbarWidgetControl widget,
        BitmapImage cover, int settle)
    {
        var settings = SettingsManager.Current;
        settings.TaskbarWidgetAutoHide = false;
        settings.TaskbarWidgetHideCompletely = true;
        void Show(string song, BitmapImage? art, GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) =>
            window.UpdateUi(song, "", art, status);
        void CheckVisible(string scenario)
        {
            Check(window.Visibility == Visibility.Visible && window.IsHitTestVisible
                && widget.Visibility == Visibility.Visible && widget.IsHitTestVisible,
                $"Paused session switch lost its interactive surface: {scenario}");
            Check(((FrameworkElement)widget.FindName("SongImageBorder")).Visibility == Visibility.Visible,
                "Session switch target disappeared when metadata was unavailable");
        }

        Show("Playing player", cover, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        Pump(MediaArtworkBuffer.WaitMs + settle);
        Show("Paused player", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
        Pump(MediaArtworkBuffer.WaitMs + settle + 800);
        CheckVisible("readable paused player");

        // Switching to a paused player can temporarily yield neither properties nor
        // a playback snapshot. This is not the explicit no-session sentinel.
        Show("", null, null);
        Pump(MediaArtworkBuffer.WaitMs + settle + 800);
        CheckVisible("temporarily missing metadata and playback snapshot");
        Show("Playing player", cover, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        Pump(MediaArtworkBuffer.WaitMs + settle);
        CheckVisible("return to the playing player");
        Check((string?)Read(widget, "_actualTitle") == "Playing player",
            "Returning from a paused player did not publish the playing song");

        settings.TaskbarWidgetAutoHide = true;
        Show("Paused player", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
        settings.TaskbarWidgetAutoHide = false;
        Pump(MediaArtworkBuffer.WaitMs + settle + 800);
        CheckVisible("auto-hide disabled while its timer was pending");

        settings.TaskbarWidgetAutoHide = true;
        Show("Paused player", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
        Pump(settle + 800);
        Check(window.Visibility == Visibility.Collapsed, "Explicitly enabled pause auto-hide stopped working");
        settings.TaskbarWidgetAutoHide = false;
        Show("Playing player", cover, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        Pump(MediaArtworkBuffer.WaitMs + settle);
        CheckVisible("auto-hide disabled again");

        // Only the actual absence sentinel should hide the whole surface.
        window.UpdateUi("-", "-", null, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
        Pump(settle + 800);
        Check(window.Visibility == Visibility.Collapsed,
            "Confirmed session removal stopped honoring Hide Completely");
        Show("Paused player", cover, GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);
        Pump(MediaArtworkBuffer.WaitMs + settle);
        CheckVisible("paused session appears again after a genuine close");
        settings.TaskbarWidgetHideCompletely = false;
        Console.WriteLine("Paused selection checks passed: no auto-hide, unknown snapshots, return to playback, obsolete timer and explicit closure.");
    }
}
