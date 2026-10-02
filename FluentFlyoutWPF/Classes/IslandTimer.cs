// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// State of the single timer countdown (spec 002: only one at a time).
/// </summary>
public enum IslandTimerState
{
    Idle,
    Running,
    Paused,
    Alerting,
}

/// <summary>
/// Engine of the single countdown. It stores the target time plus the frozen
/// remainder (plan 002 D1) to stay exact even while the Island is hidden or another
/// feature takes over. No threads or timers of its own: the host polls it with
/// <see cref="Poll"/> from its UI tick.
/// </summary>
public sealed class IslandTimer
{
    public const int MaxDurationSeconds = 24 * 3600;
    public const int MinDurationSeconds = 1;
    public const int MaxPresets = 10;

    public IslandTimerState State { get; private set; } = IslandTimerState.Idle;
    public TimeSpan Configured { get; private set; } = TimeSpan.Zero;

    /// <summary>
    /// Notice label: "Timer" if it came from free time, the preset name otherwise.
    /// </summary>
    public string OriginLabel { get; private set; } = DefaultOriginLabel;

    /// <summary>Default notice label (free time), localized.</summary>
    private static string DefaultOriginLabel => IslandStrings.Get("IslandTimerOrigin", "Timer");

    private DateTime _endUtc;
    private TimeSpan _frozen = TimeSpan.Zero;

    /// <summary>
    /// Raised exactly once on reaching zero (the host shows the notice, RF-6).
    /// </summary>
    public event Action? Finished;

    /// <summary>
    /// Raised on EVERY state change (start, pause, resume, restart, cancel and
    /// expiry) - 002 MOD RF-2. The host uses it to notify activity, re-arm its single
    /// expiry alarm and refresh only what is visible; the countdown does NOT depend on
    /// any global heartbeat (002 MOD RF-13).
    /// </summary>
    public event Action? Changed;

    public TimeSpan Remaining
    {
        get
        {
            switch (State)
            {
                case IslandTimerState.Running:
                    var left = _endUtc - DateTime.UtcNow;
                    return left < TimeSpan.Zero ? TimeSpan.Zero : left;
                case IslandTimerState.Paused:
                    return _frozen;
                case IslandTimerState.Alerting:
                    return TimeSpan.Zero;
                default:
                    return Configured;
            }
        }
    }

    /// <summary>
    /// Elapsed fraction 0..1 for the compact view's center progress (RF-8).
    /// </summary>
    public double ElapsedFraction
    {
        get
        {
            if (Configured <= TimeSpan.Zero) return 0;
            if (State == IslandTimerState.Alerting) return 1;
            double total = Configured.TotalSeconds;
            double done = total - Remaining.TotalSeconds;
            return Math.Clamp(done / total, 0, 1);
        }
    }

    public bool IsCounting => State == IslandTimerState.Running || State == IslandTimerState.Paused;

    public static bool IsValidDuration(TimeSpan duration) =>
        duration.TotalSeconds >= MinDurationSeconds && duration.TotalSeconds <= MaxDurationSeconds;

    /// <summary>
    /// Starts a new countdown, replacing the previous one if there was one (RF-3).
    /// Returns false and changes nothing if the duration is not valid (RF-2).
    /// </summary>
    public bool Start(TimeSpan duration, string originLabel)
    {
        if (!IsValidDuration(duration)) return false;
        Configured = duration;
        OriginLabel = string.IsNullOrWhiteSpace(originLabel) ? DefaultOriginLabel : originLabel;
        _endUtc = DateTime.UtcNow + duration;
        _frozen = TimeSpan.Zero;
        State = IslandTimerState.Running;
        Changed?.Invoke();
        return true;
    }

    public void Pause()
    {
        if (State != IslandTimerState.Running) return;
        _frozen = Remaining;
        State = IslandTimerState.Paused;
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (State != IslandTimerState.Paused) return;
        _endUtc = DateTime.UtcNow + _frozen;
        State = IslandTimerState.Running;
        Changed?.Invoke();
    }

    /// <summary>
    /// Restarts: returns the time to the configured value and leaves no active
    /// countdown (RF-5).
    /// </summary>
    public void Restart()
    {
        _frozen = TimeSpan.Zero;
        State = IslandTimerState.Idle;
        Changed?.Invoke();
    }

    /// <summary>
    /// Cancels/discards: no active countdown and no configured value (RF-5, notice X).
    /// </summary>
    public void Cancel()
    {
        Configured = TimeSpan.Zero;
        OriginLabel = DefaultOriginLabel;
        _frozen = TimeSpan.Zero;
        State = IslandTimerState.Idle;
        Changed?.Invoke();
    }

    /// <summary>
    /// Expiry check. The host fires it from its SINGLE alarm on the target time (and
    /// from its 5 s recovery net after a suspend), never from a global heartbeat: once
    /// the target time passes it stops the countdown and moves to alerting exactly once
    /// (RF-6).
    /// </summary>
    public void Poll(DateTime utcNow)
    {
        if (State != IslandTimerState.Running) return;
        if (utcNow < _endUtc) return;
        _frozen = TimeSpan.Zero;
        State = IslandTimerState.Alerting;
        Changed?.Invoke();
        Finished?.Invoke();
    }

    /// <summary>
    /// Timer format: always h:mm:ss with two digits (00:00:00).
    /// </summary>
    public static string FormatHms(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        long total = (long)t.TotalSeconds;
        return $"{total / 3600:00}:{(total % 3600) / 60:00}:{total % 60:00}";
    }

    /// <summary>
    /// Accepts h:mm:ss, m:ss and bare seconds. Only the 00:00:01-24:00:00 range.
    /// </summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.All(char.IsDigit))
        {
            if (!long.TryParse(text, out var seconds)) return false;
            duration = TimeSpan.FromSeconds(seconds);
            return IsValidDuration(duration);
        }
        var parts = text.Split(':');
        if (parts.Length is not (2 or 3)) return false;
        if (!parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return false;
        try
        {
            long seconds = 0;
            foreach (var p in parts) seconds = seconds * 60 + long.Parse(p);
            // m:ss does not allow 2+ digit minutes outside 0-59 when there are hours
            if (parts.Length == 3)
            {
                long m = long.Parse(parts[1]);
                long s = long.Parse(parts[2]);
                if (m > 59 || s > 59) return false;
            }
            else if (long.Parse(parts[1]) > 59) return false;
            duration = TimeSpan.FromSeconds(seconds);
            return IsValidDuration(duration);
        }
        catch { return false; }
    }
}
