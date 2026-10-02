// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Models;
using System.Globalization;
using System.Windows;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Single point for the Island's UI text in the WPF layer.
///
/// <para>It resolves a localization dictionary key with an English fallback, so an
/// untranslated language - or an incomplete dictionary - never leaves the text blank:
/// the <c>en-US</c> dictionary is ALWAYS loaded as the base (<c>LocalizationManager</c>)
/// and only the chosen language is stacked on top.</para>
///
/// <para>XAML does not need to come through here: it uses <c>{DynamicResource Key}</c>,
/// which also refreshes by itself when the language changes. This helper exists for the
/// text written from code (tooltips, states, error messages).</para>
/// </summary>
public static class IslandStrings
{
    /// <summary>
    /// Localized text for <paramref name="key"/>, or <paramref name="fallback"/> (English)
    /// if it does not exist. A resolution failure returns the fallback instead of
    /// letting the exception escape: the Bluetooth watchdog and the calendar loop write
    /// from background threads, where touching WPF resources can throw on thread affinity.
    /// </summary>
    public static string Get(string key, string fallback)
    {
        try
        {
            return Application.Current?.TryFindResource(key)?.ToString() is { Length: > 0 } text
                ? text
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Localized text with formatting (uses the current culture).</summary>
    public static string Format(string key, string fallback, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key, fallback), args);

    /// <summary>Display name of a feature (key declared in <see cref="IslandFeatureIds.DisplayNameKey"/>).</summary>
    public static string FeatureName(string? id) =>
        Get(IslandFeatureIds.DisplayNameKey(id), id ?? "");
}
