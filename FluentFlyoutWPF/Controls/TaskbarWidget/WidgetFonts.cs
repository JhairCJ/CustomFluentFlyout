// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Media;

namespace FluentFlyout.Controls.TaskbarWidget;

/// <summary>
/// Typefaces bundled with the app for the taskbar widget (see
/// <c>Resources/Fonts</c>, each with its license file alongside: mostly SIL
/// Open Font License, plus Apache 2.0 for Special Elite and the Ubuntu Font
/// Licence for Ubuntu Mono). They are embedded as assembly resources, so they
/// render identically on any PC even when not installed. Anything not listed
/// here is treated as a system font name.
/// </summary>
internal static class WidgetFonts
{
    private const string FallbackName = "Segoe UI Variable";

    private static readonly Uri BaseUri = new("pack://application:,,,/FluentFlyout;component/Resources/Fonts/");

    /// <summary>
    /// Display name (as shown in settings) to the internal font family fragment
    /// used in the pack URI. Some binaries carry subsetting quirks in their name
    /// table (e.g. "Montserrat Thin"); the mapping hides that from users while
    /// keeping the shipped binaries byte-identical to upstream.
    /// </summary>
    private static readonly Dictionary<string, string> BundledFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Inter"] = "Inter",
        ["Manrope"] = "Manrope ExtraLight",
        ["Poppins"] = "Poppins",
        ["Montserrat"] = "Montserrat Thin",
        ["Nunito"] = "Nunito ExtraLight",
        ["Space Grotesk"] = "Space Grotesk Light",
        ["Rubik"] = "Rubik Light",
        ["Quicksand"] = "Quicksand Light",
        ["Comfortaa"] = "Comfortaa",
        ["Caveat"] = "Caveat",
        ["Baloo 2"] = "Baloo 2",
        ["Fredoka"] = "Fredoka Light",
        ["Pacifico"] = "Pacifico",
        ["Permanent Marker"] = "Permanent Marker",
        ["Playfair Display"] = "Playfair Display",
        ["DM Serif Display"] = "DM Serif Display",
        ["Lora"] = "Lora",
        ["Zilla Slab"] = "Zilla Slab",
        ["Fira Code"] = "Fira Code Light",
        ["JetBrains Mono"] = "JetBrains Mono",
        ["Space Mono"] = "Space Mono",
    };

    /// <summary>Display names of the bundled fonts, in settings order.</summary>
    public static IEnumerable<string> BundledNames => BundledFamilies.Keys;

    /// <summary>
    /// Resolves a widget font setting value to a usable <see cref="FontFamily"/>:
    /// bundled display names become pack URIs, anything else is passed through
    /// as a system font name, with a safe fallback.
    /// </summary>
    public static FontFamily Resolve(string? name)
    {
        string clean = string.IsNullOrWhiteSpace(name) ? FallbackName : name.Trim();
        if (BundledFamilies.TryGetValue(clean, out string? fragment))
        {
            // NOTE: the two-arg ctor is required here. The single-string form
            // ("pack://...#Family") silently fails to bind to the resource font
            // (TryGetGlyphTypeface returns false and WPF falls back to Segoe).
            return new FontFamily(BaseUri, "./#" + fragment);
        }
        try
        {
            return new FontFamily(clean);
        }
        catch
        {
            return new FontFamily(FallbackName);
        }
    }
}
