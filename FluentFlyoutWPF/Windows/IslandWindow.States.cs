// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Media.Control;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// State machine of the container: which view is shown (nothing, the inactive piece,
/// compact or expanded) and how it gets there.
///
/// <para>Rules it upholds (change island-lista-de-activos):</para>
/// <list type="bullet">
/// <item><b>A single decision</b>: the view ALWAYS comes out of the ACTIVE EVENT LIST
/// (<c>IslandActivityRegistry</c>) translated by the pure policy
/// (<c>IslandPresentation</c>). Every route of every feature publishes its event and calls
/// <see cref="RefreshPresentation"/>; none of them decides on its own what is shown. That
/// same decision used to be written in six places with different guards —hence the
/// compacts that took long and the ones that never showed up at all.</item>
/// <item><b>Instant</b>: an active event is presented in the SAME turn. The geometry
/// catches up afterwards (springs), but nothing waits for it to settle: the inactive piece
/// is a DESTINATION (rest), not a mandatory step of the collapse.</item>
/// <item><b>Rest is decided by the setting</b>: narrow black piece or nothing
/// (001 MOD RF-2), and only with at least one usable screen: never an empty box.</item>
/// <item><b>The temporary notice keeps its deadline</b>: interacting does not extend it
/// (001 RF-2), and re-presenting the same notice does not either (its birth instant
/// decides).</item>
/// </list>
///
/// <para>Part of IslandWindow; the state lives in <c>IslandWindow.xaml.cs</c>, the
/// animation engine in <c>IslandWindow.Frame.cs</c> and the content in the cards
/// (<c>IslandWindow.FeatureCards.cs</c>).</para>
/// </summary>
public partial class IslandWindow
{
    private bool IsBoxShown => IslandBox.Visibility == Visibility.Visible;

    // ------------------------------------------------------------------
    // Active event list (change island-lista-de-activos)
    // ------------------------------------------------------------------

    /// <summary>List of what is happening right now: live activity, timed notices and exclusives.</summary>
    private readonly IslandActivityRegistry _activity = new();

    /// <summary>View applied in the last <see cref="ApplyPresentation"/>: it is what allows repair.</summary>
    private IslandPresentationResult? _appliedPresentation;

    /// <summary>Empty action for cards with no repaint of their own.</summary>
    private static readonly Action NoOp = static () => { };

    /// <summary>
    /// Features with their OWN activity: they live while something is happening (playing,
    /// counting, a reminder). The ones that only carry NOTICES —Bluetooth, charger,
    /// dictation— and the on-demand content ones —drawer, shelf, clipboard, weather— are
    /// not here: their view lives as long as the notice their presentation arms.
    /// </summary>
    private static readonly string[] LiveActivityFeatures =
        [IslandFeatureIds.Media, IslandFeatureIds.Timer, IslandFeatureIds.Calendar];

