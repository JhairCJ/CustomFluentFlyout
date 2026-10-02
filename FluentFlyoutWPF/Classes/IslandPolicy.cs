// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>What to do when a temporary notice's deadline expires.</summary>
public enum IslandNoticeStep
{
    /// <summary>The notice no longer belongs to this view (another one took over or there is an exclusive one): it is forgotten without touching the view.</summary>
    Forget,

    /// <summary>The notice is still in force but cannot be withdrawn now (expanded or under the cursor): it is retried.</summary>
    Retry,

    /// <summary>The notice is withdrawn: its view goes back to rest.</summary>
    Retract,
}

/// <summary>
/// The Island temporary notice POLICY, as pure and testable decisions. The same
/// condition used to be written three times in two different forms - on arming and on
/// expiry - and from that came the notices that stayed stuck or that appeared without
/// anything having happened.
/// </summary>
public static class IslandNoticePolicy
{
    /// <summary>
    /// Is the deadline armed? A FORCED notice (Bluetooth, charger) expires even in
    /// "Visible while active" mode, because its view is a notification and does not
    /// stick around; a normal one only lives in "Temporary notice". A live exclusive
    /// overrides everything (001 RF-2, 002 RF-16).
    /// </summary>
    public static bool Arms(bool forced, bool temporalMode, bool hasExclusive) =>
        !hasExclusive && (forced || temporalMode);

    /// <summary>
    /// What to do when the deadline expires (or when repairing a missed trigger): the
    /// notice is not closed under the cursor nor with the view expanded - it is retried
    /// until the view is compact again and the mouse is out of the way, so it never
    /// stays stuck.
    /// </summary>
    public static IslandNoticeStep OnExpired(bool forced, bool temporalMode, bool hasExclusive,
        bool boxShown, bool expanded, bool pointerOver)
    {
        if (hasExclusive || (!forced && !temporalMode)) return IslandNoticeStep.Forget;
        if (!boxShown) return IslandNoticeStep.Forget;
        if (expanded || pointerOver) return IslandNoticeStep.Retry;
        return IslandNoticeStep.Retract;
    }
}

/// <summary>Candidate to be the container's "current active item".</summary>
public readonly record struct IslandActivityCandidate(bool Active, bool Usable, DateTime LastEvent);

/// <summary>
/// The current activity POLICY: who deserves the compact view when several features
/// are active (001 MOD RF-4/RF-12). The most recent EVENT wins and, on a tie (same
/// instant or no event recorded), the FIRST one in screen order. It is an explicit
/// tie-break: it does not depend on the order an unstable sort returns, nor on any
/// separate ordering list.
/// </summary>
public static class IslandActivityPick
{
    /// <summary>Index of the winner, or -1 if no candidate is active and usable.</summary>
    public static int Winner(IReadOnlyList<IslandActivityCandidate> candidates)
    {
        int best = -1;
        DateTime bestWhen = DateTime.MinValue;
        for (int index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (!candidate.Active || !candidate.Usable) continue;
            // Strictly more recent: on an event tie the first one stays, which is the
            // one in screen order.
            if (best < 0 || candidate.LastEvent > bestWhen)
            {
                best = index;
                bestWhen = candidate.LastEvent;
            }
        }
        return best;
    }
}
