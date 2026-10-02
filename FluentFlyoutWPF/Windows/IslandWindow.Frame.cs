// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// The Island animation engine: per-frame springs (no Storyboards) over a single
/// box, plus all the geometry/opacity computation for its states.
///
/// Rules it holds:
/// - p (0 compact -> 1 expanded) and q (0 hidden -> 1 visible) are underdamped
///   springs scaled with the global animation duration.
/// - The inactive state (narrow black piece) is not another box: it is the same
///   container with a narrow rest width and faded-out content, animated with the
///   same clock so there are no jumps and never two visible states at once.
/// - Folding back FROM the expanded view towards the piece: the geometry goes
///   straight to the piece (its width is already the rest width) and the content
///   opacity travels with the box's clock. It is a single transition, expanded ->
///   inactive: the compact silhouette is never drawn in passing (that read as two
///   stages).
/// - With animations off everything is applied at once (Snap*).
/// Part of IslandWindow; the state lives in <c>IslandWindow.xaml.cs</c>.
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// Duration of the content -> inactive piece transition: it follows the global
    /// animation speed so it stays consistent with the rest of the Island
    /// (001 MOD RF-16).
    /// </summary>
    private static double InactiveTransitionSeconds =>
        Math.Clamp(MainWindow.getDuration(), 120, 700) / 1000.0;

    /// <summary>
    /// Duration of the inactive piece -> content transition: slightly shorter than
    /// the one going into rest (the user coming back looks, they do not wait) but
    /// with enough of a floor for it to be SEEN - the width jump out of the piece
    /// is over a hundred pixels, and with a 90 ms clock it read as a hard cut -
    /// (001 MOD RF-16). The geometry and the content opacity share this same clock:
    /// the compact view BLOOMS out of the piece, it never appears out of nowhere, but
    /// it does appear.
    /// </summary>
    private static double InactiveReopenSeconds =>
        Math.Clamp(MainWindow.getDuration() * 0.7, 160, 480) / 1000.0;

    private void EnsureLoop()
    {
        if (_loopOn) return;
        _loopOn = true;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopLoop()
    {
        if (!_loopOn) return;
        _loopOn = false;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering -= OnFrame;
    }

    private TimeSpan _lastTick = TimeSpan.Zero;

    private void OnFrame(object? s, EventArgs e)
    {
        var args = e as RenderingEventArgs;
        TimeSpan now = args?.RenderingTime ?? TimeSpan.Zero;
        if (now == TimeSpan.Zero) now = TimeSpan.FromTicks(Environment.TickCount64 * 10000);
        double dt;
        if (_lastTick == TimeSpan.Zero || now <= _lastTick) dt = 1.0 / 60.0;
        else dt = IslandPhysics.ClampStep((now - _lastTick).TotalSeconds);
        _lastTick = now;

        // Pure physics (IslandPhysics): coefficients memoized by (duration, style).
        var spring = IslandPhysics.Coefficients(MainWindow.getDuration(), IsNotch);
        // El mismo muelle gobierna el crecimiento y el cierre. Como ancho y alto
        // interpolan con este único progreso, ambos llegan juntos a la pieza en
        // lugar de aplastarse primero y estrecharse después (001 MOD RF-16).
        // Al cambiar de expansión a cierre se descarta el impulso anterior para
        // que la isla no se estire un frame antes de empezar a encogerse.
        IslandPhysics.Step(ref _p, ref _pv, _pT, spring.KP, spring.CP, dt);
        IslandPhysics.Step(ref _q, ref _qv, _qT, spring.KQ, spring.CQ, dt);
        // Progress towards/away from the inactive piece: linear advance at constant
        // speed (Smooth01 supplies the ease-in-out at paint time). Retargeting
        // mid-flight keeps the same speed and never jumps. Each direction has its
        // own clock: entering rest is deliberate, leaving it is immediate
        // (001 MOD RF-16).
        if (_inactiveT != _inactiveTt)
        {
            bool opening = _inactiveTt < _inactiveT;
            double step = dt / (opening ? InactiveReopenSeconds : InactiveTransitionSeconds);
            _inactiveT += Math.Sign(_inactiveTt - _inactiveT) * step;
            if (_inactiveTt > _inactiveT ? _inactiveT >= _inactiveTt : _inactiveT <= _inactiveTt)
                _inactiveT = _inactiveTt;
        }
        // The expanded height chases its target: content changes glide, they do not jump.
        _hexpShown += (_hexp - _hexpShown) * Math.Clamp(dt * 10, 0, 1);
        if (Math.Abs(_hexp - _hexpShown) < 0.5) _hexpShown = _hexp;
        // Live hover micro-growth (inactive piece and resting box).
        double hotTarget = _inactiveHot ? 1 : 0;
        if (_inactiveHotT != hotTarget)
        {
            _inactiveHotT += (hotTarget - _inactiveHotT) * Math.Clamp(dt * 12, 0, 1);
            if (Math.Abs(_inactiveHotT - hotTarget) < 0.01) _inactiveHotT = hotTarget;
        }
        if (_popPlaying) StepPop(dt);
        ApplyFrame();

        bool pSettled = IslandPhysics.Settled(_p, _pv, _pT);
        bool qSettled = IslandPhysics.Settled(_q, _qv, _qT);
        if (pSettled) { _p = _pT; _pv = 0; }
        if (qSettled) { _q = _qT; _qv = 0; }

        bool popSettled = !_popPlaying;
        bool hSettled = _hexpShown == _hexp;
        bool hotSettled = _inactiveHotT == (_inactiveHot ? 1 : 0);
        bool inactSettled = _inactiveT == _inactiveTt;

        if (pSettled && qSettled && popSettled && hSettled && hotSettled && inactSettled)
        {
            StopLoop();
            _lastTick = TimeSpan.Zero;
            if (_inactiveShown && _inactiveT == 1)
            {
                // The inactive piece is already in its final shape: only now is the
                // leftover content withdrawn (001 MOD RF-11, RF-16).
                FinishInactive();
            }
            if (_qT == 0 && _q == 0)
            {
                IslandBox.Visibility = Visibility.Collapsed;
                if (_wasSuppressed) Visibility = Visibility.Collapsed;
                UpdateLine();
            }
            // If we reached the compact view via hidingViaCompact and no q is pending, it already
            // hid above.
            //
            // Safety net (one check per settling, not a heartbeat): if the active list
            // changed while the geometry was flying, the desired view is applied now.
            // This used to be a GATE - the piece's reopen was only attempted here and with
            // EVERYTHING settled - and from that came the compact views that took long or
            // never appeared at all.
            RepairPresentation();
        }
        else if (_qT == 0 && _q <= 0.12)
        {
            _hidingViaCompact = false;
            IslandBox.Visibility = Visibility.Collapsed;
            UpdateLine();
        }
    }

    private double _popV;
    private double _popPhase; // 0 ida, 1 vuelta
    private void StepPop(double dt)
    {
        // Fast 0->1 outbound, damped 1->0 return: a single cycle
        if (_popPhase == 0)
        {
            double a = (1 - _pop) * 900 - _popV * 28;
            _popV += a * dt;
            _pop += _popV * dt;
            _pop = Math.Clamp(_pop, 0, 1);
            if (_pop >= 0.995) { _pop = 1; _popV = 0; _popPhase = 1; }
        }
        else
        {
            double a = (0 - _pop) * 500 - _popV * 32;
            _popV += a * dt;
            _pop += _popV * dt;
            _pop = Math.Clamp(_pop, 0, 1);
            if (_pop <= 0.005) { _pop = 0; _popV = 0; _popPlaying = false; _popPhase = 0; }
        }
    }

    /// <summary>
    /// Shared expanded height (001 MOD RF-15): the tallest of the configured screens
    /// that can be shown. Every screen uses this same target, even the one in front
    /// having less content.
    /// </summary>
    private double _hexpShared;

    /// <summary>Prevents a measurement of all screens from re-entering.</summary>
    private bool _measuringExpandedHeight;

    /// <summary>
    /// Forgets the shared height: the next time the Island expands it is measured
    /// from scratch again (001 MOD RF-15).
    /// </summary>
    private void ResetSharedHeight() => _hexpShared = 0;

    /// <summary>
    /// Measures the real height of every configured screen and sets the target of a
    /// SINGLE height for the whole container (001 MOD RF-15); the loop glides towards
    /// it (or snaps to it, with animations off).
    ///
    /// <para>The height is SHARED: the tallest screen that exists right now wins, and
    /// in pill style the configured height is still the floor. Before, only the views
    /// that had already appeared in the session were accumulated, so the first screen
    /// could leave the container too short for another one. Measuring them all avoids
    /// that jump and keeps the pointer inside the box.</para>
    /// </summary>
    private void SyncMeasuredHeight()
    {
        try
        {
            double measuredHeight = MeasureMaxScreenHeight();
            if (measuredHeight <= 0)
            {
                // With no usable screens yet (during startup, for example), keep a fallback
                // measurement from whatever content is mounted.
                ExpandedLayer.Measure(new Size(ContentExpandedWidth, double.PositiveInfinity));
                measuredHeight = ExpandedLayer.DesiredSize.Height;
            }
            // On notch the height comes from the content (the setting belongs to the pill
            // style); on pill, the setting is the music floor.
            double floor = IsNotch ? 0 : ContentExpandedHeight;
            _hexpShared = Math.Max(floor, measuredHeight);
            double h = _hexpShared;
            double old = _hexp;
            if (h > 34 && h < 260) _hexp = h;
            // The target wins: if it changed, run frames (or snap). If it did not
            // change, the loop is not touched at all: music at rest does not notice.
            if (_hexp != old)
            {
                if (!AnimationsEnabled || (!IsBoxShown && _qT == 0)) _hexpShown = _hexp;
                else EnsureLoop();
            }
        }
        catch { }
    }

    /// <summary>
    /// Measures each screen with its real columns, without waiting for the user to
    /// open it. The composition is horizontal, which is why a combined screen's
    /// height is that of its tallest column, not the sum of all columns.
    /// </summary>
    private double MeasureMaxScreenHeight()
    {
        if (_measuringExpandedHeight || _screens.Count == 0) return 0;

        int savedScreenIndex = _screenIndex;
        bool savedExpanded = _expanded;
        IslandContentMode savedContentMode = _contentMode;
        IIslandFeature? savedSelectedFeature = _selectedFeature;
        double savedLayerWidth = ExpandedLayer.Width;
        HorizontalAlignment savedLayerAlignment = ExpandedLayer.HorizontalAlignment;
        Thickness savedLayerMargin = ExpandedLayer.Margin;
        string savedClipboardStatus = ClipboardStatus.Text;
        double maxHeight = 0;

        _measuringExpandedHeight = true;
        try
        {
            // Columns may have panels moved from ExpandedLayer. Every pass starts with
            // the tree back in its original place.
            RestoreExpandedHomes();

            for (int index = 0; index < _screens.Count; index++)
            {
                var members = ScreenUsableFeatures(_screens[index])
                    .Where(feature => ColumnFor(feature.Id) != null)
                    .ToList();
                if (members.Count == 0) continue;

                int columns = members.Count;
                bool combined = columns > 1;
                double screenWidth = combined
                    ? ScreenExpandedWidthForMembers(columns)
                    : ExpandedWidthForFeature(members[0]);
                double memberWidth = combined
                    ? ScreenColumnWidthForCount(columns)
                    : screenWidth;

                _screenIndex = index;
                _expanded = true;
                _contentMode = combined ? IslandContentMode.Screen : ModeForFeature(members[0].Id);
                SelectFeature(members[0].Id);
                ExpandedLayer.Width = IsNotch ? screenWidth : double.NaN;
                ExpandedLayer.HorizontalAlignment = IsNotch
                    ? HorizontalAlignment.Center
                    : HorizontalAlignment.Stretch;
                ExpandedLayer.Margin = _expandedMarginOrig;
                HideAllExpandedPanels();

                double screenContentHeight = 0;
                foreach (var member in members)
                {
                    var card = FeatureCard(member.Id);
                    if (card == null) continue;

                    card.ShowExpanded();
                    double memberHeight = 0;
                    foreach (var panel in card.Expanded)
                    {
                        if (panel.Visibility == Visibility.Collapsed) continue;
                        panel.Measure(new Size(memberWidth, double.PositiveInfinity));
                        memberHeight += panel.DesiredSize.Height;
                    }
                    screenContentHeight = Math.Max(screenContentHeight, memberHeight);
                }

                if (screenContentHeight > 0)
                {
                    maxHeight = Math.Max(
                        maxHeight,
                        screenContentHeight + _expandedMarginOrig.Top + _expandedMarginOrig.Bottom);
                }
            }
        }
        finally
        {
            try
            {
                // The measurement is invisible to the user: it leaves the view that was in
                // front untouched and re-composes the current screen if needed.
                RestoreExpandedHomes();
                _screenIndex = savedScreenIndex;
                _expanded = savedExpanded;
                _contentMode = savedContentMode;
                _selectedFeature = savedSelectedFeature;
                try { ApplyContentVisibility(); } catch { }
                ClipboardStatus.Text = savedClipboardStatus;
                ExpandedLayer.Width = savedLayerWidth;
                ExpandedLayer.HorizontalAlignment = savedLayerAlignment;
                ExpandedLayer.Margin = savedLayerMargin;
            }
            finally
            {
                _measuringExpandedHeight = false;
            }
        }

        return maxHeight;
    }

    private double ExpandedWidthForFeature(IIslandFeature feature) =>
        feature.ExpandedPreferredWidth > 0
            ? Math.Clamp(feature.ExpandedPreferredWidth, 200, 600)
            : ExpandedIslandWidth;

    private void SnapFrame()
    {
        // Coherent base state before the first frame
        _p = _pT; _q = _qT;
        _pv = _qv = 0;
        _inactiveT = _inactiveTt = _inactiveShown ? 1 : 0;
        _collapseFromExpanded = false;
        _hexpShown = _hexp;
        _inactiveHotT = _inactiveHot ? 1 : 0;
        ApplyFrame();
    }

    /// <summary>
    /// Paints the container's complete frame: geometry (width, height, radius,
    /// clip, notch), crossfade of the compact and expanded layers, background and
    /// content opacities. It is called on every loop frame and also on demand when
    /// no animation is running.
    /// </summary>
    private void ApplyFrame()
    {
        double p = Math.Clamp(_p, 0, 1);
        double q = Math.Clamp(_q, 0, 1);
        // Body progress (0 = rest size, 1 = expanded), for geometry only (width/height,
        // with the spring bounce intact): the container overshoots its final size a
        // little and comes back. In both directions this progress is the only clock
        // for geometry, so width and height travel together. The reveal (q) carries
        // its own through the stretch curve, below.
        double bounceP = IslandPhysics.BounceCurve(_p);
        // Progress towards the inactive piece, with ease-in-out: it governs the rest
        // width and the transitions that start from the compact view. When the close
        // starts from the expanded one, p's spring governs opacity so content and
        // geometry land together (001 MOD RF-16).
        double inact = IslandPhysics.Smooth(_inactiveT);
        // Content opacity on the same clock as the geometry: the content fades while
        // the body lands on the piece, so expanded -> inactive is ONE single
        // transition and not two chained stages (001 MOD RF-16).
        double contentOp = _collapseFromExpanded ? IslandPhysics.Smooth(p) : 1 - inact;
        // Content opening out of the piece: 0 at rest, 1 in content.
        // It is that SAME rest clock that makes the compact view bloom (scale,
        // artwork, title and equalizer converge from the center) besides fading its
        // opacity: without this the way back from the piece was a plain fade that
        // read as a jump (001 MOD RF-16). In content it equals 1 and touches nothing.
        double restReveal = IslandPhysics.Smooth(Math.Clamp((1 - inact - 0.10) / 0.90, 0, 1));
        // Grey line on the same clock as the island (p and q): the island grows centred
        // = inside out, the line shrinks centred = outside in. IslandLine resolves
        // its width and its presence - one single rule, shared with the pointer
        // strip: with no door behind there is no handle, and without painting it a
        // loose stripe is left over either.
        _lineW = _lineShown ? IslandLine.BarWidth * IslandLine.WidthFactor(p, q, contentOp) : 0;
        ActivityLine.Width = _lineW;
        ActivityLine.Visibility = _lineW > 0.5 ? Visibility.Visible : Visibility.Collapsed;
        MediaStatusDot.Opacity = contentOp;
        // Track pop fades with q (invisible -> does not pulse)
        double pop = _popPlaying ? _pop * q : 0;
        double exitTailOpacity = _qT == 0
            ? Math.Pow(IslandPhysics.Smooth(Math.Clamp((q - 0.12) / 0.20, 0, 1)), 3)
            : 1;
        if (_hidingViaCompact)
            exitTailOpacity *= Math.Pow(IslandPhysics.Smooth(Math.Clamp((p - 0.12) / 0.38, 0, 1)), 3);

        bool notch = IsNotch;
        double w, h, notchFillet = 0;
        // Live rest: the inactive piece and the compact view breathe with hover
        // (001 MOD RF-3, RF-16): they only grow in size, the container never moves
        // from its position.
        double hotW = 10 * _inactiveHotT;
        if (notch)
        {
            // Notch: same reveal as the Island: centre dot -> compact (narrower by
        // por diseño) -> expandido.
            const double notchDot = 26;
            // On the way to the piece from the expanded view the rest width IS the piece's
            // width: so the WIDTH interpolates from expanded to piece with the SAME
            // progress as the HEIGHT and both shrink at once. Chaining the rest clock
            // here (compact -> piece) left the width waiting for the height to finish
            // - first it squashed, then it narrowed - (001 MOD RF-16).
            double compactW = _collapseFromExpanded
                ? InactivePillWidth
                : IslandPhysics.Lerp(RestCompactWidth(NotchCompactWidth), InactivePillWidth, inact);
            double dotT = Math.Clamp(q / 0.32, 0, 1);
            double stretchT = IslandPhysics.RevealStretch(q);
            double baseW = q < 0.32 ? notchDot : IslandPhysics.Lerp(notchDot, compactW, stretchT);
            w = IslandPhysics.Lerp(baseW, ContentExpandedWidth, bounceP);
            h = IslandPhysics.Lerp(ContentCompactHeight, _hexpShown, bounceP);
            // Live hover: grows out of the dot and at rest (also in compact).
            w += hotW * (1 - IslandPhysics.Smooth(p));
            double revealOpacity = IslandPhysics.Smooth(Math.Clamp(q / 0.38, 0, 1));
            IslandBox.Opacity = revealOpacity * revealOpacity * exitTailOpacity;
            // The radius morphs with p: compact -> expanded with no jumps.
            // NOTE: the ear WIDTH is fixed by the expanded fillet (constant per state);
            // the cave DROP morphs compact->expanded: stretched ellipse -> circle.
            double radius = Math.Min(IslandPhysics.Lerp(IslandCompactRadius, IslandExpandedRadius, IslandPhysics.Smooth(p)), Math.Min(w, h) / 2);
            // ponytail: reach y drop usan la curva del estado actual; el extra (+18 = 25-30%)
            // solo aplica en expandido (escala con p), en compacto no se inyecta ancho.
            double filletNow = IslandPhysics.Lerp(Math.Clamp(SettingsManager.Current.IslandNotchFilletCompact, 0, 20), Math.Clamp(SettingsManager.Current.IslandNotchFilletExpanded, 0, 20), IslandPhysics.Smooth(p));
            double earReach = (Math.Clamp(filletNow, 0, 20) + 18 * IslandPhysics.Smooth(p)) * stretchT;
            earReach = Math.Min(earReach, Math.Max(0, (Width - w) / 2 - 2));
            notchFillet = Math.Clamp(filletNow, 0, 20) * stretchT;
            IslandBox.CornerRadius = new CornerRadius(0);
            IslandBox.Width = w + 2 * earReach;
            IslandBox.Height = h;
            // ponytail: las orejas son solo fondo; el contenido vive en el ancho lógico w.
            ExpandedLayer.Width = w;
            ExpandedLayer.Margin = new Thickness(0, _expandedMarginOrig.Top, 0, _expandedMarginOrig.Bottom);
            ExpandedLayer.HorizontalAlignment = HorizontalAlignment.Center;
            IslandBox.RenderTransformOrigin = new Point(0.5, 0);
            BoxScale.ScaleX = BoxScale.ScaleY = IslandPhysics.Lerp(0.92, 1, IslandPhysics.Smooth(dotT));
            ApplyIslandClip(IslandBox.Width, h, radius, earReach, notchFillet, notch: true);
            LayoutBackground(IslandBox.Width, h);
        }
        else
        {
            // Pill: hidden -> 26px dot (circular) -> compact (or the narrower inactive
            // piece, 001 MOD RF-11) -> the configured expanded width.
            const double pillDot = 26;
            double dotT = Math.Clamp(q / 0.32, 0, 1);
            double stretchT = IslandPhysics.RevealStretch(q);
            // On the way to the piece from the expanded view the rest width IS the piece's
            // width: so the WIDTH interpolates from expanded to piece with the SAME
            // progress as the HEIGHT and both shrink at once. Chaining the rest clock
            // here (compact -> piece) left the width waiting for the height to finish
            // - first it squashed, then it narrowed - (001 MOD RF-16). The compact
            // view does not bloom in passing because its layer is faded out, not
            // because the width prevents it.
            double restW = _collapseFromExpanded
                ? InactivePillWidth
                : IslandPhysics.Lerp(ContentCompactWidth, InactivePillWidth, inact);
            double baseW = q < 0.32 ? pillDot : IslandPhysics.Lerp(pillDot, restW, stretchT);
            w = IslandPhysics.Lerp(baseW, ContentExpandedWidth, bounceP);
            h = IslandPhysics.Lerp(ContentCompactHeight, _hexpShown, bounceP);
            // Live hover: grows out of the dot and at rest (also in compact).
            w += hotW * (1 - IslandPhysics.Smooth(p));
            IslandBox.Width = w;
            IslandBox.Height = h;
            ExpandedLayer.Width = double.NaN;
            ExpandedLayer.Margin = _expandedMarginOrig;
            ExpandedLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
            IslandBox.Opacity = IslandPhysics.Smooth(Math.Clamp(q / 0.38, 0, 1)) * exitTailOpacity;
            // Radius: a perfect circle while it is the dot, then compact->expanded morph.
            // When expanded (p>0.02) always pill with the expanded radius.
            double morphR = IslandPhysics.Lerp(IslandCompactRadius, IslandExpandedRadius, IslandPhysics.Smooth(p));
            double cr = baseW <= pillDot + 0.5 && p < 0.02
                ? pillDot / 2
                : Math.Min(morphR, Math.Min(w, h) / 2);
            IslandBox.CornerRadius = new CornerRadius(cr);
            BoxScale.ScaleX = BoxScale.ScaleY = IslandPhysics.Lerp(0.92, 1, IslandPhysics.Smooth(dotT));
            IslandBox.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        if (!notch)
        {
            ApplyIslandClip(w, h, IslandBox.CornerRadius.TopLeft);
            LayoutBackground(w, h);
        }

        // Layer crossfade + content morph (Apple: album and title breathe). One single
        // route for pill and notch: the content reveal depends only on q, so
        // branching it duplicated 40 identical lines and opened the door to the two
        // styles drifting out of sync.
        double stretchT2 = IslandPhysics.Smooth(Math.Clamp((q - 0.18) / 0.82, 0, 1));
        double contentT = Math.Clamp((stretchT2 - 0.42) / 0.58, 0, 1);
        double dotT2 = Math.Clamp(q / 0.32, 0, 1);
        double compactOp = (1 - IslandPhysics.Smooth(Math.Clamp(p * 2.2, 0, 1))) * IslandPhysics.Smooth(contentT);
        if (q < 0.32) compactOp = 0;
        else compactOp *= IslandPhysics.Lerp(0.85, 1, dotT2);
        // On the way to the inactive piece from an expanded view the compact view does
        // NOT bloom - neither at the start nor at the end of the flight -: only the
        // content that was already there fades, so expanded -> inactive is ONE single
        // transition with no compact flash (001 MOD RF-16). Phase 2 of a two-phase
        // fold-back starts with the flag off, so there the compact view does bloom;
        // and the fold-back that starts from the compact view (p=0) does not set it,
        // so its content keeps fading on the rest clock.
        if (_collapseFromExpanded) compactOp = 0;
        double expandedOp = IslandPhysics.Smooth(Math.Clamp((p - 0.12) / 0.88, 0, 1));
        CompactLayer.Opacity = compactOp * contentOp;
        CompactScale.ScaleX = CompactScale.ScaleY = IslandPhysics.Lerp(0.95, 1, IslandPhysics.Smooth(contentT) * restReveal);
        if (q < 0.32)
            CompactScale.ScaleX = CompactScale.ScaleY = IslandPhysics.Lerp(0.90, 0.94, dotT2);
        else if (notch)
        {
            // On notch the compact view huddles when entering the expanded one.
            double pScale = IslandPhysics.Lerp(1, 0.92, IslandPhysics.Smooth(p));
            CompactScale.ScaleX *= pScale;
            CompactScale.ScaleY *= pScale;
        }

        // The content also blooms from the center during the reveal - and during the
        // way back from the piece, on the rest clock -:
        // diverge(0) = huddled at the center, diverge(1) = in its place.
        double diverge = Math.Pow(stretchT2, 1.25) * restReveal;
        // SYMMETRIC drift: artwork and equalizer travel the same distance towards the
        // center (before it was 42 vs 36, and the equalizer dragged the view to one
        // side). Short distances on purpose: the content converges, it does not travel.
        CompactArtTranslate.X = IslandPhysics.Lerp(22, 0, diverge);
        CompactTitleTranslate.X = IslandPhysics.Lerp(4, 0, diverge);
        CompactEqTranslate.X = IslandPhysics.Lerp(-22, 0, diverge);
        CompactTitleScale2.ScaleX = CompactTitleScale2.ScaleY = IslandPhysics.Lerp(0.92, 1, diverge);
        double titleOp = IslandPhysics.Smooth(Math.Clamp((stretchT2 - 0.50) / 0.50, 0, 1));
        double eqOp = IslandPhysics.Smooth(Math.Clamp((stretchT2 - 0.55) / 0.45, 0, 1));
        CompactTitle.Opacity = q < 0.32 ? 0 : titleOp;
        CompactEq.Opacity = q < 0.32 ? 0 : eqOp;
        CompactArtWrap.Opacity = q < 0.15 ? 0 : (q < 0.32 ? IslandPhysics.Smooth(dotT2) : 1);

        // When inactive there is no content, but it is NOT cut off abruptly either: the
        // previous view fades on the same clock as the piece's width and is only
        // removed from the tree when it ends (FinishInactive). The background
        // (blurred artwork) is content and fades the same way (001 MOD RF-11, RF-16).
        BackgroundCanvas.Opacity = contentOp;
        bool bgWanted = contentOp > 0.01;
        if ((BackgroundCanvas.Visibility == Visibility.Visible) != bgWanted)
            BackgroundCanvas.Visibility = bgWanted ? Visibility.Visible : Visibility.Collapsed;
        // Hit-testing only turns off once the piece already dominates the view: during
        // the transition clicks still reach the outgoing content.
        bool contentLive = contentOp > 0.5;
        CompactLayer.IsHitTestVisible = contentLive && p < 0.6 && q > 0.35;
        ExpandedLayer.Opacity = expandedOp * contentOp * (notch ? q : 1);
        ExpandedLayer.IsHitTestVisible = contentLive && p > 0.4 && q > 0.4;

        double artS = IslandPhysics.Lerp(0.94, 1, IslandPhysics.Smooth(Math.Clamp((p - 0.05) / 0.95, 0, 1)));
        // Pop adds a slight bump to artwork/title on track change
        artS += pop * 0.04;
        ExpandedArtScale.ScaleX = ExpandedArtScale.ScaleY = artS;

        double titleS = IslandPhysics.Lerp(0.94, 1, IslandPhysics.Smooth(Math.Clamp((p - 0.08) / 0.9, 0, 1))) + pop * 0.035;
        SongTitleScale.ScaleX = SongTitleScale.ScaleY = titleS;
        SongTitle.Opacity = IslandPhysics.Lerp(0, 1, IslandPhysics.Smooth(Math.Clamp((p - 0.12) / 0.7, 0, 1)));
        SongTitleTranslate.Y = IslandPhysics.Lerp(4, 0, IslandPhysics.Smooth(Math.Clamp((p - 0.12) / 0.7, 0, 1)));

        SongArtist.Opacity = IslandPhysics.Lerp(0, _islandArtistOpacity, IslandPhysics.Smooth(Math.Clamp((p - 0.22) / 0.6, 0, 1)));
        SongArtistTranslate.Y = IslandPhysics.Lerp(4, 0, IslandPhysics.Smooth(Math.Clamp((p - 0.22) / 0.6, 0, 1)));

        ExpandedEq.Opacity = IslandPhysics.Lerp(0, 1, IslandPhysics.Smooth(Math.Clamp((p - 0.18) / 0.6, 0, 1)));
        SeekRow.Opacity = IslandPhysics.Lerp(0, 1, IslandPhysics.Smooth(Math.Clamp((p - 0.30) / 0.5, 0, 1)));
        SeekTranslate.Y = IslandPhysics.Lerp(5, 0, IslandPhysics.Smooth(Math.Clamp((p - 0.30) / 0.5, 0, 1)));
        ControlsRow.Opacity = IslandPhysics.Lerp(0, 1, IslandPhysics.Smooth(Math.Clamp((p - 0.38) / 0.5, 0, 1)));
        ControlsTranslate.Y = IslandPhysics.Lerp(5, 0, IslandPhysics.Smooth(Math.Clamp((p - 0.38) / 0.5, 0, 1)));
    }

    /// <summary>
    /// Brief artwork/title pulse on a track change (only in the visible compact view
    /// with animations enabled): an accent, never a new state.
    /// </summary>
    private void PlayTrackPop()
    {
        if (!AnimationsEnabled || _expanded) return;
        if (!IsBoxShown || _q < 0.6) return;
        if (_popPlaying) return;
        _pop = 0; _popV = 0; _popPhase = 0; _popPlaying = true;
        EnsureLoop();
    }

    // ------------------------------------------------------------------
    // Container geometry (per-frame clip, no Storyboards)
    // ------------------------------------------------------------------

    private static Geometry CreateIslandClip(double width, double height, CornerRadius radius)
    {
        if (width <= 0 || height <= 0) return Geometry.Empty;
        double tl = Math.Clamp(radius.TopLeft, 0, Math.Min(width, height) / 2);
        double tr = Math.Clamp(radius.TopRight, 0, Math.Min(width, height) / 2);
        double br = Math.Clamp(radius.BottomRight, 0, Math.Min(width, height) / 2);
        double bl = Math.Clamp(radius.BottomLeft, 0, Math.Min(width, height) / 2);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(tl, 0), true, true);
            context.LineTo(new Point(width - tr, 0), true, false);
            AddCorner(context, new Point(width, tr), tr);
            context.LineTo(new Point(width, height - br), true, false);
            AddCorner(context, new Point(width - br, height), br);
            context.LineTo(new Point(bl, height), true, false);
            AddCorner(context, new Point(0, height - bl), bl);
            context.LineTo(new Point(0, tl), true, false);
            AddCorner(context, new Point(tl, 0), tl);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void AddCorner(StreamGeometryContext context, Point end, double radius)
    {
        if (radius <= 0.01)
            context.LineTo(end, true, false);
        else
            context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
    }

    // NOTE: notch hugging the edge with a cave (W includes 2*ear reach).
    // reach = horizontal width (fixed: expanded fillet), drop = vertical fall (morph).
    private static Geometry CreateNotchClip(double width, double height, double bottomRadius, double reach, double drop)
    {
        if (width <= 0 || height <= 0) return Geometry.Empty;
        double br = Math.Clamp(bottomRadius, 0, Math.Min(width, height) / 2);
        double r = Math.Clamp(reach, 0, 24);
        double f = Math.Min(Math.Clamp(drop, 0, 20), Math.Max(0, height - br - 1));
        if (Math.Max(r, f) < 0.5)
            return CreateIslandClip(width, height, new CornerRadius(0, 0, br, br));
        double kx = 0.5523 * r, ky = 0.5523 * f; // aprox. cuarto de elipse
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(0, 0), true, true);
            context.LineTo(new Point(width, 0), true, false);
            context.BezierTo(new Point(width - kx, 0), new Point(width - r, f - ky), new Point(width - r, f), true, false);
            context.LineTo(new Point(width - r, height - br), true, false);
            AddCorner(context, new Point(width - r - br, height), br);
            context.LineTo(new Point(r + br, height), true, false);
            AddCorner(context, new Point(r, height - br), br);
            context.LineTo(new Point(r, f), true, false);
            context.BezierTo(new Point(r, f - ky), new Point(kx, 0), new Point(0, 0), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    // --- per-frame clip memo ---
    // The clip geometry is only rebuilt when its parameters genuinely change (DIP
    // fractions); once the clip has settled the frozen StreamGeometry is reused. It
    // was the animation engine's largest allocation: a new one per frame, frozen and
    // full of arcs, on every paint.
    private const double ClipEpsilon = 0.05;
    private Geometry? _clipGeometry;
    private double _clipWidth, _clipHeight, _clipRadius, _clipReach, _clipDrop;
    private bool _clipNotch;

    /// <summary>
    /// Applies the container clip, reusing the last built geometry when its
    /// parameters have not changed. With <paramref name="notch"/> it uses the
    /// eared and caveed silhouette; otherwise the rounded-corner capsule.
    /// </summary>
    private void ApplyIslandClip(double width, double height, double radius,
        double reach = 0, double drop = 0, bool notch = false)
    {
        if (_clipGeometry != null && _clipNotch == notch
            && Math.Abs(_clipWidth - width) < ClipEpsilon
            && Math.Abs(_clipHeight - height) < ClipEpsilon
            && Math.Abs(_clipRadius - radius) < ClipEpsilon
            && Math.Abs(_clipReach - reach) < ClipEpsilon
            && Math.Abs(_clipDrop - drop) < ClipEpsilon)
        {
            if (!ReferenceEquals(IslandBox.Clip, _clipGeometry)) IslandBox.Clip = _clipGeometry;
            return;
        }
        _clipWidth = width;
        _clipHeight = height;
        _clipRadius = radius;
        _clipReach = reach;
        _clipDrop = drop;
        _clipNotch = notch;
        _clipGeometry = notch
            ? CreateNotchClip(width, height, radius, reach, drop)
            : CreateIslandClip(width, height, new CornerRadius(radius));
        IslandBox.Clip = _clipGeometry;
    }
}
