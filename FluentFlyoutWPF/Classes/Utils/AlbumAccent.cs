// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;

namespace FluentFlyout.Classes.Utils;

/// <summary>
/// Single source of truth for the album-derived accent color.
/// Holds the raw album color (theme-independent) plus a frozen brush
/// already tuned for the current theme. One accent for every consumer
/// (play button, placeholders, visualizer).
/// </summary>
internal static class AlbumAccent
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static Color? _raw;
    private static SolidColorBrush? _brush;
    private static int _sourceHash;
    private static double _lastThreshold = 0.65;
    private static double _lastAmount;

    public static SolidColorBrush Brush => _brush ??= SystemFallback();

    public static int SourceHash => _sourceHash;

    public static bool HasAlbumColor => _raw.HasValue;

    /// <summary>
    /// Recomputes the accent from a thumbnail, or returns the cached brush
    /// when the hash matches. Falls back to the system accent when album
    /// art must not be used or yields no usable color.
    /// </summary>
    /// <param name="threshold01">Saturation threshold (0-1) below which colors are left untouched.</param>
    /// <param name="amount01">How much of the saturation excess above the threshold to remove (0-1).</param>
    public static SolidColorBrush Refresh(BitmapSource? thumbnail, int hash, bool useAlbumArt, bool isDark, double threshold01 = 0.65, double amount01 = 0)
    {
        if (!useAlbumArt || thumbnail == null || hash == 0)
        {
            _raw = null;
            _sourceHash = 0;
            _lastThreshold = threshold01;
            _lastAmount = amount01;
            _brush = SystemFallback();
            return _brush;
        }

        // Same artwork: return cached brush unless theme/desaturation params changed,
        // in which case just re-derive from the cached raw color (no pixel scan).
        if (hash == _sourceHash && _brush != null && _raw.HasValue
            && threshold01 == _lastThreshold && amount01 == _lastAmount)
            return _brush;

        if (hash == _sourceHash && _raw.HasValue)
        {
            _lastThreshold = threshold01;
            _lastAmount = amount01;
            _brush = Freeze(new SolidColorBrush(ToThemed(_raw.Value, isDark, threshold01, amount01)));
            return _brush;
        }

        Color? raw;
        try
        {
            raw = Extract(thumbnail);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error extracting album accent color");
            raw = null;
        }

        _sourceHash = hash;
        if (raw == null)
        {
            _raw = null;
            _lastThreshold = threshold01;
            _lastAmount = amount01;
            _brush = SystemFallback();
            return _brush;
        }

        _raw = raw.Value;
        _lastThreshold = threshold01;
        _lastAmount = amount01;
        _brush = Freeze(new SolidColorBrush(ToThemed(raw.Value, isDark, threshold01, amount01)));
        return _brush;
    }

    /// <summary>
    /// Re-derives the themed brush from the cached raw color without
    /// re-scanning pixels. Call on theme changes and setting toggles.
    /// </summary>
    public static SolidColorBrush RefreshTheme(bool isDark, double threshold01 = 0.65, double amount01 = 0)
    {
        if (_raw == null)
        {
            _sourceHash = 0;
            _lastThreshold = threshold01;
            _lastAmount = amount01;
            _brush = SystemFallback();
            return _brush;
        }

        _lastThreshold = threshold01;
        _lastAmount = amount01;
        _brush = Freeze(new SolidColorBrush(ToThemed(_raw.Value, isDark, threshold01, amount01)));
        return _brush;
    }

    public static bool IsDarkTheme()
    {
        try
        {
            var appTheme = ApplicationThemeManager.GetAppTheme();
            if (appTheme == ApplicationTheme.Dark)
                return true;
            if (appTheme == ApplicationTheme.Light)
                return false;
            return ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Extracts the representative album color: a coverage-weighted 5-bit
    /// histogram vote, averaged over the winning bin. Deterministic, no
    /// randomness. Neutral (gray) covers vote gray instead of being
    /// discarded, so the result keeps the album's tint.
    /// </summary>
    /// <returns>The raw album color, or null when nothing usable was found.</returns>
    public static Color? Extract(BitmapSource source)
    {
        var formatted = new FormatConvertedBitmap();
        formatted.BeginInit();
        formatted.Source = source;
        formatted.DestinationFormat = PixelFormats.Bgra32;
        formatted.EndInit();

        int width = formatted.PixelWidth;
        int height = formatted.PixelHeight;
        if (width <= 0 || height <= 0)
            return null;

        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        formatted.CopyPixels(pixels, stride, 0);

        const int bins = 32; // 5 bits per channel
        const int shift = 3; // 8 - 5
        float[] weights = new float[bins * bins * bins];

        // Pass 1: weighted vote. Step of 4 pixels (~6% of a 256px thumb) is
        // plenty for a dominant tone and keeps this under a millisecond.
        double totalWeight = 0;
        for (int i = 0; i < pixels.Length; i += 16)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            byte a = pixels[i + 3];

            if (a < 128)
                continue;

            float rf = r / 255f;
            float gf = g / 255f;
            float bf = b / 255f;

            float max = MathF.Max(rf, MathF.Max(gf, bf));
            float min = MathF.Min(rf, MathF.Min(gf, bf));
            float lightness = (max + min) / 2f;

            // ignore pure black / pure white pixels, keep everything else
            if (lightness < 0.06f || lightness > 0.94f)
                continue;

            float chroma = max - min;
            float weight = 1f + chroma * 2f;
            if (chroma < 0.06f)
                weight *= 0.25f; // neutrals vote, but lose against real color

            int idx = ((r >> shift) * bins + (g >> shift)) * bins + (b >> shift);
            weights[idx] += weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0)
            return null;

        int winner = 0;
        for (int i = 1; i < weights.Length; i++)
        {
            if (weights[i] > weights[winner])
                winner = i;
        }

        // Pass 2: average the actual pixels inside the winning bin so the
        // result is the true tone, not a quantized bin center.
        int wb = winner % bins;
        int wg = (winner / bins) % bins;
        int wr = winner / (bins * bins);

        long sumR = 0, sumG = 0, sumB = 0, count = 0;
        for (int i = 0; i < pixels.Length; i += 16)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            byte a = pixels[i + 3];

            if (a < 128)
                continue;
            if ((r >> shift) != wr || (g >> shift) != wg || (b >> shift) != wb)
                continue;

            sumR += r;
            sumG += g;
            sumB += b;
            count++;
        }

        if (count == 0)
            return null;

        return Color.FromRgb((byte)(sumR / count), (byte)(sumG / count), (byte)(sumB / count));
    }

    /// <summary>
    /// Tunes a raw album color for visibility on the flyout background while
    /// preserving its hue. Clamps lightness into a theme-appropriate band and
    /// guarantees a minimum saturation for chromatic colors; neutral grays
    /// keep their tint instead of being pushed toward white.
    /// Finally, saturation above <paramref name="threshold01"/> is compressed
    /// toward the threshold by <paramref name="amount01"/>:
    /// S' = t + (S - t) * (1 - d), so intense album colors are tamed while
    /// soft ones are left untouched. With amount 0 this is the identity.
    /// Desaturating scales saturation toward gray without changing hue or
    /// brightness: at 100% any color becomes a neutral gray of the same
    /// lightness.
    /// </summary>
    public static Color ToThemed(Color raw, bool isDark, double threshold01 = 0.65, double amount01 = 0)
    {
        RgbToHsl(raw.R, raw.G, raw.B, out double h, out double s, out double l);

        if (isDark)
        {
            l = Math.Clamp(l, 0.60, 0.72);
            if (s > 0.08)
                s = Math.Max(s, 0.38);
        }
        else
        {
            l = Math.Clamp(l, 0.42, 0.55);
            if (s > 0.08)
                s = Math.Max(s, 0.50);
        }

        double t = Math.Clamp(threshold01, 0, 1);
        double d = Math.Clamp(amount01, 0, 1);
        if (d > 0 && s > t)
            s = t + (s - t) * (1 - d);

        HslToRgb(h, s, l, out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    private static SolidColorBrush SystemFallback()
    {
        try
        {
            if (Application.Current?.TryFindResource("MicaWPF.Brushes.SystemAccentColorSecondary") is SolidColorBrush accent)
            {
                if (!accent.IsFrozen)
                    accent = accent.Clone();
                accent.Freeze();
                return accent;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "System accent resource unavailable; using neutral fallback");
        }

        var neutral = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        neutral.Freeze();
        return neutral;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static void RgbToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
    {
        double rf = r / 255.0;
        double gf = g / 255.0;
        double bf = b / 255.0;

        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        l = (max + min) / 2.0;

        if (max == min)
        {
            h = 0;
            s = 0;
            return;
        }

        double delta = max - min;
        s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

        if (max == rf)
            h = ((gf - bf) / delta + (gf < bf ? 6 : 0)) / 6.0;
        else if (max == gf)
            h = ((bf - rf) / delta + 2) / 6.0;
        else
            h = ((rf - gf) / delta + 4) / 6.0;
    }

    private static void HslToRgb(double h, double s, double l, out byte r, out byte g, out byte b)
    {
        double rf, gf, bf;

        if (s == 0)
        {
            rf = gf = bf = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            rf = HueToRgb(p, q, h + 1.0 / 3.0);
            gf = HueToRgb(p, q, h);
            bf = HueToRgb(p, q, h - 1.0 / 3.0);
        }

        r = (byte)Math.Clamp(Math.Round(rf * 255), 0, 255);
        g = (byte)Math.Clamp(Math.Round(gf * 255), 0, 255);
        b = (byte)Math.Clamp(Math.Round(bf * 255), 0, 255);
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}
