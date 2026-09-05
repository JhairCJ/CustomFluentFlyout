// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Windows;
using System.Windows.Media;

namespace FluentFlyout.Classes.Utils
{
    internal static class StringWidth
    {
        private static FontFamily? fontFamily;
        private static string cachedFontFamily = string.Empty;
        private static Dictionary<string, Typeface> _cachedTypefaces = new();

        // Measured-width cache: CalculateSize runs on every reposition tick and every
        // metadata event, and each call used to build Throwaway FormattedText instances
        // for title + artist (+ spacer + ghost). Widths only depend on
        // (font family, weight, size, text), so memoize them. Bounded with a simple
        // clear-on-overflow; song titles are a small working set in practice.
        private static readonly Dictionary<string, double> _widthCache = new();
        private static readonly object _widthCacheSync = new();
        private const int WidthCacheLimit = 512;

        /// <summary>
        /// Gets the width of the specified string when rendered with the specified font weight.
        /// </summary>
        /// <param name="text">Text to measure</param>
        /// <param name="fontWeight">Weight of the font. Defaults to 500 (Medium)</param>
        /// <param name="fontSize">Size of the font in device-independent units (pixels). Defaults to 14.</param>
        /// <returns>The width of the specified text, in device-independent units (pixels), including a small padding.</returns>
        public static double GetStringWidth(string? text, int fontWeight = 500, int fontSize = 14)
        {
            string currentFontFamily = SettingsManager.Current.FontFamily ?? "Segoe UI Variable, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic";
            return GetStringWidth(text, currentFontFamily, fontWeight, fontSize);
        }

        /// <summary>
        /// Gets the width of the specified string rendered with an explicit font family,
        /// weight and size. Used by the taskbar widget, which has its own typeface
        /// settings independent from the global <c>FontFamily</c>.
        /// </summary>
        public static double GetStringWidth(string? text, string fontFamilyName, int fontWeight = 500, int fontSize = 14)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            string family = string.IsNullOrWhiteSpace(fontFamilyName)
                ? "Segoe UI Variable"
                : fontFamilyName;
            return GetStringWidth(text, new FontFamily(family), fontWeight, fontSize);
        }

        /// <summary>
        /// Gets the width of the specified string rendered with an explicit
        /// <see cref="FontFamily"/> (which may point at an embedded resource via a
        /// pack URI), weight and size.
        /// </summary>
        public static double GetStringWidth(string? text, FontFamily family, int fontWeight = 500, int fontSize = 14)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            string cacheKey = family.Source + "|" + fontWeight + "|" + fontSize + "|" + text;
            lock (_widthCacheSync)
            {
                if (_widthCache.TryGetValue(cacheKey, out double cached))
                    return cached;
            }

            var formattedText = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                GetTypeface(family, fontWeight),
                fontSize,
                Brushes.Black,
                null,
                1);

            double width = formattedText.Width;
            lock (_widthCacheSync)
            {
                if (_widthCache.Count >= WidthCacheLimit)
                    _widthCache.Clear();
                _widthCache[cacheKey] = width;
            }

            return width;
        }

        // Returns the current Typeface based on the font family and weight, caching it for performance.
        private static Typeface GetCurrentTypeface(int fontWeight)
        {
            string currentFontFamily = SettingsManager.Current.FontFamily ?? "Segoe UI Variable, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic";

            // determine if the font family has changed since the last call, and if so, update the cached font family
            if (fontFamily == null || cachedFontFamily != currentFontFamily)
            {
                // get the current font family from user settings or use the default one
                fontFamily = new FontFamily(currentFontFamily);
                cachedFontFamily = currentFontFamily;
                // Measured widths depend on the family: previously cached values are stale.
                lock (_widthCacheSync)
                {
                    _widthCache.Clear();
                }
            }

            return GetTypeface(currentFontFamily, fontWeight);
        }

        private static Typeface GetTypeface(string familyName, int fontWeight)
        {
            return GetTypeface(new FontFamily(familyName), fontWeight);
        }

        private static Typeface GetTypeface(FontFamily family, int fontWeight)
        {
            string key = family.Source + fontWeight;

            _cachedTypefaces.TryGetValue(key, out var cachedTypeface);

            // if exists, return the cached typeface. Otherwise, create a new one and cache it.
            if (cachedTypeface != null)
            {
                return cachedTypeface;
            }
            else
            {
                var newTypeface = new Typeface(family, new FontStyle(), ToFontWeight(fontWeight), FontStretches.Normal);
                _cachedTypefaces[key] = newTypeface;
                return newTypeface;
            }
        }

        private static FontWeight ToFontWeight(int weight) => weight switch
        {
            >= 700 => FontWeights.Bold,
            >= 600 => FontWeights.SemiBold,
            >= 500 => FontWeights.Medium,
            _ => FontWeights.Normal,
        };
    }
}