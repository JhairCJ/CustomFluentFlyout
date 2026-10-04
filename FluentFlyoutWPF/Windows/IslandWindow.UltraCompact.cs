// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Windows;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// ULTRA COMPACT MODE: with the capsule folded back only its TWO EXTREMES are
/// seen - what goes on the left and what goes on the right - and the gap in the middle
/// is left empty. In the media controls that leaves, for example, the artwork and the
/// equalizer: what identifies the content and what can be operated, with no title or
/// artist.
///
/// <para>It is the setting meant to coexist with "Return to inactive" OFF: the Island
/// does not live on screen, it only appears when there is something to show, and its
/// resting presence is minimal. Since in that case there is neither piece nor capsule
/// to point at, the top strip - from the activity line to the top edge - is the door
/// that brings it back expanded.</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// Compact view width in ultra mode: two extremes and nothing else. It is measured
    /// on the widest pair (the timer icon on the left and its digits on the right),
    /// which takes about 107 DIPs with the XAML margins and spacing.
    /// </summary>
    private const double UltraCompactWidth = 120;

    /// <summary>Is ultra compact mode on?</summary>
    private static bool UltraCompactOn => SettingsManager.Current.IslandUltraCompact;

    /// <summary>
    /// Current compact view width with ultra mode in front: it overrides both the
    /// feature's width and the style's (notch included), because in ultra the capsule
    /// is defined by its two extremes.
    /// </summary>
    private static double RestCompactWidth(double styleWidth) =>
        UltraCompactOn ? UltraCompactWidth : styleWidth;

    /// <summary>
    /// How many cells fit in a compact row (tray, shelf, clipboard): ultra mode
    /// leaves ONE, and the rest are counted with its "+N", which is the right
    /// extreme.
    /// </summary>
    private static int CompactRowFit(int fullFit) => UltraCompactOn ? 1 : fullFit;

    /// <summary>
    /// Applies ultra mode to the current compact view: it moves the middle out of the
    /// view in front or returns it to its own rule - which the feature itself declares
    /// on its card, because the timer progress depends on its setting while the rest
    /// are always visible. <paramref name="refitRows"/> also recounts the rows of a
    /// cell; it is only needed when the setting CHANGES, because every content refresh
    /// already accounts for the mode (<see cref="CompactRowFit"/>).
    /// </summary>
    private void ApplyUltraCompactContent(bool refitRows = false)
    {
        RefreshDictationStatusLayout();
        foreach (var card in _featureCards.Values)
        {
            if (card.Middle is not { } middle) continue;
            middle.Visibility = UltraCompactOn && card.Mode == _contentMode
                && !(card.Mode == IslandContentMode.Dictation && (DictationTranscribing || DictationHandsFree))
                ? Visibility.Collapsed
                : card.RestoreMiddle?.Invoke() ?? Visibility.Visible;
        }
        if (!refitRows) return;
        RefreshAppList();
        RefreshShelfList();
        RefreshClipboardViews();
    }

    /// <summary>
    /// Should the pointer bring the Island back from the top? Only in ultra mode with
    /// the Island fully HIDDEN: with no piece and no capsule there is nothing to point
    /// at, so the top strip is the only door left.
    /// </summary>
    private bool UltraCompactPullsFromTop =>
        UltraCompactOn && !IsBoxShown && !Suppressed();

    /// <summary>
    /// Hot change of the ultra mode setting: it moves (or returns) the middle of the
    /// current view, recounts the rows and re-measures the capsule with the new width.
    /// </summary>
    public void RefreshUltraCompact() => Dispatcher.Invoke(() =>
    {
        ApplyUltraCompactContent(refitRows: true);
        ApplyContentVisibility();
        RefreshAppearance();
    });
}
