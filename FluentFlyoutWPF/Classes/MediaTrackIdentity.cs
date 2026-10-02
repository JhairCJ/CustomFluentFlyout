// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Identity of the last PRESENTED song (title + artist) and track change detector. It
/// is pure logic - no sessions, no UI - which is why it can be tested on its own.
///
/// <para>Rules it holds (001 MOD RF-1):</para>
/// <list type="bullet">
/// <item>An event with no TITLE flags nothing: players that blank it for an instant
/// between tracks must not count as a song change. A known artist is kept if the
/// event arrives without it.</item>
/// <item>The title wins: a different name is a different song.</item>
/// <item>The artist only counts when BOTH sides provide it, so a player that fills the
/// artist in late is not mistaken for a song change.</item>
/// </list>
/// </summary>
public sealed class MediaTrackIdentity
{
    /// <summary>Recorded title (empty = no song known yet).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Recorded artist (empty = unknown).</summary>
    public string Artist { get; private set; } = string.Empty;

    /// <summary>
    /// Records what the player announces and answers whether it is a NEW song.
    /// </summary>
    public bool Observe(string? title, string? artist)
    {
        string t = (title ?? string.Empty).Trim();
        string a = (artist ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            if (a.Length > 0) Artist = a;
            return false;
        }
        bool changed = Title.Length > 0
            && (!string.Equals(Title, t, StringComparison.Ordinal)
                || (Artist.Length > 0 && a.Length > 0
                    && !string.Equals(Artist, a, StringComparison.Ordinal)));
        Title = t;
        if (a.Length > 0) Artist = a;
        return changed;
    }
}
