// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Models;

namespace FluentFlyoutWPF.Classes;

/// <summary>View the container must show RIGHT NOW (change island-lista-de-activos).</summary>
public enum IslandDesiredView
{
    /// <summary>Nothing: neither piece nor box (suppressed, or rest without piece).</summary>
    Hidden,

    /// <summary>The inactive piece (narrow dark square).</summary>
    Inactive,

    /// <summary>A feature's compact view (the active item, in the same turn).</summary>
    Compact,

    /// <summary>The expanded view of a feature or of its screen.</summary>
    Expanded,
}

/// <summary>
/// Everything the policy needs to know to decide the view. It is a snapshot: the
/// policy queries nothing on its own (no settings, no media system, no Windows), so it
/// is pure and testable.
/// </summary>
public readonly record struct IslandPresentationInput(
    bool Suppressed,
    bool ReturnToInactive,
    bool UserExpanded,
    string? UserExpandedFeatureId,
    bool AnyScreenUsable,
    string? PresentedNoticeId,
    DateTime PresentedNoticeStarted,
    IReadOnlyList<string> ScreenOrderedIds,
    /// <summary>Features that HAVE their own expanded view: the ones of a screen. An
    /// exclusive without expanded view (dictation) lives in the compact view, so it is
    /// not expanded.</summary>
    IReadOnlyList<string> ExpandableIds,
    IReadOnlyList<string> UsableIds,
    IslandActivityRegistry Activity,
    DateTime Now);

/// <summary>Desired view, with the feature holding it and the notice deadline.</summary>
public readonly record struct IslandPresentationResult(
    IslandDesiredView View,
    string? FeatureId,
    bool RestartNotice,
    bool AlwaysTemporal);

/// <summary>
/// The Island presentation POLICY (change island-lista-de-activos): it translates the
/// LIST OF ACTIVE EVENTS into ONE desired view, in a single place and without per-mode
/// branches scattered through half the container.
///
/// <para>Rules, in this order:</para>
/// <list type="number">
/// <item>Live exclusive access -> its expanded view; it overrides everything and goes
/// through suppression (001 RF-8/14, 002 RF-2, spec 006 RF-9).</item>
/// <item>Contextual suppression -> only a pending dictation notice, otherwise nothing.</item>
/// <item>The expanded view the user has open -> respected: an ordinary event does not
/// take away the view they decided to open (001 RF-24).</item>
/// <item>Live activity -> the COMPACT view of the winner: the most recent event and, on
/// a tie, the first in screen order (001 MOD RF-4/RF-12). A new notice premieres its
/// deadline; re-presenting the same one does not restart it (001 RF-2).</item>
/// <item>No activity -> rest: the inactive piece if the setting asks for it and there is
/// something usable to open; otherwise nothing (001 MOD RF-2).</item>
/// </list>
///
/// <para>These rules used to be written six times - in the event-driven activity, in the
/// fold-backs, in the notice and in the settings - each with its own guards: from that
/// came the compact views that took long to appear and the ones that never appeared.</para>
/// </summary>
public static class IslandPresentation
{
    /// <summary>View to show given this state.</summary>
    public static IslandPresentationResult Resolve(IslandPresentationInput input)
    {
        // 1. The exclusive wins: the timer alert (it has an expanded view) and dictation
        // in progress (its card lives in the compact view, outside the screens).
        if (input.Activity.ExclusiveId(input.Now) is { } exclusive && IsUsable(input, exclusive))
        {
            bool expandable = input.ExpandableIds.Contains(exclusive);
            return new IslandPresentationResult(
                expandable ? IslandDesiredView.Expanded : IslandDesiredView.Compact,
                exclusive, RestartNotice: !expandable, AlwaysTemporal: !expandable);
        }

        // 2. Dictation errors remain visible after the session releases exclusive access.
        // Other notices and rest still respect contextual suppression.
        if (input.Suppressed)
        {
            var feedback = input.Activity.Live(IslandFeatureIds.Dictation, input.Now);
            if (feedback is { Kind: IslandActivityKind.Notice } notice && IsUsable(input, notice.Id))
                return new IslandPresentationResult(IslandDesiredView.Compact, notice.Id,
                    input.PresentedNoticeId != notice.Id || input.PresentedNoticeStarted != notice.StartedUtc,
                    notice.AlwaysTemporal);
            return new IslandPresentationResult(IslandDesiredView.Hidden, null, false, false);
        }

        // 3. The expanded view the user opened is respected while nothing exclusive takes it.
        if (input.UserExpanded)
            return new IslandPresentationResult(IslandDesiredView.Expanded, input.UserExpandedFeatureId, false, false);

        // 4. The active list: the most recent event wins, ties broken by screen order.
        var winner = Winner(input);
        if (winner is { } entry)
        {
            bool notice = entry.Kind == IslandActivityKind.Notice;
            bool restart = notice
                && (input.PresentedNoticeId != entry.Id || input.PresentedNoticeStarted != entry.StartedUtc);
            return new IslandPresentationResult(IslandDesiredView.Compact, entry.Id, restart,
                notice && entry.AlwaysTemporal);
        }

        // 5. Rest: the inactive piece or nothing.
        bool piece = input.ReturnToInactive && input.AnyScreenUsable;
        return new IslandPresentationResult(piece ? IslandDesiredView.Inactive : IslandDesiredView.Hidden, null, false, false);
    }

    /// <summary>
    /// Winning entry: the most recent of those that are alive AND usable, with the
    /// candidates in SCREEN ORDER - which is the tie-break - and the choice made in the
    /// usual pure policy (<see cref="IslandActivityPick"/>).
    /// </summary>
    private static IslandActivityEntry? Winner(IslandPresentationInput input)
    {
        var candidates = new List<IslandActivityCandidate>(input.ScreenOrderedIds.Count);
        var entries = new List<IslandActivityEntry?>(input.ScreenOrderedIds.Count);
        foreach (string id in input.ScreenOrderedIds)
        {
            var entry = input.Activity.Live(id, input.Now);
            entries.Add(entry);
            bool usable = IsUsable(input, id);
            candidates.Add(new IslandActivityCandidate(entry != null, usable,
                entry?.StartedUtc ?? DateTime.MinValue));
        }
        int index = IslandActivityPick.Winner(candidates);
        return index < 0 ? null : entries[index];
    }

    /// <summary>Can this feature be opened right now? (enabled and available).</summary>
    private static bool IsUsable(IslandPresentationInput input, string id) => input.UsableIds.Contains(id);
}
