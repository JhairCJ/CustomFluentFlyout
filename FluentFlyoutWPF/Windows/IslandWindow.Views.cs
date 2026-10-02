// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Current content band of the container (001 MOD RF-11): which feature
/// is on screen. It replaces the magic 0/1/2 integers that were compared by hand
/// in every partial.
/// </summary>
internal enum IslandContentMode
{
    Media = 0,
    Timer = 1,
    Apps = 2,
    Shelf = 3,
    Calendar = 4,
    Bluetooth = 5,
    /// <summary>COMBINED screen (several features at once, change island-pantallas).</summary>
    Screen = 6,
    Clipboard = 7,
    /// <summary>Weather of the configured place (change island-clima).</summary>
    Weather = 8,
    /// <summary>Charger of the machine (change island-cargador).</summary>
    Power = 9,
    /// <summary>Voice dictation with the key held down (spec 006).</summary>
    Dictation = 10,
}

/// <summary>
/// SINGLE presentation route of the container: how it gets to the compact and to the
/// expanded view, written once for all features.
///
/// <para>Before, music, timer and drawer repeated the same sequence
/// (compute the collapse in progress, cancel the inactive rest, apply
/// content, measure, snap or animate, arm the notice) and any touch-up had
/// to be done three times: forgetting one was enough for the three views to
/// diverge. Now each feature contributes ONLY its content
/// (<c>present</c>) and its availability check; the choreography of the
/// container lives here:</para>
///
/// <list type="bullet">
/// <item><b>Compact</b> — guards → content → resting geometry → snap or
/// springs → temporary notice deadline (001 RF-2).</item>
/// <item><b>Expanded</b> — guards → content → expanded geometry → snap or
/// springs towards p=1, q=1.</item>
/// </list>
///
/// <para>The FACE is ALWAYS presented in the same turn (change
/// island-lista-de-activos): an active event is seen immediately, and no box can be
/// left without layers. Before, the collapse from the expanded view could skip the
/// presentation and leave the black pill empty —the timer «black block».</para>
///
/// <para>Part of IslandWindow; the state lives in <c>IslandWindow.xaml.cs</c>,
/// the state machine in <c>IslandWindow.States.cs</c> and the per-frame
/// animation engine in <c>IslandWindow.Frame.cs</c>.</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// SINGLE entry point to the compact view. The feature brings its
    /// availability (its own guards, already evaluated by the caller) and the
    /// content of <paramref name="present"/>; the container decides the
    /// geometry, the animation and the temporary notice.
    ///
    /// <para><paramref name="forceNotice"/> and <paramref name="restartNotice"/> are
    /// for the contents whose notice is ALWAYS temporary (Bluetooth devices, the
    /// charger: change island-bluetooth-conectado RF-1): the notice expires even when the
    /// mode is «Visible while active», and re-presenting it does not restart its deadline
    /// —only a new event does—.</para>
    ///
    /// <para>The view of the Island is a SCREEN (change island-pantallas): the
    /// feature adopts its own, but the COMPACT does not group —it shows the rich view of
    /// that feature, a single one, even if its screen carries several— and it is the
    /// later click that opens the whole screen. A feature that is on no screen has no
    /// view: the container resolves something else —or rest— instead of
    /// presenting it.</para>
    /// </summary>
    private void ShowCompactView(IslandContentMode mode, IIslandFeature? feature, Action present,
        bool forceNotice = false, bool restartNotice = true)
    {
        // A view that is presented is a view that is seen: if the window ended up
        // Collapsed (suppression, off, collapse to nothing), it peeks out again right
        // here. Without this, a notice arriving with the window withdrawn painted its
        // card inside a window that was not visible.
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        _hidingViaCompact = false;
        if (feature != null) SelectFeature(feature.Id);
        // Entering content cancels the inactive rest: without this the compact would be
        // painted over already faded content (001 MOD RF-16).
        SetInactiveRest(false);
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) { SnapHidden(); return; }
        // SCREENS: the screen of the feature owns the view, and the compact
        // shows ONE single one of its features (its rich view): grouping belongs to the
        // expanded view, which is what the click opens. Without a screen there is nothing
        // to present: the view is resolved through the normal routes (another screen,
        // another active one or rest). The exceptions keep their own view: an exclusive
        // one (the timer alert) and a NOTICE (Bluetooth, charger, dictation), which is
        // neither screen nor navigable.
        if (feature != null && !AdoptScreenFor(feature, expanded: false, ref mode, ref present)
            && !ScreenlessFeatureKeepsOwnView(feature))
        {
            _expanded = false;
            HidePerMode();
            return;
        }
        // The face and the geometry, in the SAME turn: the compact view must be resolved
        // before painting (keeping _expanded alive here made an intermediate
        // update read the expanded state and mix panels from both
        // presentations).
        _expanded = false;
        _contentMode = mode;
        present();
        ApplyContentVisibility();
        // The views that arrive straight from a function must also
        // update the presentation cache. Otherwise a quick collapse could
        // compare against the previous state (inactive/hidden) and not apply the exit.
        _appliedPresentation = new IslandPresentationResult(
            IslandDesiredView.Compact, CurrentViewFeatureId(), restartNotice, forceNotice);
        // The navigation arrows only exist in the expanded view: they turn off right
        // away, in the same turn, instead of waiting for the next container heartbeat.
        UpdateArrows();
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled) SnapCompact();
        else SetCompactFrame();
        // «Temporary notice» (001 RF-2, 002 RF-16): the compact view publishes its notice
        // in the active list with the configured deadline. restart:false keeps the deadline
        // that was already running, so interacting (expanding and coming back) never
        // extends the notice.
        ArmTemporaryHide(restart: restartNotice, force: forceNotice);
    }

    /// <summary>
    /// SINGLE entry point to the expanded view. <paramref name="guard"/> is
    /// evaluated right after cancelling the inactive rest —before applying
    /// content and geometry— for the features that need to veto the
    /// expansion with the state already normalized (media with a current
    /// exclusive one).
    ///
    /// <para><paramref name="skipIfExpanded"/> set to false forces the re-arming of
    /// the frame even if the box was already expanded: it is what the final
    /// timer notice needs, since it must prevail over the current view
    /// (002 RF-2, RF-6).</para>
    ///
    /// <para>As in the compact, the view is the SCREEN of the feature: with a
    /// combined screen the expanded view is its row of columns (from left to right)
    /// and, with no screen, the feature opens nothing (change island-pantallas).</para>
    /// </summary>
    private void ShowExpandedView(IslandContentMode mode, IIslandFeature? feature, Action present,
        bool skipIfExpanded = true, Func<bool>? guard = null)
    {
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        // Expanding does not cancel the notice: it only postpones its collapse keeping
        // the deadline it had left (001 RF-2).
        HoldTemporaryNotice();
        _hidingViaCompact = false;
        bool wasExpanded = _expanded;
        if (feature != null) SelectFeature(feature.Id);
        SetInactiveRest(false);
        // SCREENS: the screen of the feature owns the view (columns if it is
        // combined) and without a screen there is nothing to open: the view is resolved
        // through the normal routes instead of opening the loose feature. The exception
        // is an exclusive one (the timer alert), which keeps its own view.
        if (feature != null && !AdoptScreenFor(feature, expanded: true, ref mode, ref present)
            && !ScreenlessFeatureKeepsOwnView(feature))
        {
            HidePerMode();
            return;
        }
        if (guard != null && !guard()) return;
        _contentMode = mode;
        // The content and the composition must know the final state before
        // rendering. If it were set afterwards, a combined screen went in through the
        // compact branch, called ExpandCurrentScreen again and was also measured with
        // the compact width.
        _expanded = true;
        present();
        ApplyContentVisibility();
        _appliedPresentation = new IslandPresentationResult(
            IslandDesiredView.Expanded, CurrentViewFeatureId(), false, false);
        // With the view already expanded, the navigation arrows come in the same
        // turn.
        UpdateArrows();
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled)
        {
            SnapExpandedFrame();
            return;
        }
        if (wasExpanded && skipIfExpanded) return; // already expanded: only the content changed
        SetExpandedFrame();
    }

    // --- geometry shared by both ends ---

    /// <summary>
    /// Resting target of the compact: springs towards p=0 with the box alive.
    /// </summary>
    private void SetCompactFrame()
    {
        _pT = 0;
        _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
        EnsureLoop();
    }

    /// <summary>Animated start of the expanded view: springs towards p=1, q=1.</summary>
    private void SetExpandedFrame()
    {
        _pT = 1;
        _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
        EnsureLoop();
    }

    /// <summary>Expanded view without animation: final state applied in one go.</summary>
    private void SnapExpandedFrame()
    {
        SetInactiveRest(false);
        _p = _pT = 1; _pv = 0;
        _q = _qT = 1; _qv = 0;
        _hexpShown = _hexp;
        _pop = 0; _popPlaying = false;
        ApplyFrame();
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
    }
}
