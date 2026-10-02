// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Coefficients of the Island's underdamped spring, already scaled by the global
/// animation duration.
/// </summary>
public readonly record struct IslandSpring(double KP, double CP, double KQ, double CQ);

/// <summary>
/// The Island's PHYSICS: the spring that governs its geometry and the curves that
/// paint it. It is pure arithmetic, with no WPF and no container state, so it can be
/// tested without opening a window (it used to live inside the per-frame engine, where
/// it could only be validated by eye).
///
/// <para>Rules it holds:</para>
/// <list type="bullet">
/// <item>Damping is chosen so the spring BOUNCES like Apple's (ζ ≈ 0.58 on the morph,
/// ζ ≈ 0.72 on the reveal): reaching the final size it overshoots a little and comes
/// back. The bounce is the same at any configured speed, because the duration scales
/// frequency and damping at the same time.</item>
/// <item>The spring step is clamped at the bottom (1/240 s) and at the top (1/25 s), so
/// that a dropped frame or a suspend does not blow it up.</item>
/// <item>The painted bounce has a ceiling (<see cref="BounceLimit"/>) and a minimum
/// floor (<see cref="BounceCompress"/>): the overshoot when OPENING is capped at 5%
/// and the compression when CLOSING at 0.6%, so the island lands almost firm without
/// losing the spring entirely.</item>
/// </list>
/// </summary>
public static class IslandPhysics
{
    /// <summary>Minimum spring step (one frame at 240 Hz).</summary>
    public const double MinStepSeconds = 1.0 / 240.0;

    /// <summary>Maximum spring step (one frame at 25 Hz): a dropped frame does not blow up the bounce.</summary>
    public const double MaxStepSeconds = 1.0 / 25.0;

    /// <summary>Bounce ceiling: how far the geometry may overshoot its final size.</summary>
    public const double BounceLimit = 0.05;

    /// <summary>How far the geometry may compress below its destination.</summary>
    public const double BounceCompress = 0.006;

    /// <summary>
    /// Spring coefficients for a global duration and a style, memoized by
    /// (duration, style): the frame only reads them, it does not recompute them.
    /// </summary>
    public static IslandSpring Coefficients(double durationMs, bool notch)
    {
        if (durationMs != _duration || notch != _notch)
        {
            _duration = durationMs;
            _notch = notch;
            double durationScale = durationMs > 0 ? durationMs / 300.0 : 1.0;
            double frequencyScale = 1.0 / (durationScale * durationScale);
            double dampingScale = 1.0 / durationScale;
            double kp = 520 * frequencyScale;
            double cp = 26 * dampingScale;
            double kq = 200 * frequencyScale;
            double cq = 20 * dampingScale; // ambos estilos emergen desde el centro como Island
            // Notch pushes 5% harder (it raises stiffness, not damping), so its ζ drops by
            // 2%: 0.570 → 0.556. It is the bounce that was already visible; it is pinned
            // in the check so nobody changes it by accident.
            if (notch) kp *= 1.05;
            _spring = new IslandSpring(kp, cp, kq, cq);
        }
        return _spring;
    }

    private static double _duration = double.NaN;
    private static bool _notch;
    private static IslandSpring _spring;

    /// <summary>
    /// One spring step (semi-implicit Euler integration). The displacement is clamped
    /// to [-0.15, 1.15] so the overshoot is visible but does not run away.
    /// </summary>
    public static void Step(ref double x, ref double v, double target, double k, double c, double dt)
    {
        double a = (target - x) * k - v * c;
        v += a * dt;
        x += v * dt;
        x = Math.Clamp(x, -0.15, 1.15);
    }

    /// <summary>Is the spring already at its destination? (position and velocity threshold)</summary>
    public static bool Settled(double x, double v, double target) =>
        Math.Abs(x - target) < 0.002 && Math.Abs(v) < 0.02;

    /// <summary>Hermite smoothing (starts and stops on its own): the easing of the whole Island.</summary>
    public static double Smooth(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// Progress with the spring BOUNCE intact on the upper side and compressed on the
    /// lower one: the container overshoots its size a little and comes back - Apple's
    /// bounce - instead of stopping dead, but it does not thin out below the target.
    /// </summary>
    public static double BounceCurve(double t) => Math.Clamp(t, -BounceCompress, 1 + BounceLimit);

    /// <summary>
    /// Reveal stretch curve (hidden → dot → rest width). It keeps the original smooth
    /// shape - the dot is born without a jump when it crosses its width threshold - and
    /// lets the bounce through ONLY on the tail, once the spring has already passed its
    /// target: injecting it across the whole curve would multiply the threshold value
    /// and the dot would jerk as it started stretching.
    /// </summary>
    public static double RevealStretch(double q)
    {
        double t = (q - 0.18) / 0.82;
        if (t <= 0) return 0;
        if (t < 1) return Smooth(t);
        return 1 + Math.Min(t - 1, BounceLimit);
    }

    /// <summary>Clamps one frame's step to the range the spring understands.</summary>
    public static double ClampStep(double seconds) =>
        Math.Clamp(seconds, MinStepSeconds, MaxStepSeconds);
}
