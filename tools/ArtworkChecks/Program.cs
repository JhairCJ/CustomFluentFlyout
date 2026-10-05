using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes;
using FluentFlyout.Controls.TaskbarWidget;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;

// dotnet run --project tools/ArtworkChecks -c Release
internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        var a = Cover(72, 64, false);
        var b = Cover(72, 64, true); // Identical palette, reversed spatial layout.
        var resized = Cover(144, 128, false);
        var compressed = Cover(72, 64, false, jpeg: true);
        Require(AlbumArtworkSimilarity.AreSimilar(a, resized), "Resolution must not trigger a flip");
        Require(AlbumArtworkSimilarity.AreSimilar(a, compressed), "JPEG compression must not trigger a flip");
        Require(!AlbumArtworkSimilarity.AreSimilar(a, b), "Matching palettes must not hide different layouts");
        Require(!AlbumArtworkSimilarity.AreSimilar(Solid(Colors.Red), Solid(Colors.Blue)), "Color changes must be detected on flat images");
        Require(AlbumArtworkSimilarity.AreSimilar(null, null), "Two placeholders are equivalent");

        var source = new object();
        var initial = new MediaArtworkSnapshot(source, "A", "Artist", a);
        var next = initial with { Title = "B" };
        var complete = next with { Artwork = b };
        var buffer = new MediaArtworkBuffer();
        Require(buffer.Offer(initial, 0) == initial, "First complete song publishes immediately");
        Require(buffer.Offer(next, 10) == null && buffer.Published == initial, "New title with old artwork must wait atomically");
        long deadline = buffer.Deadline;
        int version = buffer.Version;
        buffer.Offer(next, 100);
        Require(buffer.Deadline == deadline && buffer.Version == version, "Duplicates cannot extend the deadline or invalidate the read");
        Require(buffer.Offer(complete, 150) == complete && buffer.Pending == null, "Late distinct artwork commits the complete song once");
        Require(buffer.Offer(complete, 160) == null, "Repeated completed metadata cannot republish");
        buffer.Offer(next with { Title = "Canceled skip", Artwork = null }, 170);
        Require(buffer.Offer(complete, 180) == null && buffer.Pending == null,
            "Returning to the visible track cancels an intermediate skip without republishing");

        var noArt = complete with { Title = "No artwork", Artwork = null };
        buffer.Offer(noArt, 200);
        Require(buffer.Resolve(noArt, 549) == null, "Missing artwork gets its bounded grace period");
        Require(buffer.Resolve(noArt, 550)?.Artwork == null && buffer.Published?.Title == "No artwork", "Absent artwork publishes a placeholder, never the old album");
        var late = noArt with { Artwork = a };
        Require(buffer.Offer(late, 900) == late, "Artwork arriving after a placeholder does not delay again");
        Require(Transition(null, a) == AlbumArtworkTransition.Fade, "Late artwork fades in without another flip");
        Require(AlbumArtworkSimilarity.ChooseTransition(null, a, false, false) == AlbumArtworkTransition.Direct, "First-ever artwork does not animate");
        Require(Transition(a, b) == AlbumArtworkTransition.Flip, "Different valid covers flip once");
        Require(Transition(a, resized) == AlbumArtworkTransition.Direct, "Similar valid covers do not animate");
        Require(Transition(a, null) == AlbumArtworkTransition.Fade, "Removing artwork fades to the placeholder");
        Require(AlbumArtworkSimilarity.ChooseTransition(a, b, true, true) == AlbumArtworkTransition.Fade, "The user's crossfade preference is honored");

        var shared = late with { Title = "Same album", Artwork = resized };
        buffer.Offer(shared, 1000);
        Require(buffer.Pending == shared, "Visually identical covers also wait on a new track");
        Require(buffer.Resolve(shared, 1350) == shared, "A fresh read can confirm a legitimately shared album");
        var switched = shared with { Session = new object() };
        Require(buffer.Offer(switched, 1400) == null, "Changing sources cannot masquerade as duplicate metadata");
        Require(buffer.Resolve(null, 1750)?.Artwork == null, "Failed refresh clears stale artwork");

        var slow = switched with { Title = "Intermediate", Artwork = null };
        buffer.Offer(slow, 1800);
        var last = slow with { Title = "Last" };
        buffer.Offer(last, 1900);
        Require(buffer.Deadline == 2250 && buffer.Pending == last, "Rapid skips keep only the final identity and its deadline");
        var freshDifferentTrack = last with { Title = "Changed during re-read" };
        Require(buffer.Resolve(freshDifferentTrack, 2250) == null && buffer.Pending == freshDifferentTrack,
            "A re-read revealing a new track starts a new coherent update");
        version = buffer.Version;
        buffer.Cancel();
        Require(buffer.Pending == null && buffer.Version != version && buffer.Resolve(last, 3000) == null,
            "Stopping invalidates every pending publication");

        AsyncPublicationChecks(a, b);
        AccentOwnershipChecks();
        TransitionLifecycleChecks(a, b);
        Console.WriteLine($"Artwork checks passed: {_checks}; hidden WPF host, no media playback or input hooks.");
    }

    private static void AsyncPublicationChecks(BitmapImage a, BitmapImage b)
    {
        var source = new object();
        var initial = new MediaArtworkSnapshot(source, "A", "Artist", a);
        var waiting = initial with { Title = "B" };
        var read = new TaskCompletionSource<MediaArtworkSnapshot?>();
        var commits = new List<MediaArtworkSnapshot>();
        int reads = 0;
        var publication = new MediaArtworkPublication(Dispatcher.CurrentDispatcher,
            _ => { reads++; return read.Task; }, commits.Add);
        publication.Observe(initial);
        publication.Observe(waiting);
        Pump(400);
        Require(reads == 1 && commits.Count == 1, "The UI can keep running while one deadline read is in flight");
        var latest = waiting with { Title = "C", Artwork = b };
        publication.Observe(latest);
        read.SetResult(waiting);
        Pump(30);
        Require(commits.Count == 2 && commits[^1] == latest, "An older async read cannot overwrite a newer committed track");
        publication.Cancel();

        var closedRead = new TaskCompletionSource<MediaArtworkSnapshot?>();
        commits.Clear();
        publication = new MediaArtworkPublication(Dispatcher.CurrentDispatcher, _ => closedRead.Task, commits.Add);
        publication.Observe(initial);
        publication.Observe(waiting);
        Pump(400);
        publication.Cancel();
        closedRead.SetResult(waiting);
        Pump(30);
        Require(commits.Count == 1, "Closing during a read prevents publication after closure");

        commits.Clear();
        publication = new MediaArtworkPublication(Dispatcher.CurrentDispatcher,
            _ => Task.FromException<MediaArtworkSnapshot?>(new InvalidOperationException("Closed session")), commits.Add);
        publication.Observe(initial);
        publication.Observe(waiting);
        Pump(430);
        Require(commits.Count == 2 && commits[^1].Title == "B" && commits[^1].Artwork == null,
            "Read errors publish the new identity with a placeholder");
        publication.Cancel();

        commits.Clear();
        var never = new TaskCompletionSource<MediaArtworkSnapshot?>();
        publication = new MediaArtworkPublication(Dispatcher.CurrentDispatcher, _ => never.Task, commits.Add);
        publication.Observe(initial);
        publication.Observe(waiting);
        Pump(680);
        Require(commits.Count == 2 && commits[^1].Artwork == null, "An unresponsive source cannot keep artwork pending forever");
        publication.Cancel();
        never.SetResult(waiting);
        Pump(20);
        Require(commits.Count == 2, "A refresh completing after its timeout cannot resurrect stale artwork");
    }

    private static AlbumArtworkTransition Transition(BitmapImage? old, BitmapImage? next)
        => AlbumArtworkSimilarity.ChooseTransition(old, next, true, false);

    private static void TransitionLifecycleChecks(BitmapImage a, BitmapImage b)
    {
        var firstScale = new ScaleTransform();
        var secondScale = new ScaleTransform();
        var applied = new List<BitmapImage?>();
        var firstSurface = new System.Windows.Controls.Border { RenderTransform = firstScale };
        var secondSurface = new System.Windows.Controls.Border { RenderTransform = secondScale };
        using var host = new HwndSource(new HwndSourceParameters("Artwork checks")
        { Width = 8, Height = 8, WindowStyle = int.MinValue }); // WS_POPUP, no WS_VISIBLE.
        var root = new System.Windows.Controls.StackPanel();
        root.Children.Add(firstSurface);
        root.Children.Add(secondSurface);
        host.RootVisual = root;
        var transition = new AlbumArtTransition(applied.Add,
            (firstSurface, firstScale), (secondSurface, secondScale));
        transition.Show(a, true, 200, false);
        Require(applied.Count == 1 && applied[0] == a, "The shared transition presents its first cover directly");
        transition.Show(b, true, 200, false);
        transition.Show(b, true, 200, false);
        var latest = Solid(Colors.Green);
        transition.Show(latest, true, 200, false);
        Pump(400);
        Require(applied.Count == 2 && applied[^1] == latest && !applied.Contains(b),
            "A duplicated flip and rapid replacement apply only the latest pending cover");
        Require(firstScale.ScaleX == 1 && secondScale.ScaleX == 1,
            "Both island surfaces settle at their full width");
        transition.Set(a);
        transition.Show(b, true, 200, false);
        Pump(150);
        var equivalent = Cover(144, 128, true);
        transition.Show(equivalent, true, 200, false);
        Pump(90);
        Require(applied[^1] == equivalent && firstScale.ScaleX == 1,
            "Equivalent artwork arriving after the midpoint updates without chaining a second flip");
        transition.Show(b, true, 200, false);
        transition.Show(b, false, 200, false);
        int instantCount = applied.Count;
        Pump(300);
        Require(applied.Count == instantCount && applied[^1] == b && firstScale.ScaleX == 1,
            "Disabling animations cancels the running flip and stale completion callbacks");
        transition.Show(a, true, 200, false);
        transition.Set(null);
        Pump(300);
        Require(applied[^1] == null && transition.Target == null,
            "A placeholder cancels a pending cover without resurrecting the previous artwork");
        instantCount = applied.Count;
        transition.Show(a, true, 200, false);
        Require(applied.Count == instantCount + 1 && applied[^1] == a,
            "Late artwork updates both surfaces once through the fade path");
        transition.Stop();
    }

    private static void AccentOwnershipChecks()
    {
        var widgetArt = Solid(Colors.Red);
        var otherArt = Solid(Colors.Blue);
        var settings = FluentFlyout.Classes.Settings.SettingsManager.Current;
        AlbumAccent.SetTaskbarArtwork(widgetArt);
        Color widgetColor = AlbumAccent.TaskbarBrush.Color;
        AlbumAccent.Refresh(otherArt, 11, true, true);
        Require(AlbumAccent.Refresh(otherArt, 11, true, false).Color == AlbumAccent.ToThemed(Colors.Blue, false),
            "A cached global cover must respect a changed theme");
        AlbumAccent.Refresh(otherArt, 11, false, false);
        Require(!AlbumAccent.HasAlbumColor && AlbumAccent.Brush.Color == AlbumAccent.SystemFallback().Color,
            "Disabled global accents display the system fallback");
        AlbumAccent.RefreshTheme(false);
        Require(AlbumAccent.Brush.Color == AlbumAccent.ToThemed(Colors.Blue, false),
            "Theme refresh can restore retained artwork without relying on a last-decoded image");
        Require(AlbumAccent.TaskbarBrush.Color == widgetColor && AlbumAccent.Brush.Color != widgetColor,
            "A foreign active media source must not recolor the widget or its visualizer");
        Require(AlbumAccent.TaskbarBrush.IsFrozen, "The visualizer receives an immutable widget brush");
        Wpf.Ui.Appearance.ApplicationThemeManager.Theme = Wpf.Ui.Appearance.ApplicationTheme.Light;
        AlbumAccent.RefreshTaskbarTheme();
        Require(AlbumAccent.TaskbarBrush.Color == AlbumAccent.ToThemed(Colors.Red, false),
            "Theme changes must use the widget's retained artwork, not the newest media");
        settings.AlbumAccentDesaturationThreshold = 0;
        settings.AlbumAccentDesaturationAmount = 100;
        AlbumAccent.RefreshTaskbarTheme();
        var neutral = AlbumAccent.TaskbarBrush.Color;
        Require(neutral.R == neutral.G && neutral.G == neutral.B,
            "Desaturation settings apply to the retained widget color");
        settings.UseAlbumArtAsAccentColor = false;
        AlbumAccent.RefreshTaskbarTheme();
        Require(AlbumAccent.TaskbarBrush.Color == AlbumAccent.SystemFallback().Color,
            "Disabling album accents restores the system accent");
        settings.UseAlbumArtAsAccentColor = true;
        settings.AlbumAccentDesaturationThreshold = 65;
        settings.AlbumAccentDesaturationAmount = 0;
        AlbumAccent.RefreshTaskbarTheme();
        Require(AlbumAccent.TaskbarBrush.Color == AlbumAccent.ToThemed(Colors.Red, false),
            "Re-enabling album accents restores the widget's cover");
        AlbumAccent.SetTaskbarArtwork(otherArt);
        Require(AlbumAccent.TaskbarBrush.Color == AlbumAccent.ToThemed(Colors.Blue, false),
            "A newly presented widget cover updates the visualizer's color");
        AlbumAccent.SetTaskbarArtwork(null);
        AlbumAccent.Refresh(widgetArt, 12, true, false);
        Require(AlbumAccent.TaskbarBrush.Color == AlbumAccent.SystemFallback().Color,
            "A widget placeholder cannot inherit the color of unrelated media");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        ++_checks;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static BitmapImage Solid(Color color) => Encode(BitmapSource.Create(1, 1, 96, 96,
        PixelFormats.Bgra32, null, new byte[] { color.B, color.G, color.R, 255 }, 4), false);

    private static BitmapImage Cover(int width, int height, bool reverse, bool jpeg = false)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool stripe = x < width / 3;
                if (reverse) stripe = x >= width * 2 / 3;
                int i = (y * width + x) * 4;
                pixels[i] = stripe ? (byte)40 : (byte)180;
                pixels[i + 1] = stripe ? (byte)70 : (byte)130;
                pixels[i + 2] = stripe ? (byte)200 : (byte)30;
                pixels[i + 3] = 255;
            }
        return Encode(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4), jpeg);
    }

    private static BitmapImage Encode(BitmapSource source, bool jpeg)
    {
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 90 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
