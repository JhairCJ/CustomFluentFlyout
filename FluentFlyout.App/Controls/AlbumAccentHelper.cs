using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace FluentFlyout.App.Controls;

/// <summary>
/// Resolves the bar accent color for the equalizer. With "use album art as accent"
/// enabled it computes a dominant color from the current artwork (simple hue-bucketed
/// average with a saturation floor); otherwise it falls back to the brand accent.
/// </summary>
public static class AlbumAccentHelper
{
    private static readonly object gate = new();
    private static Color currentAccent = Color.FromArgb(255, 85, 214, 190); // brand accent
    private static bool hasCapturedArtwork;

    public static event EventHandler<Color>? AccentChanged;

    public static Color GetAccentColor()
    {
        lock (gate)
            return currentAccent;
    }

    /// <summary>Feeds new artwork in; computes the accent on a background thread.</summary>
    public static void UpdateArtwork(byte[]? artworkBytes, bool useAlbumAccent)
    {
        if (!useAlbumAccent || artworkBytes is not { Length: > 0 })
        {
            if (hasCapturedArtwork)
            {
                hasCapturedArtwork = false;
                SetAccent(Color.FromArgb(255, 85, 214, 190));
            }
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(artworkBytes.AsBuffer());
                stream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(stream);
                var transform = new BitmapTransform { ScaledWidth = 16, ScaledHeight = 16 };
                var pixels = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                byte[] data = pixels.DetachPixelData();

                // Hue-bucketed dominant color with a saturation/value floor so
                // near-gray artwork doesn't produce a dead bar color.
                var buckets = new (double H, double S, double V, double Weight)[24];
                for (int i = 0; i < data.Length; i += 4)
                {
                    byte b = data[i], g = data[i + 1], r = data[i + 2];
                    ColorToHsv(r, g, b, out double h, out double s, out double v);
                    if (s < 0.25 || v < 0.2)
                        continue;
                    int bucket = (int)(h / 360.0 * 24) % 24;
                    buckets[bucket].H += h;
                    buckets[bucket].S += s;
                    buckets[bucket].V += v;
                    buckets[bucket].Weight += 1;
                }

                int best = -1;
                double bestWeight = 0;
                for (int i = 0; i < buckets.Length; i++)
                {
                    if (buckets[i].Weight > bestWeight)
                    {
                        bestWeight = buckets[i].Weight;
                        best = i;
                    }
                }

                if (best >= 0 && bestWeight > 4)
                {
                    double h = buckets[best].H / bestWeight;
                    double s = Math.Min(1, buckets[best].S / bestWeight);
                    double v = Math.Min(1, buckets[best].V / bestWeight + 0.1);
                    ColorFromHsv(h, Math.Clamp(s, 0.45, 0.9), Math.Clamp(v, 0.55, 0.9), out byte rr, out byte gg, out byte bb);
                    SetAccent(Color.FromArgb(255, rr, gg, bb));
                }
            }
            catch
            {
                // Keep the previous accent on decode failure.
            }
        });
    }

    private static void SetAccent(Color color)
    {
        lock (gate)
        {
            if (currentAccent == color)
                return;
            currentAccent = color;
        }
        AccentChanged?.Invoke(null, color);
    }

    private static void ColorToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)) / 255.0;
        double min = Math.Min(r, Math.Min(g, b)) / 255.0;
        double delta = max - min;

        h = 0;
        if (delta > 0)
        {
            if (max == r / 255.0) h = 60 * (((g - b) / 255.0 / delta) % 6);
            else if (max == g / 255.0) h = 60 * (((b - r) / 255.0 / delta) + 2);
            else h = 60 * (((r - g) / 255.0 / delta) + 4);
        }
        if (h < 0) h += 360;

        s = max <= 0 ? 0 : delta / max;
        v = max;
    }

    private static void ColorFromHsv(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double rr, double rg, double rb) = (h % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        r = (byte)Math.Round((rr + m) * 255);
        g = (byte)Math.Round((rg + m) * 255);
        b = (byte)Math.Round((rb + m) * 255);
    }
}
