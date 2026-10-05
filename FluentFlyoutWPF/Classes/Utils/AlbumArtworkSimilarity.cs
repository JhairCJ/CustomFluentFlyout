// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FluentFlyout.Classes.Utils;

internal enum AlbumArtworkTransition { Direct, Fade, Flip }

/// <summary>Small visual fingerprints, independent of thumbnail encoding and resolution.</summary>
internal static class AlbumArtworkSimilarity
{
    private sealed record Fingerprint(ulong Structure, byte[] Colors);
    private static readonly ConditionalWeakTable<BitmapSource, Fingerprint> Cache = new();

    public static AlbumArtworkTransition ChooseTransition(BitmapSource? visible, BitmapSource? incoming,
        bool hasPresentedArtwork, bool preferFade)
    {
        if (!hasPresentedArtwork || AreSimilar(visible, incoming)) return AlbumArtworkTransition.Direct;
        if (visible == null || incoming == null || preferFade) return AlbumArtworkTransition.Fade;
        return AlbumArtworkTransition.Flip;
    }

    public static bool AreSimilar(BitmapSource? first, BitmapSource? second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first == null || second == null) return false;
        try
        {
            var a = Cache.GetValue(first, Create);
            var b = Cache.GetValue(second, Create);
            if (BitOperations.PopCount(a.Structure ^ b.Structure) > 6) return false;
            long error = 0;
            for (int i = 0; i < a.Colors.Length; i++) error += Math.Abs(a.Colors[i] - b.Colors[i]);
            return error <= a.Colors.Length * 255 * 0.06;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // An unreadable image cannot establish visual equality.
            return false;
        }
    }

    private static Fingerprint Create(BitmapSource source)
    {
        // WPF's resampler avoids decoding or comparing full-resolution images again.
        var scaled = new TransformedBitmap(source, new ScaleTransform(9.0 / source.PixelWidth, 8.0 / source.PixelHeight));
        var rgb = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[9 * 8 * 4];
        rgb.CopyPixels(new System.Windows.Int32Rect(0, 0, 9, 8), pixels, 9 * 4, 0);
        byte[] colors = new byte[9 * 8 * 3];
        for (int i = 0; i < 72; i++)
        {
            // Composite transparency over black, matching the album surfaces.
            for (int c = 0; c < 3; c++) colors[i * 3 + c] = (byte)(pixels[i * 4 + c] * pixels[i * 4 + 3] / 255);
        }
        static int Luma(byte[] values, int p) => values[p * 3] * 114 + values[p * 3 + 1] * 587 + values[p * 3 + 2] * 299;
        ulong structure = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                // Ignore four luminance levels of codec noise in otherwise flat regions.
                if (Luma(colors, y * 9 + x) - Luma(colors, y * 9 + x + 1) > 4000) structure |= 1UL << (y * 8 + x);
        return new Fingerprint(structure, colors);
    }
}
