// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// SCREENS of the Island (change island-pantallas): the container does not navigate
/// through loose features —nor does it order them with any separate list— but through
/// configured screens, and a screen can carry SEVERAL features at once.
///
/// <para>Rules it upholds:</para>
/// <list type="bullet">
/// <item><b>The screen is the view</b>: when a feature takes the stage (playing,
/// counting, warning…), the Island presents ITS SCREEN, not the view of the loose
/// feature. With a single usable feature that screen is its usual rich view and nothing
/// changes (RF-1); with several, the COMPACT shows ONE of them —its rich view— and the
/// EXPANDED view shows the whole screen, with one column per feature (RF-2/RF-3).</item>
/// <item><b>The compact does not group</b>: a screen with several features shows ONE
/// in the compact (the one holding the view: the one playing, counting or warning), with
/// the rich view of that feature; the others wait for the expanded view. A click on the
/// compact opens the screen that feature belongs to, which is where they are all seen
/// together.</item>
/// <item><b>No screen, no view</b>: a feature that is on no screen is not shown even if
/// it is active; the container resolves another screen or rest, never its loose view.</item>
/// <item><b>Up to four per screen</b>
/// (<see cref="IslandFeatureIds.MaxFeaturesPerScreen"/>): those are the ones that fit
/// on a single screen, from left to right. The setting stores no more, and the width of
/// the columns is shared out so that all of them are seen whole.</item>
/// <item><b>Its own order</b> (RF-2/RF-3): the order of a screen is the order of its
/// features, and it is the one the expanded columns follow —and the choice of which
/// feature occupies the compact—, always from left to right. There is no other order
/// list.</item>
/// <item><b>A single navigation</b> (RF-4): the wheel and the arrows walk through
/// screens (not features) and skip the ones with nothing usable; the «current active»
/// of «Visible while active» resolves to its screen, so that the compact shows the
/// active feature of that group (a single one).</item>
/// <item><b>Without configuration the usual rules apply</b> (RF-5): one screen per
/// feature, in the default order.</item>
/// </list>
///
/// <para>Part of IslandWindow; the feature registry is in <c>IslandFeatures.cs</c> and
/// the presentation routes in <c>IslandWindow.Views.cs</c>.</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>Resting width of the compact of a single feature (CompactLayer in the XAML).</summary>
    private const double SingleCompactLayerWidth = 240;
    /// <summary>MAXIMUM width of a column of the expanded view of a combined screen.</summary>
    private const double ScreenColumnWidth = 236;
    /// <summary>Gap between columns of the expanded view (same as the XAML margin).</summary>
    private const double ScreenColumnGap = 14;
    /// <summary>Side padding of the expanded content (the ExpandedLayer margin: 16+16).</summary>
    private const double ScreenRowPadding = 32;
    /// <summary>
    /// Maximum width of the expanded view of a combined screen: with four columns (the
    /// maximum of <see cref="IslandFeatureIds.MaxFeaturesPerScreen"/>) they fit at full
    /// width, and it never goes past the monitor.
    /// </summary>
    private const double ScreenExpandedMaxWidth = 1100;

    /// <summary>Configured screens, already sanitized and in navigation order.</summary>
    private readonly List<string[]> _screens = [];
    /// <summary>Current screen (index into <see cref="_screens"/>).</summary>
    private int _screenIndex;

    /// <summary>
    /// Re-reads the configured screens and leaves the current one at a valid index. It
    /// runs at startup and on every settings change; it does NOT touch the view (whoever
    /// changes screen is the navigation, or the setting change that presents it again).
    /// </summary>
    public void ApplyScreens()
    {
        _screens.Clear();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in SettingsManager.Current.IslandScreens)
        {
            var ids = IslandFeatureIds.ParseScreen(raw)
                .Where(assigned.Add)
                .ToList();
            if (ids.Count > 0) _screens.Add([.. ids]);
        }
        // With no configured screen the factory ones apply, PARSED: wrapping each string
        // («media+timer+apps+shelf») in a single-element array used to leave screens
        // containing no known feature —none usable— and the container ended up with
        // nothing to present (not even the music).
        if (_screens.Count == 0)
            _screens.AddRange(IslandFeatureIds.DefaultScreens
                .Select(screen => IslandFeatureIds.ParseScreen(screen).ToArray())
                .Where(ids => ids.Length > 0));
        _screenIndex = Math.Clamp(_screenIndex, 0, _screens.Count - 1);
    }

    private IReadOnlyList<string> CurrentScreenIds() =>
        _screens.Count == 0 ? [] : _screens[Math.Clamp(_screenIndex, 0, _screens.Count - 1)];

    /// <summary>Usable features of a screen, in the order of the screen.</summary>
    private List<IIslandFeature> ScreenUsableFeatures(IReadOnlyList<string> ids)
    {
        var members = new List<IIslandFeature>();
        foreach (var id in ids)
        {
            if (FeatureById(id) is { } feature && feature.State.Usable) members.Add(feature);
        }
        return members;
    }

    /// <summary>Usable members of the current screen.</summary>
    private List<IIslandFeature> CurrentScreenFeatures() => ScreenUsableFeatures(CurrentScreenIds());

    /// <summary>Does the current screen carry several features? (then its expanded view is made of columns)</summary>
    private bool ScreenIsCombined() => CurrentScreenFeatures().Count > 1;

    /// <summary>First screen that contains the given feature (-1 if it is on none).</summary>
    private int ScreenIndexOfFeature(string id)
    {
        for (int i = 0; i < _screens.Count; i++)
            for (int j = 0; j < _screens[i].Length; j++)
                if (string.Equals(_screens[i][j], id, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// Screen «of» a feature, used to resolve its view. The configuration is sanitized
    /// so that each feature belongs to at most one screen, but the lookup stays defensive
    /// in case an old collection arrives.
    /// </summary>
    private int ResolveScreenIndexFor(string id)
    {
        return ScreenIndexOfFeature(id);
    }

    /// <summary>Content mode of a feature: its card declares it (IslandWindow.FeatureCards.cs).</summary>
    private IslandContentMode ModeForFeature(string id) =>
        FeatureCard(id)?.Mode ?? IslandContentMode.Media;

    /// <summary>
    /// Is the feature on some screen? Without a screen there is no view: a feature that
    /// was left out of all of them (e.g. after deleting its screen) is not shown even if
    /// it is active.
    /// </summary>
    private bool FeatureInAnyScreen(IIslandFeature feature) => ResolveScreenIndexFor(feature.Id) >= 0;

    /// <summary>
    /// A feature without a screen has NO view (change island-pantallas)… except:
    /// <list type="bullet">
    /// <item>an EXCLUSIVE one: the final timer alert needs a surface to be closable
    /// (002 RF-2), and with no view it would stay blocking the container with no way to
    /// remove it;</item>
    /// <item>a NOTICE (<see cref="IslandFeatureIds.IsNotice"/>): its view is a temporary
    /// card —Bluetooth device, charger— that has no expanded view and does not take part
    /// in navigation, so it cannot depend on the user having placed it on a screen. That
    /// was the bug: with a setting missing that screen, the notice was lost.
    /// </item>
    /// </list>
    /// </summary>
    private static bool ScreenlessFeatureKeepsOwnView(IIslandFeature feature) =>
        feature.State.Exclusive || IslandFeatureIds.IsNotice(feature.Id);

    /// <summary>
    /// Is the screen of the feature COMBINED right now? (several usable features). Then
    /// its EXPANDED view is not the loose view of the feature but the whole screen: one
    /// column per feature, from left to right.
    /// </summary>
    private bool ScreenOfFeatureIsCombined(IIslandFeature feature)
    {
        int index = ResolveScreenIndexFor(feature.Id);
        return index >= 0 && ScreenUsableFeatures(_screens[index]).Count > 1;
    }

    /// <summary>
    /// Resolves the presentation of a feature to its SCREEN. It always adopts its index
    /// (it is the screen a later click will open) and, only with the EXPANDED view in
    /// front, rewrites the mode and the content to the composition of the screen: the
    /// compact shows ONE feature with its rich view, never the group. It returns false if
    /// the feature is on no screen: without a screen there is no view to present.
    /// </summary>
    private bool AdoptScreenFor(IIslandFeature feature, bool expanded, ref IslandContentMode mode, ref Action present)
    {
        int index = ResolveScreenIndexFor(feature.Id);
        if (index < 0) return false;
        _screenIndex = index;
        if (!expanded || mode == IslandContentMode.Screen) return true;
        if (ScreenUsableFeatures(_screens[index]).Count > 1)
        {
            mode = IslandContentMode.Screen;
            present = RefreshScreenMembers;
        }
        return true;
    }

    /// <summary>
    /// Features registered IN SCREEN ORDER (one entry per feature with its position): it
    /// is the order that replaces the old sortable list, both to break the tie of the
    /// current active feature and for any walk through the container. The ones that are on
    /// no screen stay out.
    /// </summary>
    private IEnumerable<(IIslandFeature Feature, int Order)> ScreenOrderedFeatures()
    {
        for (int i = 0; i < _screens.Count; i++)
        {
            for (int j = 0; j < _screens[i].Length; j++)
            {
                if (FeatureById(_screens[i][j]) is { } feature)
                    yield return (feature, i * IslandFeatureIds.MaxFeaturesPerScreen + j);
            }
        }
    }

    /// <summary>
    /// Feature holding the current view (null with a screen in front, at rest, or when
    /// the view belongs to an unknown feature).
    /// </summary>
    private IIslandFeature? ViewOwnerFeature() =>
        FeatureCardOfMode(_contentMode) is { } card ? FeatureById(card.Id) : null;

    /// <summary>
    /// Feature the user has IN FRONT in the COMPACT (null with the expanded view in
    /// front, on the resting piece or with no box): it decides which screen a click opens,
    /// because the compact shows a single feature.
    /// </summary>
    private IIslandFeature? ShownCompactFeature() =>
        IsBoxShown && !_expanded && !_inactiveShown ? ViewOwnerFeature() : null;

    /// <summary>Screens with something usable right now: it is the navigation unit (RF-4).</summary>
    private int UsableScreenCount() => _screens.Count(screen => ScreenUsableFeatures(screen).Count > 0);

    /// <summary>Does the current screen contain this feature?</summary>
    private bool CurrentScreenContains(string id) => CurrentScreenIds().Contains(id);

    /// <summary>
    /// Does the current view show this feature? With a SCREEN in front its members show
    /// it (a combined screen can have several at once); with a rich view, only its own.
    /// It is the gatekeeper of the «paint what is already in front» refreshes of each
    /// feature.
    /// </summary>
    private bool ViewShowsFeature(string id) =>
        _contentMode == IslandContentMode.Screen ? CurrentScreenContains(id) : ViewOwnerFeature()?.Id == id;

    /// <summary>
    /// Is the current view the EXPANDED view of a screen that contains the music? Then
    /// the screen owns the composition and a music event only repaints its content: it
    /// changes neither the mode nor the layers (if it did, the expanded view would jump
    /// from the columns to the loose music view and the screen would disappear).
    /// </summary>
    private bool ScreenOwnsMediaView() =>
        _contentMode == IslandContentMode.Screen && CurrentScreenContains(IslandFeatureIds.Media);

    /// <summary>
    /// The current view lost one of its members (a feature was turned off, its session was
    /// closed…): if it was a SCREEN, it is recomposed with the ones left —and it goes back
    /// to the rich view of its single feature if only one remains—. It returns true if the
    /// screen already resolved the view (nothing left to reconsider).
    /// </summary>
    private bool RecoverScreensAfterMemberLost()
    {
        if (_contentMode != IslandContentMode.Screen) return false;
        ApplyContentVisibility();
        SyncMeasuredHeight();
        return true;
    }

    /// <summary>
    /// Presents the COMPACT of the given feature inside ITS screen: it adopts the screen
    /// —the one a later click will open— and shows the rich view of THAT feature, because
    /// the compact shows a single one. It is the point through which the «current active»
    /// routes give the view back what is active. Without a screen (the feature was left
    /// out of all of them) it presents nothing and returns false.
    /// </summary>
    private bool ShowScreenOfFeature(IIslandFeature feature)
    {
        int index = ResolveScreenIndexFor(feature.Id);
        if (index < 0) return false;
        _screenIndex = index;
        return feature.TryShowCompact();
    }

    /// <summary>
    /// Presents the compact of the current screen: the rich view of ONE single one of its
    /// features (the one holding the view), never the whole group.
    /// </summary>
    private bool ShowCurrentScreenCompact()
    {
        var member = CompactMemberOfCurrentScreen();
        return member != null && member.TryShowCompact();
    }

    /// <summary>
    /// Feature that occupies the COMPACT of the current screen: the compact shows ONE —the
    /// first one holding the view (music playing, timer counting, a live notice) and, if
    /// none holds it, the first usable one—, and that is the one a click opens (its whole
    /// screen).
    /// </summary>
    private IIslandFeature? CompactMemberOfCurrentScreen()
    {
        var members = CurrentScreenFeatures();
        foreach (var member in members)
        {
            if (SingleFeatureSustainsView(member)) return member;
        }
        return members.Count > 0 ? members[0] : null;
    }

    /// <summary>
    /// Resting width of the compact of the current screen: the one declared by the
    /// feature occupying it (the compact never groups several).
    /// </summary>
    private double CompactWidthOfCurrentScreen() =>
        CompactMemberOfCurrentScreen() is { } member ? Math.Clamp(member.CompactWidth, 160, 360) : CompactPillWidth;

    /// <summary>
    /// Expands the current screen: the rich view of its single feature if it is simple, and
    /// the WHOLE screen —its columns, from LEFT TO RIGHT— if it is combined. It is what
    /// you see when clicking the compact. A combined screen with no columns to show (only
    /// notices that live in the compact) opens nothing: returning false gives way to the
    /// next screen that can.
    /// </summary>
    private bool ExpandCurrentScreen()
    {
        var members = CurrentScreenFeatures();
        if (members.Count == 0) return false;
        if (members.Count == 1) return members[0].TryShowExpanded();
        if (CurrentScreenColumnCount() == 0) return false;
        ShowExpandedView(IslandContentMode.Screen, members[0], RefreshScreenMembers);
        return true;
    }

    /// <summary>
    /// The current screen gives way to another one that does have something usable: the
    /// next one in navigation order is looked up and adopted as current. It is used when
    /// a settings change leaves the current screen with nothing to show (a feature turned
    /// off, an edited screen) so as not to be left with an empty view.
    /// </summary>
    private bool MoveToNearestUsableScreen()
    {
        for (int step = 0; step < _screens.Count; step++)
        {
            int index = (_screenIndex + step) % _screens.Count;
            if (ScreenUsableFeatures(_screens[index]).Count == 0) continue;
            _screenIndex = index;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The screens setting changed (or a feature was turned off, or the current feature
    /// entered/left a screen): the configuration is re-read and, if the Island is on
    /// screen, the current view —compact or expanded— is presented again so that the change
    /// is SEEN right away and the container adapts (width and height of the new content).
    /// While hidden nothing is deployed: the next appearance already uses the new
    /// configuration.
    /// </summary>
    public void RefreshScreensContent() => Dispatcher.Invoke(() =>
    {
        ApplyScreens();
        if (!IsBoxShown || _disposed) return;
        // The current view is presented again when it is a SCREEN (its composition may
        // have changed) or when it is the view of a feature that was left without a screen
        // (without a screen there is no view). The compact of a feature does not change
        // because its screen became combined: it is still its rich view.
        var owner = ViewOwnerFeature();
        bool repaint = _contentMode == IslandContentMode.Screen
            || (owner != null && !FeatureInAnyScreen(owner));
        if (repaint)
        {
            if (owner != null && FeatureInAnyScreen(owner)) _screenIndex = ResolveScreenIndexFor(owner.Id);
            if (MoveToNearestUsableScreen())
            {
                if (_expanded ? ExpandCurrentScreen() : ShowCurrentScreenCompact()) return;
            }
            ShowInactiveOrHidden();
            return;
        }
        // Another view in front (a notice, rest): it is only measured again and the box
        // repositioned, which is all the screens setting can change for it.
        SyncMeasuredHeight();
        PositionTopCenter();
    });

    /// <summary>
    /// Repaints the content of every member of the current screen: it is the content of the
    /// EXPANDED view of a combined screen (its columns).
    /// </summary>
    private void RefreshScreenMembers()
    {
        foreach (var member in CurrentScreenFeatures()) RefreshMemberContent(member.Id);
    }

    /// <summary>
    /// Periodic refresh of a combined screen that is in front: in the expanded view, the
    /// timer countdown, the calendar counts and the music seek (each column ages at its
    /// own pace). The compact does not go through here: it shows a single feature and is
    /// refreshed through that feature's route.
    /// </summary>
    private void RefreshCombinedScreenTick()
    {
        if (_disposed || !IsBoxShown || _contentMode != IslandContentMode.Screen) return;
        if (!_expanded) return;
        // Each column ages at its own pace: its card declares it.
        foreach (var member in CurrentScreenFeatures()) FeatureCard(member.Id)?.Tick?.Invoke();
    }

    /// <summary>Refreshes the data of a feature inside a combined screen.</summary>
    private void RefreshMemberContent(string id) => FeatureCard(id)?.Refresh();

    /// <summary>
    /// Columns the expanded view of the current screen will occupy: one per feature with
    /// panels of its own. The ones that only live in the compact (Bluetooth, charger) do
    /// not count: their width is not reserved.
    /// </summary>
    private int CurrentScreenColumnCount() => CurrentScreenFeatures().Count(m => ColumnFor(m.Id) != null);

    // ------------------------------------------------------------------
    // Layer visibility of a combined screen
    // ------------------------------------------------------------------

    /// <summary>
    /// Leaves on screen ONLY what composes the EXPANDED view of the current screen: the
    /// expanded panels of each member, in columns from left to right. Everything else is
    /// turned off (a combined screen cannot show a layer of a feature that does not
    /// compose it). The compact does not go through here: it shows a single feature with
    /// its rich view.
    /// </summary>
    private void ApplyScreenLayerVisibility()
    {
        var members = CurrentScreenFeatures();
        // Simple screen (or with no usable members): the panels go back to their home.
        if (members.Count <= 1) RestoreExpandedHomes();
        HideAllContentLayers();
        // The expanded view goes from LEFT TO RIGHT: the panels of each member are moved
        // to its column, in the order of the screen (RF-2/RF-3).
        ComposeScreenExpandedRow(members);
        foreach (var member in members) ShowMemberPanels(member.Id);
        UpdateArrows();
    }

    /// <summary>
    /// Shows the expanded panels that belong to a feature, with the same rules as its
    /// simple screen: the timer alert takes precedence over its reels and the music seek
    /// adapts to the session capabilities. Its card declares it
    /// (IslandWindow.FeatureCards.cs).
    /// </summary>
    private void ShowMemberPanels(string id) => FeatureCard(id)?.ShowExpanded();

    /// <summary>
    /// Is the view of a screen held by any of its members? (RF-4). It is the sustaining
    /// rule of a combined screen: as long as a single one of its features is active, the
    /// screen stays.
    /// </summary>
    private bool ScreenSustainsView() => CurrentScreenFeatures().Any(SingleFeatureSustainsView);

    // ------------------------------------------------------------------
    // Composition of the expanded view (left to right)
    // ------------------------------------------------------------------

    /// <summary>
    /// Expanded panels that belong to each feature: they are the ones that MOVE to its
    /// column when the screen is combined (each one lives in a single container at a time,
    /// so it is moved, not copied). Its card declares them.
    /// </summary>
    private IEnumerable<UIElement> MemberPanels(string id) => FeatureCard(id)?.Expanded ?? [];

    /// <summary>Column of the expanded view that hosts the panels of a feature (null if it has none).</summary>
    private StackPanel? ColumnFor(string id) => FeatureCard(id)?.Column();

    /// <summary>
    /// Where each panel lived before entering a column: putting it back is what allows
    /// returning to the simple views without duplicating panels or reordering the
    /// expanded view by hand.
    /// </summary>
    private readonly Dictionary<UIElement, (Panel Parent, int Index)> _expandedHomes = [];

    /// <summary>
    /// Composes the expanded view of a combined screen: one column per feature, from LEFT
    /// TO RIGHT in the order of the screen, with the panels of each one inside. The
    /// columns are PLACED in that order (the XAML order does not rule: a «timer+media»
    /// screen shows the timer on the left) and the ones that do not compose the screen are
    /// parked at the end, hidden. The width of each column is shared out so that they all
    /// fit whole inside the box (the total width is dynamic: the number of columns sets
    /// it).
    /// </summary>
    private void ComposeScreenExpandedRow(List<IIslandFeature> members)
    {
        RestoreExpandedHomes();
        var columns = new List<(string Id, StackPanel Host)>();
        foreach (var member in members)
        {
            if (ColumnFor(member.Id) is { } host) columns.Add((member.Id, host));
        }
        // The used columns go first, in the order of the screen; behind them stay (hidden)
        // the rest, so that no XAML reference is lost.
        ScreenExpandedRow.Children.Clear();
        foreach (var (_, host) in columns) ScreenExpandedRow.Children.Add(host);
        foreach (var column in AllScreenColumns())
        {
            if (!columns.Any(c => ReferenceEquals(c.Host, column))) ScreenExpandedRow.Children.Add(column);
        }
        double columnWidth = ScreenColumnWidthForCount(columns.Count);
        foreach (var column in AllScreenColumns())
        {
            bool used = columns.Any(c => ReferenceEquals(c.Host, column));
            column.Visibility = used ? Visibility.Visible : Visibility.Collapsed;
            column.Width = used ? columnWidth : double.NaN;
        }
        ScreenExpandedRow.Visibility = columns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, host) in columns)
        {
            foreach (var panel in MemberPanels(id)) MoveToColumn(panel, host);
        }
    }

    /// <summary>
    /// Width of each column of the expanded view of a combined screen: the available width
    /// (the box one, already clamped to the monitor) is shared out among the columns that
    /// will be seen, so that the last one is not cut off by the edge.
    /// </summary>
    private double ScreenColumnWidthForCount(int columns)
    {
        if (columns <= 0) return ScreenColumnWidth;
        double available = ScreenExpandedWidthForMembers(columns) - ScreenRowPadding
            - Math.Max(0, columns - 1) * ScreenColumnGap;
        return Math.Clamp(available / columns, 150, ScreenColumnWidth);
    }

    private IEnumerable<StackPanel> AllScreenColumns() =>
        [ScreenColumnMedia, ScreenColumnTimer, ScreenColumnApps, ScreenColumnShelf, ScreenColumnCalendar, ScreenColumnClipboard, ScreenColumnWeather];

    /// <summary>Moves a panel to its column remembering its original place (only once).</summary>
    private void MoveToColumn(UIElement panel, Panel host)
    {
        if (VisualTreeHelper.GetParent(panel) is not Panel parent) return;
        if (ReferenceEquals(parent, host)) return;
        if (!_expandedHomes.ContainsKey(panel)) _expandedHomes[panel] = (parent, parent.Children.IndexOf(panel));
        parent.Children.Remove(panel);
        host.Children.Add(panel);
    }

    /// <summary>
    /// Puts every panel back in its original place. It is called when composing (a clean
    /// starting point) and when leaving a combined screen; with no moved panels it does
    /// nothing.
    /// </summary>
    private void RestoreExpandedHomes()
    {
        if (_expandedHomes.Count == 0)
        {
            ScreenExpandedRow.Visibility = Visibility.Collapsed;
            RestoreScreenColumnOrder();
            return;
        }
        foreach (var (panel, home) in _expandedHomes.OrderBy(kv => kv.Value.Index))
        {
            if (VisualTreeHelper.GetParent(panel) is Panel parent && !ReferenceEquals(parent, home.Parent))
                parent.Children.Remove(panel);
            home.Parent.Children.Insert(Math.Min(home.Index, home.Parent.Children.Count), panel);
        }
        _expandedHomes.Clear();
        ScreenExpandedRow.Visibility = Visibility.Collapsed;
        RestoreScreenColumnOrder();
        foreach (var column in AllScreenColumns())
        {
            column.Children.Clear();
            column.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Puts the columns of the expanded view back in their XAML order: composing a screen
    /// reorders them (left to right following the screen) and outside of it they are parked
    /// in their place.
    /// </summary>
    private void RestoreScreenColumnOrder()
    {
        var canonical = AllScreenColumns().ToList();
        if (ScreenExpandedRow.Children.Count == canonical.Count
            && canonical.All(column => ScreenExpandedRow.Children.Contains(column)))
        {
            ScreenExpandedRow.Children.Clear();
            foreach (var column in canonical) ScreenExpandedRow.Children.Add(column);
        }
    }

    /// <summary>
    /// Width of the expanded view of a combined screen: dynamic, one column per feature
    /// visible in the expanded view, without going past the monitor.
    /// </summary>
    private double ScreenExpandedWidthForMembers(int columns)
    {
        // Each column carries its gap on the right (the XAML margin), so the needed width
        // is one column + its gap per column, plus the side padding of the content. Without
        // that last margin the last column would be cut off by the edge of the box.
        double total = columns * (ScreenColumnWidth + ScreenColumnGap) + ScreenRowPadding;
        var primary = PrimaryMonitor();
        double monitorWidth = primary.dpiX > 0 ? primary.workArea.Width * 96.0 / primary.dpiX : ScreenExpandedMaxWidth;
        return Math.Clamp(total, 280, Math.Min(ScreenExpandedMaxWidth, Math.Max(280, monitorWidth - 24)));
    }
}