    /// <summary>
    /// Dumps the state of the features into the active event list. It runs on every
    /// resolution: repeating the same state does not touch the entries —their birth
    /// instant, which is the tie-breaker, survives the refreshes—.
    ///
    /// <para>In «Temporary notice» there is NO live activity: there the content lives as
    /// long as its notice, and that notice is armed by the presentation of the compact
    /// (001 RF-2).</para>
    /// </summary>
    private void PublishActivity()
    {
        var now = DateTime.UtcNow;
        bool temporalMode = SettingsManager.Current.IslandVisibilityMode == 1;
        var inScreens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (feature, _) in ScreenOrderedFeatures())
        {
            inScreens.Add(feature.Id);
            if (!LiveActivityFeatures.Contains(feature.Id)) continue;
            _activity.SetLive(feature.Id, FeatureIsLiveActivity(feature, temporalMode), now);
        }
        // A feature left without any screen has no view: its activity stops counting
        // (it used to stay in the list with nobody reading it).
        foreach (string id in LiveActivityFeatures)
        {
            if (!inScreens.Contains(id)) _activity.SetLive(id, false, now);
        }
        // Exclusive access: the timer alert and the dictation in progress, which the
        // policy makes prevail over any other activity.
        foreach (var feature in _features.Features)
            _activity.SetExclusive(feature.Id, feature.State.Exclusive, now);
    }

    /// <summary>
    /// Is this feature LIVE ACTIVITY under the given mode? In «Visible while active» it
    /// is while something is happening (playing, counting, a reminder). In
    /// «Temporary notice» the content lives as long as its deadline, with ONE exception
    /// that is a STATE and not a notice: a PAUSED session the user declared active holds
    /// the view until it resumes or the session is closed.
    ///
    /// <para>Without this the setting served no purpose in «Temporary notice»: pausing
    /// showed the compact for the configured deadline and then hid it completely, when
    /// what the user asked for with «pause counts as active» is for the pause to BE the
    /// view (change island-lista-de-activos).</para>
    /// </summary>
    private bool FeatureIsLiveActivity(IIslandFeature feature, bool temporalMode)
    {
        if (!temporalMode) return FeatureIsActiveNow(feature);
        return feature.Id switch
        {
            // Paused music: the setting decides whether a pause is a state.
            IslandFeatureIds.Media => SettingsManager.Current.IslandPauseCountsActive && MediaPausedNow(),
            // Paused countdown: it is still a countdown, with its remaining time on screen.
            IslandFeatureIds.Timer => _timer.State == IslandTimerState.Paused,
            _ => false,
        };
    }

    /// <summary>
    /// Is this feature happening NOW? Its own card declares its own activity (playing,
    /// counting); a feature without own activity declares it with its sustaining check, and
    /// a future one without a card, with its contract.
    /// </summary>
    private bool FeatureIsActiveNow(IIslandFeature feature) =>
        FeatureCard(feature.Id) switch
        {
            { OwnActivity: { } own } => own(),
            { } card => card.Sustains(),
            _ => feature.State.Active,
        };

    /// <summary>
    /// Features with a possible view, in SCREEN ORDER, followed by the ones that keep their
    /// own view outside them (notices and exclusives: their card does not depend on the user
    /// having placed them anywhere).
    /// </summary>
    private List<IIslandFeature> PresentationFeatures()
    {
        var list = new List<IIslandFeature>();
        foreach (var (feature, _) in ScreenOrderedFeatures()) list.Add(feature);
        foreach (var feature in _features.Features)
        {
            if (!ScreenlessFeatureKeepsOwnView(feature)) continue;
            if (list.Any(f => f.Id == feature.Id)) continue;
            list.Add(feature);
        }
        return list;
    }

    /// <summary>Feature holding the current view (null with a combined screen or with no view).</summary>
    private string? CurrentViewFeatureId() => _contentMode == IslandContentMode.Screen
        ? CompactMemberOfCurrentScreen()?.Id
        : ViewOwnerFeature()?.Id;

    /// <summary>Feature of the presented notice, if its deadline is still alive.</summary>
    private string? PresentedNoticeId(DateTime now) =>
        _noticeUntil > now ? _noticeFeatureId : null;

    /// <summary>Full snapshot for the policy: it is the only thing the policy reads.</summary>
    private IslandPresentationInput PresentationInput(bool userExpanded)
    {
        var now = DateTime.UtcNow;
        var features = PresentationFeatures();
        var ordered = new List<string>(features.Count);
        var usable = new List<string>(features.Count);
        foreach (var feature in features)
        {
            ordered.Add(feature.Id);
            if (feature.State.Usable) usable.Add(feature.Id);
        }
        // With an expanded view of its own: only the ones placed on a SCREEN. A notice or
        // an exclusive outside them (dictation) lives in the compact.
        var expandable = new List<string>(features.Count);
        foreach (var (feature, _) in ScreenOrderedFeatures()) expandable.Add(feature.Id);
        return new IslandPresentationInput(
            Suppressed: Suppressed(),
            ReturnToInactive: SettingsManager.Current.IslandReturnToInactive,
            UserExpanded: userExpanded,
            UserExpandedFeatureId: CurrentViewFeatureId(),
            AnyScreenUsable: AnyScreenUsable(),
            PresentedNoticeId: PresentedNoticeId(now),
            PresentedNoticeStarted: _noticeStarted,
            ScreenOrderedIds: ordered,
            ExpandableIds: expandable,
            UsableIds: usable,
            Activity: _activity,
            Now: now);
    }

    /// <summary>View to show RIGHT NOW according to the active list (pure, touches nothing).</summary>
    private IslandPresentationResult DesiredPresentation() =>
        IslandPresentation.Resolve(PresentationInput(userExpanded: _expanded));

    // ------------------------------------------------------------------
    // View application (SINGLE point)
    // ------------------------------------------------------------------

    /// <summary>
    /// Publishes the activity and applies the view that belongs to it. It is the SINGLE
    /// point through which the container enters a view: every feature route calls it, the
    /// settings, the notice expiry, the pointer and the startup.
    /// </summary>
    private void RefreshPresentation()
    {
        if (_disposed) return;
        PublishActivity();
        ApplyPresentation(DesiredPresentation());
    }

    /// <summary>
    /// Repairs the view if the active list changed while the geometry was in flight: it is
    /// resolved again and, if it is a different one, applied. It is the net that guarantees
    /// that the container NEVER stays on the piece while something is active (change
    /// island-lista-de-activos).
    /// </summary>
    private void RepairPresentation()
    {
        if (_disposed || !SettingsManager.Current.IslandEnabled) return;
        PublishActivity();
        var desired = DesiredPresentation();
        if (_appliedPresentation is { } applied
            && applied.View == desired.View && applied.FeatureId == desired.FeatureId)
            return;
        ApplyPresentation(desired);
    }

    /// <summary>
    /// Paints the desired view. The FACE (the card of the feature with its content) is
    /// always presented in the same turn; the animation only decides the geometry. With
    /// the «Temporary notice» mode and no live notice it falls back to rest (001 RF-2).
    /// </summary>
    private void ApplyPresentation(IslandPresentationResult desired)
    {
        // Idempotence: the view that is ALREADY applied is not presented again (it would
        // repaint the piece and restart its micro-animation on every mailbox pass). The
        // compact is let through: there it may have to arm the deadline of a new notice.
        if (desired.View != IslandDesiredView.Compact
            && _appliedPresentation is { } applied
            && applied.View == desired.View && applied.FeatureId == desired.FeatureId)
            return;
        _appliedPresentation = desired;
        if (_disposed) return;
        if (!SettingsManager.Current.IslandEnabled)
        {
            SnapHidden();
            Visibility = Visibility.Collapsed;
            return;
        }
        switch (desired.View)
        {
            case IslandDesiredView.Hidden:
                _expanded = false;
                // Already hidden: there is nothing to collapse (and the current notice is
                // left alone, it may be waiting for its own event).
                if (!IsBoxShown && _qT == 0) return;
                GoHidden();
                return;
            case IslandDesiredView.Inactive:
                _expanded = false;
                ShowInactive();
                return;
            case IslandDesiredView.Expanded:
                PresentExpanded(desired);
                return;
            default:
                PresentCompact(desired);
                return;
        }
    }

    /// <summary>
    /// Presents the COMPACT of the active feature. If its face is already in front, only its
    /// content is repainted and —if the notice is new— its deadline is armed: repeating the
    /// same view does not reanimate anything.
    /// </summary>
    private void PresentCompact(IslandPresentationResult desired)
    {
        var feature = desired.FeatureId == null ? null : FeatureById(desired.FeatureId);
        if (feature == null || !feature.State.Usable)
        {
            // The active feature stopped being presentable in the last instant: rest,
            // never an empty box (001 MOD RF-9).
            PresentRest();
            return;
        }
        _expanded = false;
        var mode = ModeForFeature(feature.Id);
        // The presented notice belonged to another feature: it is forgotten along with its view.
        if (_noticeFeatureId != null && _noticeFeatureId != feature.Id) ClearTemporaryNotice();
        if (IsBoxShown && !_inactiveShown && _inactiveTt == 0 && _contentMode == mode)
        {
            FeatureCard(feature.Id)?.Refresh();
            ApplyContentVisibility();
            ArmTemporaryHide(restart: desired.RestartNotice, force: desired.AlwaysTemporal);
            UpdateLine();
            // The geometry also has to reach the compact: if the user had the expanded view
            // open, this is the ONLY transition (expanded → compact, without going through
            // the piece), and the face of the compact is already presented.
            if (AnimationsEnabled) SetCompactFrame();
            return;
        }
        ShowCompactView(mode, feature, FeatureCard(feature.Id)?.Refresh ?? NoOp,
            forceNotice: desired.AlwaysTemporal, restartNotice: desired.RestartNotice);
    }

    /// <summary>
    /// Presents the EXPANDED view of the feature holding it (the current exclusive or the
    /// one the user has open). With its expanded view already in front it only repaints:
    /// the final alert is forced so it prevails over any view (002 RF-2/RF-6).
    /// </summary>
    private void PresentExpanded(IslandPresentationResult desired)
    {
        var feature = desired.FeatureId == null ? null : FeatureById(desired.FeatureId);
        if (feature == null)
        {
            // No identifiable owner (a screen without a concrete member): the current view
            // is repainted as it is.
            if (IsBoxShown) { ApplyContentVisibility(); SyncMeasuredHeight(); }
            return;
        }
        bool alert = feature.Id == IslandFeatureIds.Timer && _timer.State == IslandTimerState.Alerting;
        var mode = ModeForFeature(feature.Id);
        if (_expanded && _contentMode == mode && !alert)
        {
            FeatureCard(feature.Id)?.Refresh();
            ApplyContentVisibility();
            SyncMeasuredHeight();
            _pT = 1;
            _qT = 1;
            if (AnimationsEnabled) EnsureLoop();
            return;
        }
        ShowExpandedView(mode, feature, FeatureCard(feature.Id)?.Refresh ?? NoOp, skipIfExpanded: alert);
    }

    // --- rest ---

    /// <summary>
    /// Rest of the container (001 MOD RF-2): inactive piece or nothing depending on the
    /// setting; when suppressed, hidden (the piece is suppressed too, RF-14).
    /// </summary>
    private void PresentRest()
    {
        if (Suppressed() && !HasExclusive()) { SnapHidden(); return; }
        if (!ReturnToInactive()) { GoHidden(); return; }
        ShowInactive();
    }

    /// <summary>Is there any SCREEN with something usable? (001 MOD RF-9): without a usable screen there is no view to anchor.</summary>
    private bool AnyScreenUsable() => UsableScreenCount() > 0;

    /// <summary>
    /// The «return to inactive» toggle decides the rest: visible black piece or nothing
    /// (001 MOD RF-2, inactive visible by default). Without usable features there is no
    /// piece: an empty box is never anchored.
    /// </summary>
    private bool ReturnToInactive() =>
        SettingsManager.Current.IslandReturnToInactive && AnyScreenUsable();

    /// <summary>
    /// Inactive state (001 MOD RF-11, RF-16): a black pill narrower than the compact, with
    /// no content view at all; hover only grows it and a click opens the last usable one.
    /// </summary>
    private void ShowInactive()
    {
        // Diagnostic of the activity contract: the piece should not rest while media is
        // playing (001 MOD RF-4). If it shows up in the log, the active list let a session
        // through.
        if (SettingsManager.Current.IslandVisibilityMode == 0 && !_expanded
            && _music?.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            Logger.Warn("Island: resting on the piece with media playing " +
                "(snapshot={Snap}, allowed while playing={Playing}); check the active list",
                _music?.Id, NewestPlaying() != null);
        ClearTemporaryNotice();
        // The collapse starts from the expanded geometry: the compact must not blossom on
        // the way while the width morphs into the piece one (001 MOD RF-16).
        _collapseFromExpanded = _expanded || _p > 0.02;
        _hidingViaCompact = false;
        _expanded = false;
        _contentMode = IslandContentMode.Media;
        _inactiveHot = false;
        // Rest forgets the shared height: the next deployment measures again (001 MOD RF-15).
        ResetSharedHeight();
        SetInactiveRest(true);
        // Animated when the piece does not dominate the view yet (content to fade) or when
        // the geometry is not the resting one yet (expanded view to morph): in both cases
        // there is something to interpolate.
        if (AnimationsEnabled && IsBoxShown && (_inactiveT < 1 || _p > 0.05))
        {
            _pT = 0;
            _pv = 0;
            _qT = 1;
            PositionTopCenter();
            IslandBox.Visibility = Visibility.Visible;
            UpdateRotationPauseState();
            UpdateLine();
            EnsureLoop();
            return;
        }
        // Immediate rest (animations off or a box that just appeared): with no content to
        // fade, the piece is cleaned in one go.
        _p = _pT = 0; _pv = 0;
        _q = _qT = 1; _qv = 0;
        _inactiveHotT = 0;
        ClearInactiveResidue();
        PositionTopCenter();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Visible;
        UpdateRotationPauseState();
        UpdateLine();
        UpdateMediaStatusDot();
    }

    /// <summary>
    /// Sets the target of the inactive rest. Towards the piece (on) it fades the current
    /// view into it; towards content it delegates to <see cref="EnterContent"/>. With the
    /// box hidden (or animations off) the progress jumps straight there: there is no content
    /// to fade and no morphology to interpolate, and it would start off at the wrong width.
    /// </summary>
    private void SetInactiveRest(bool on)
    {
        if (!on) { EnterContent(); return; }
        _inactiveTt = 1;
        _inactiveShown = true;
        if (!AnimationsEnabled || !IsBoxShown)
        {
            _inactiveT = 1;
            return;
        }
        EnsureLoop();
    }

    /// <summary>
    /// SINGLE entry point to content (compact or expanded, 001 MOD RF-16): while a feature is
    /// on screen, the inactive rest neither applies nor leaves residue. The rest clock is
    /// re-aimed at content and travels from wherever it is: if the piece already dominates
    /// the view, the content fades outwards from it; if the collapse was halfway, it turns
    /// back from its current progress (no jumps: snapping it to 0 was precisely the hard cut
    /// seen when re-aiming in flight).
    /// </summary>
    private void EnterContent()
    {
        _collapseFromExpanded = false;
        _inactiveShown = false;
        _inactiveTt = 0;
        if (_inactiveT <= 0) return;
        if (AnimationsEnabled && IsBoxShown) { EnsureLoop(); return; }
        // Without animations (or with the box out of the tree) there is nothing to
        // interpolate: the progress snaps to the destination.
        _inactiveT = 0;
    }

    /// <summary>
    /// End of the transition to inactive: now the residual content is really removed
    /// (001 MOD RF-11) and the indicators are turned off. It is called only when the
    /// progress reached 1, never halfway through the flight.
    /// </summary>
    private void FinishInactive()
    {
        // The piece is the final view of this collapse: the next reopening goes through the
        // normal route (and that way the content can blossom).
        _collapseFromExpanded = false;
        ClearInactiveResidue();
        UpdateLine();
        UpdateMediaStatusDot();
    }

    /// <summary>
    /// Real cleanup of the inactive state (001 MOD RF-11): fading the layers by opacity is
    /// not enough, the residual content (compact grids with the last feature, cover art,
    /// background, timer titles and texts) is really removed. Restoration goes through the
    /// normal routes (ApplyContentVisibility + the repaint of the card when showing the
    /// compact or the expanded view).
    /// </summary>
    private void ClearInactiveResidue()
    {
        HideAllContentLayers();
        ClearMusicResidue();
        TimerRemaining.Text = "00:00:00";
        TimerProgressFill.Width = 0;
        TimerRunRemaining.Text = "00:00:00";
        RestoreExpandedHomes();
        ApplyFrame();
    }

    // --- instant transitions (no animations) ---

    private void GoHidden()
    {
        if (!AnimationsEnabled || !IsBoxShown) { SnapHidden(); return; }
        if (_hidingViaCompact) return;
        // Hidden has no height to keep: the next deployment measures again (001 MOD RF-15).
        ResetSharedHeight();
        if (Math.Abs(_p) > 0.05)
        {
            _expanded = false; _hidingViaCompact = true; _pT = 0; _qT = 0; EnsureLoop(); return;
        }
        _qT = 0;
        EnsureLoop();
    }

    private void SnapCompact()
    {
        // State applied in one go: there is no applied view to compare against (the next
        // resolution presents it again).
        _appliedPresentation = null;
        _inactiveShown = false;
        _collapseFromExpanded = false;
        _inactiveTt = 0; _inactiveT = 0;
        _p = _pT = 0; _pv = 0;
        _q = _qT = 1; _qv = 0;
        _hexpShown = _hexp;
        _pop = 0; _popPlaying = false;
        StopLoop();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
        SyncEq();
        UpdateVisibleRefresh();
    }

    private void SnapHidden()
    {
        _appliedPresentation = null;
        _hidingViaCompact = false;
        _inactiveShown = false;
        _collapseFromExpanded = false;
        ResetSharedHeight();
        ClearTemporaryNotice();
        _inactiveTt = 0; _inactiveT = 0;
        _p = _pT = 0; _pv = 0;
        _q = _qT = 0; _qv = 0;
        _hexpShown = _hexp;
        _pop = 0; _popPlaying = false;
        StopLoop();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Collapsed;
        UpdateRotationPauseState();
        UpdateLine();
        UpdateMediaStatusDot();
        SyncEq();
        UpdateVisibleRefresh();
    }

    // ------------------------------------------------------------------
    // Collapse routes (they resolve the view again: they no longer decide it)
    // ------------------------------------------------------------------

    /// <summary>
    /// Something stopped having a view (it was turned off, closed, stopped being
    /// presentable) or the view has to be reconsidered: the container resolves it again
    /// with the active list. Collapsing is not deciding: if something is still active, its
    /// compact is shown.
    /// </summary>
    private void HidePerMode()
    {
        UpdateRotationPauseState();
        // Final alert: it is exclusive and modal until it is closed (002 RF-2); the policy
        // itself puts it back, so there is nothing to reconsider.
        if (_timer.State == IslandTimerState.Alerting) return;
        _expanded = false;
        RefreshPresentation();
    }

    /// <summary>
    /// The current view collapses into whatever applies: the active feature still alive or
    /// rest (001 MOD RF-2, RF-4). It is the point the six scattered decisions went through.
    /// </summary>
    private void ShowInactiveOrHidden()
    {
        _expanded = false;
        RefreshPresentation();
    }

    /// <summary>
    /// The pointer moved away from the expanded Island AND its tolerance already expired
    /// (<see cref="HoverLeaveGraceMs"/>): the user's expanded view ends here and the view is
    /// resolved again with the active list —with music playing the compact is the music,
    /// never the piece (001 MOD RF-4)—.
    /// </summary>
    private void CollapseFromHover()
    {
        if (!_expanded) return;
        // Final alert (exclusive): it stays until stop or restart, even if the mouse goes
        // away (001 MOD RF-4).
        if (_timer.State == IslandTimerState.Alerting) return;
        _expanded = false;
        RefreshPresentation();
    }

    /// <summary>
    /// Feature that SUSTAINS the current view according to the active list (null if there is
    /// none). It is what a click on the piece opens and the starting point of the collapses
    /// that keep content: a single resolution for all of them (001 MOD RF-4).
    /// </summary>
    private IIslandFeature? ResolveActiveVigenteForVisible()
    {
        var result = IslandPresentation.Resolve(PresentationInput(userExpanded: false));
        return result.FeatureId is { } id ? FeatureById(id) : null;
    }

    /// <summary>
    /// Is the feature still sustaining the view NOW? (its own activity or its live notice).
    /// With a combined screen in front ANY of its members sustains it (change
    /// island-pantallas RF-4).
    /// </summary>
    private bool FeatureSustainsView(IIslandFeature feature)
    {
        if (_contentMode == IslandContentMode.Screen && ScreenIsCombined()) return ScreenSustainsView();
        return SingleFeatureSustainsView(feature);
    }

    /// <summary>
    /// Expands the last usable one with a safe collapse: if the chosen one stops being
    /// presentable in the last instant, it tries the next usable one; if there is none, it
    /// resolves the rest without opening an empty box (RF-3, RF-9, RF-16).
    /// </summary>
    private bool ExpandLastUsable()
    {
        // Dictation has priority (RF-10): with a session in progress no screen is opened
        // on top of its card.
        if (DictationActive()) return false;
        if ((Suppressed() && !HasExclusive()) || !SettingsManager.Current.IslandEnabled) { SnapHidden(); return false; }
        // The unit of the view is the SCREEN (change island-pantallas RF-4): a click opens
        // the screen of the feature the user has IN FRONT —the compact shows a single one—
        // and, with no compact on screen (the resting piece), the one of the current active
        // feature.
        if ((ShownCompactFeature() ?? ResolveActiveVigenteForVisible()) is { } target)
        {
            int index = ResolveScreenIndexFor(target.Id);
            if (index >= 0) _screenIndex = index;
        }
        for (int step = 0; step < _screens.Count; step++)
        {
            int index = WrapUnit(_screenIndex + step, _screens.Count);
            if (ScreenUsableFeatures(_screens[index]).Count == 0) continue;
            _screenIndex = index;
            if (ExpandCurrentScreen()) return true;
        }
        HidePerMode();
        return false;
    }

    // ------------------------------------------------------------------
    // Temporary notice (001 RF-2, 002 RF-16): one more entry in the list
    // ------------------------------------------------------------------

    /// <summary>
    /// Arms the deadline of the notice of the view that was just presented. And with it
    /// publishes the ENTRY in the active list: that is what keeps the view being the desired
    /// one while its deadline runs (and stops it when it expires).
    ///
    /// <list type="bullet">
    /// <item><b>force</b> = true for the contents whose notice is ALWAYS temporary, even when
    /// the mode is «Visible while active» (Bluetooth, charger: their view is a notification,
    /// not a state).</item>
    /// <item><b>restart</b> = false keeps the deadline that was already running: interacting
    /// —expanding and coming back— or re-presenting the same notice never extends it
    /// (001 RF-2). A new event (a connection, a copy, a track) does start a new deadline.</item>
    /// </list>
    /// </summary>
    private void ArmTemporaryHide(bool restart = true, bool force = false)
    {
        var now = DateTime.UtcNow;
        // A countdown action done inside the expanded view leaves its notice PENDING: the
        // timer deadline starts when its compact is presented, not when Start is pressed
        // (002 RF-16), and that is exactly what just happened.
        if (_pendingTimerNotice) { restart = true; _pendingTimerNotice = false; }
        string? id = FeatureCardOfMode(_contentMode)?.Id ?? CompactMemberOfCurrentScreen()?.Id;
        // A view that already sustains its ACTIVITY needs no deadline: a pause that counts
        // as active or a paused countdown are STATES, and their view does not expire. Arming
        // them a notice would make them re-arm and expire every few seconds with nothing
        // having changed.
        if (id != null && _activity.HasLive(id, now))
        {
            ClearTemporaryNotice();
            return;
        }
        // The arming rule lives only once, in the policy (IslandPolicy.cs): with no notice
        // to arm, this view's one is forgotten.
        if (id == null || !IslandNoticePolicy.Arms(force, SettingsManager.Current.IslandVisibilityMode == 1, HasExclusive()))
        {
            ClearTemporaryNotice();
            return;
        }
        if (restart || !_activity.NoticeAlive(id, now))
        {
            int ms = Math.Clamp(SettingsManager.Current.IslandVisibilityDuration, 1000, 10000);
            _activity.Pulse(id, now, now.AddMilliseconds(ms), force);
        }
        _noticeFeatureId = id;
        _noticeStarted = _activity.NoticeStarted(id, now);
        _noticeForced = force;
        _noticeUntil = _activity.NoticeExpiry(id, now);
        ScheduleNoticeRetraction();
    }

    /// <summary>
    /// The notice was expanded: the deadline keeps running, only its collapse is postponed
    /// until the view becomes compact again. Without this the expiry died inside the
    /// expanded view and the notice stayed stuck forever.
    /// </summary>
    private void HoldTemporaryNotice() => _noticeVersion++;

    /// <summary>
    /// Closes the presented notice: there is no pending deadline to honour nor any entry
    /// sustaining the view (the active list stops counting it and the container resolves it
    /// again on the next pass, not before: forgetting a notice does not change the view).
    /// </summary>
    private void ClearTemporaryNotice()
    {
        _noticeVersion++;
        _noticeCheckActive = false;
        _noticeUntil = DateTime.MinValue;
        _noticeForced = false;
        // Only the NOTICE: if the feature also has live activity (a pause that counts as
        // active, a paused countdown), that one still sustains its view.
        if (_noticeFeatureId is { } id) _activity.ForgetNotice(id);
        _noticeFeatureId = null;
        _noticeStarted = DateTime.MinValue;
        _pendingTimerNotice = false;
    }

    /// <summary>
    /// Is the notice of THIS view still alive? With entries per feature it can no longer be
    /// confused: a timer notice does not declare the charger or Bluetooth active.
    /// </summary>
    private bool NoticeAliveFor(IslandContentMode mode)
    {
        string? id = FeatureCardOfMode(mode)?.Id;
        return id != null && _activity.NoticeAlive(id, DateTime.UtcNow);
    }

    /// <summary>
    /// Schedules the expiry of the notice with a ONE-SHOT timer (no heartbeat): it collapses
    /// at the absolute time of the deadline. If the shot were lost, the 5 s recovery detects
    /// it and honours it.
    /// </summary>
    private void ScheduleNoticeRetraction()
    {
        int version = ++_noticeVersion;
        TimeSpan wait = _noticeUntil - DateTime.UtcNow;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        ScheduleNoticeCheck(version, wait);
    }

    /// <summary>
    /// Leaves a single check of the notice armed. <c>_noticeCheckActive</c> avoids duplicated
    /// chains when the slow recovery repairs a lost shot.
    /// </summary>
    private void ScheduleNoticeCheck(int version, TimeSpan wait)
    {
        _noticeCheckActive = true;
        _ = Task.Delay(wait).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            _noticeCheckActive = false;
            RetractTemporaryNotice(version);
        }));
    }

    /// <summary>
    /// Collapse of the temporary notice when its deadline expires. Neither the pointer nor the
    /// data updates restart the deadline, but the notice does not close under the cursor
    /// either while it is expanded: it is retried until the view is compact again and the
    /// mouse is not in the way, so it never gets stuck (001 RF-2, 002 RF-8/RF-16).
    /// </summary>
    private void RetractTemporaryNotice(int version)
    {
        if (_disposed || version != _noticeVersion) return;
        var now = DateTime.UtcNow;
        string? id = _noticeFeatureId;
        var step = IslandNoticePolicy.OnExpired(
            forced: _noticeForced,
            temporalMode: SettingsManager.Current.IslandVisibilityMode == 1,
            hasExclusive: HasExclusive(),
            boxShown: IsBoxShown,
            expanded: _expanded,
            pointerOver: IsMouseOverBoxOrStrip());
        if (step == IslandNoticeStep.Retry)
        {
            // The view cannot collapse now: the same notice is given a SHORT extra deadline
            // —renewing it, so its birth instant is untouched— and it is retried.
            if (id != null && !_activity.NoticeAlive(id, now))
                _activity.Renew(id, now.AddMilliseconds(250));
            ScheduleNoticeCheck(version, TimeSpan.FromMilliseconds(250));
            return;
        }
        ClearTemporaryNotice();
        if (step == IslandNoticeStep.Forget) return;
        RefreshPresentation();
    }
}
