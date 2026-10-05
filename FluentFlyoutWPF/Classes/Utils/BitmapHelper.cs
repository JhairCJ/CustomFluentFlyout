// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using Windows.Media.Control;
using FluentFlyoutWPF.Classes;

namespace FluentFlyout.Classes.Utils;

internal static class BitmapHelper
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // Bounded, synchronized thumbnail cache: foreground events and async re-reads share it.
    private sealed class LruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry> _lruList = [];
        private readonly object _sync = new();

        private sealed class CacheEntry(TKey key, TValue value)
        {
            public TKey Key { get; } = key;
            public TValue Value { get; set; } = value;
        }

        public LruCache(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

            _capacity = capacity;
            _map = new Dictionary<TKey, LinkedListNode<CacheEntry>>(capacity);
        }

        public bool TryGetValue(TKey key, out TValue? value)
        {
            lock (_sync)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            lock (_sync)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    existing.Value.Value = value;
                    _lruList.Remove(existing);
                    _lruList.AddFirst(existing);
                    return;
                }

                var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, value));
                _lruList.AddFirst(node);
                _map[key] = node;

                if (_map.Count <= _capacity)
                    return;

                var leastRecent = _lruList.Last;
                if (leastRecent == null)
                    return;

                _lruList.RemoveLast();
                _map.Remove(leastRecent.Value.Key);
            }
        }
    }

    private const int _maxThumbnailSize = 256; // previously 512, reduced for application memory
    private const int _cacheEntryLimit = 5;

    // cached thumbnails to prevent reprocessing
    private static readonly LruCache<int, BitmapImage> _thumbnailCache = new(_cacheEntryLimit);

    /// <summary>
    /// Fast non-cryptographic hash (FNV-1a) of raw thumbnail bytes, used for
    /// cache lookup and change detection. Thumbnails are small; the previous
    /// SHA-256 over the full stream was pure overhead on the UI thread.
    /// </summary>
    private static int HashThumbnailBytes(byte[] bytes, long streamLength)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        uint hash = offsetBasis;
        hash ^= (uint)streamLength;
        hash *= prime;

        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= prime;
        }
        return unchecked((int)hash);
    }

    private static byte[] ReadThumbnailBytes(IRandomAccessStreamReference thumbnail, out long streamLength)
    {
        using Stream stream = thumbnail.OpenReadAsync().GetAwaiter().GetResult().AsStreamForRead();
        using var copy = new MemoryStream((int)Math.Min(Math.Max(stream.Length, 0), 4 * 1024 * 1024));
        stream.CopyTo(copy);
        streamLength = stream.Length;
        return copy.ToArray();
    }

    internal static BitmapImage? GetThumbnail(IRandomAccessStreamReference? thumbnail, int maxThumbnailSize = _maxThumbnailSize)
    {
        if (thumbnail == null)
            return null;

        // Single stream open: buffer the bytes once, hash them for the cache lookup,
        // then decode from the same buffer. The previous code opened the thumbnail
        // stream twice per song change (once to hash, once to decode).
        byte[] bytes;
        long streamLength;
        try
        {
            bytes = ReadThumbnailBytes(thumbnail, out streamLength);
        }
        catch (Exception ex)
        {
            Logger.Info(ex, "Failed to read thumbnail stream");
            return null;
        }

        int hashCode = HashThumbnailBytes(bytes, streamLength);

        if (hashCode == 0)
            return null;

        return GetThumbnailFromBytes(bytes, hashCode, maxThumbnailSize);
    }

    private static BitmapImage? GetThumbnailFromBytes(byte[] bytes, int hashCode, int maxThumbnailSize)
    {
        if (_thumbnailCache.TryGetValue(hashCode, out var cachedImage) && cachedImage != null)
        {
            return cachedImage;
        }

        BitmapImage image = new();
        using (var imageStream = new MemoryStream(bytes, writable: false))
        {
            // initialize the BitmapImage
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = maxThumbnailSize;
            image.StreamSource = imageStream;
            image.EndInit();
        }
        image.Freeze();

        _thumbnailCache.Set(hashCode, image);
        return image;
    }

    internal static CroppedBitmap? CropToSquare(BitmapImage? sourceImage)
    {
        if (sourceImage == null)
            return null;

        int size = (int)Math.Min(sourceImage.PixelWidth, sourceImage.PixelHeight);
        int x = (sourceImage.PixelWidth - size) / 2;
        int y = (sourceImage.PixelHeight - size) / 2;

        var rect = new Int32Rect(x, y, size, size);

        // create a CroppedBitmap (this is a lightweight object)
        var croppedBitmap = new CroppedBitmap(sourceImage, rect);

        croppedBitmap.Freeze();
        return croppedBitmap;
    }

    /// <summary>
    /// Current desaturation settings (0-1) for the album accent.
    /// Threshold: saturation below this is left untouched.
    /// Amount: fraction of the excess above threshold to remove.
    /// </summary>
    private static (double Threshold, double Amount) GetDesaturation()
    {
        double threshold = Math.Clamp(SettingsManager.Current.AlbumAccentDesaturationThreshold / 100.0, 0, 1);
        double amount = Math.Clamp(SettingsManager.Current.AlbumAccentDesaturationAmount / 100.0, 0, 1);
        return (threshold, amount);
    }

    public static void GetDominantColors(BitmapImage? artwork)
    {
        var (threshold, amount) = GetDesaturation();
        AlbumAccent.Refresh(artwork, artwork == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(artwork),
            SettingsManager.Current.UseAlbumArtAsAccentColor, AlbumAccent.IsDarkTheme(), threshold, amount);
    }

    public static void RefreshAccentTheme()
    {
        AlbumAccent.RefreshTaskbarTheme();
        var (threshold, amount) = GetDesaturation();
        AlbumAccent.RefreshTheme(AlbumAccent.IsDarkTheme(), threshold, amount, SettingsManager.Current.UseAlbumArtAsAccentColor);
    }

    internal static async Task<MediaArtworkSnapshot?> ReadMediaArtworkAsync(MediaArtworkSnapshot pending)
    {
        if (pending.Session is not GlobalSystemMediaTransportControlsSession session) return null;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null) return null;
            var art = await Task.Run(() => GetThumbnail(props.Thumbnail));
            return new MediaArtworkSnapshot(session, props.Title ?? "", props.Artist ?? "", art);
        }
        catch (Exception) { return null; }
    }
}
