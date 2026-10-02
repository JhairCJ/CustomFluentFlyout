// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Voice dictation on the Island (spec 006): the card shown while the key is held
/// - microphone on the left, waves from the real microphone on the right - and the
/// three transitions that accompany it (recording, transcribing, withdrawing).
///
/// <para>Rules it holds:</para>
/// <list type="bullet">
/// <item><b>While dictating, the view belongs to it</b>: the feature holds the view and
/// declares exclusive access, so no song change or notice takes the card away
/// mid-phrase and the temporary notice deadline does not hide it.</item>
/// <item><b>The waves are the microphone, not the system</b>: the level comes from
/// <see cref="DictationService"/> itself (RMS of the last captured block) and is drawn
/// as small bars; they flatten when you go quiet and jump when you speak (RF-2).</item>
/// <item><b>When it ends it withdraws by itself</b>: once the text is written, it is
/// cancelled, or it fails, the Island goes back to whatever applied - the current
/// active item or rest - without leaving the card stuck (RF-9).</item>
/// </list>
/// </summary>
public partial class IslandWindow
{
    /// <summary>Number of visualizer bars: width / step (3 + 2).</summary>
    private const int DictationBarCount = 12;
    /// <summary>Minimum and maximum bar height, within the zone's 22 px.</summary>
    private const double DictationBarMin = 4;
    private const double DictationBarMax = 20;
    /// <summary>Visualizer cadence: 20 fps is enough for a smooth wave and costs little.</summary>
    private static readonly TimeSpan DictationBarInterval = TimeSpan.FromMilliseconds(50);

    private static readonly Brush DictationWhiteBrush = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private Brush _dictationIndicatorBrush = DictationWhiteBrush;

    private readonly Border[] _dictationBars = new Border[DictationBarCount];
    private readonly double[] _dictationLevels = new double[DictationBarCount];
    private DispatcherTimer? _dictationBarsTimer;
    private DictationService? _dictation;
    /// <summary>The dictation card is the view in front right now.</summary>
    private bool _dictationViewShown;

    /// <summary>Registered "dictation" feature (never null after startup).</summary>
    private IIslandFeature? DictationFeature => FeatureById(IslandFeatureIds.Dictation);

    /// <summary>Is the feature turned on with the container?</summary>
    private bool DictationModeAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.DictationEnabled;

    /// <summary>Is a dictation session in progress? It is what holds the view and grants exclusive access.</summary>
    private bool DictationActive() => _dictation?.Active == true;

    /// <summary>
    /// Does another feature have exclusive access right now (the timer alert)? It is
    /// asked WITHOUT counting dictation: the dictation card is exclusive while the
    /// session lasts, so a plain <c>HasExclusive()</c> would veto itself.
    /// </summary>
    private bool AnotherFeatureExclusive() =>
        _features.Features.Any(f => f.State.Exclusive && f.Id != IslandFeatureIds.Dictation);

    /// <summary>
    /// While dictating, the Island is out of service for the pointer (RF-10): the box's
    /// hit-test is turned off - and with it ALL of its clicks, its wheel and its
    /// drag-and-drop - and so is the detection strip's. A brush, or a click that was
    /// meant for the window behind, cannot open a screen over the card or change it
    /// mid-phrase: dictation is closed with its hotkey, not with the mouse. An already
    /// lit hover is withdrawn right here (its exit never arrives: without hit-testing
    /// there is no MouseLeave) so the micro-growth does not stay frozen under the
    /// card.
    /// </summary>
    private void ApplyDictationInteractionLock()
    {
        bool interactive = !DictationActive();
        if (!interactive && _inactiveHot)
        {
            _inactiveHot = false;
            if (AnimationsEnabled) EnsureLoop();
            else { _inactiveHotT = 0; ApplyFrame(); }
        }
        if (IslandBox.IsHitTestVisible != interactive) IslandBox.IsHitTestVisible = interactive;
        // The detection strip is governed by the edge DOOR (ApplyAccessZone), which
        // already vetoes dictation on its own: here it is re-evaluated, not overridden.
        ApplyAccessZone();
    }

    internal IslandFeatureState GetDictationFeatureState()
    {
        bool enabled = DictationModeAvailable();
        bool active = DictationActive();
        return new IslandFeatureState(enabled, enabled, active,
            Selected: _selectedFeature?.Id == IslandFeatureIds.Dictation,
            // With a session in progress the card is exclusive: not a song change, not a
            // notice, not the pointer can replace it until dictation ends.
            Exclusive: active);
    }

    internal bool ShowDictationCompactFromContract()
    {
        if (!DictationModeAvailable() || _dictation == null) return false;
        ShowDictationCompact();
        return true;
    }

    /// <summary>
    /// Binds the dictation service - it lives in MainWindow, which owns the keyboard
    /// hook - and creates the visualizer bars, which cannot come from the XAML because
    /// their count and height are driven by the captured level.
    /// </summary>
    private void InitDictation()
    {
        BuildDictationBars();
        _dictation = _main.Dictation;
        _dictation.Changed += OnDictationChanged;
    }

    private void ShutdownDictation()
    {
        if (_dictation != null) _dictation.Changed -= OnDictationChanged;
        _dictation = null;
        StopDictationBars();
        // Without the service there is no dictation: the Island responds to the pointer again.
        ApplyDictationInteractionLock();
    }

    /// <summary>Hot setting change: dictation was turned off with its card in front.</summary>
    public void RefreshDictationContent() => Dispatcher.Invoke(() =>
    {
        ApplyDictationInteractionLock();
        bool returnedFromDictation = false;
        if (!DictationModeAvailable() && _dictationViewShown)
        {
            _dictationViewShown = false;
            StopDictationBars();
            FallbackFromDictationView();
            returnedFromDictation = true;
        }
        if (!returnedFromDictation) ApplyContentVisibility();
        SyncMeasuredHeight();
        UpdateArrows();
    });

    // ------------------------------------------------------------------
    // Session lifecycle
    // ------------------------------------------------------------------

    /// <summary>The service notifies from its own thread (the transcription one): over to the UI one.</summary>
    private void OnDictationChanged()
    {
        if (_disposed) return;
        Dispatcher.BeginInvoke(new Action(SyncDictationView));
    }

    /// <summary>
    /// Single point for the card: a running session presents it, a finished one
    /// withdraws it. The error is shown - with its reason - until the service forgets
    /// it.
    /// </summary>
    private void SyncDictationView()
    {
        var dictation = _dictation;
        if (_disposed || dictation == null) return;
        // The pointer lock follows the phase: it comes in with the session and leaves
        // with it (also with the error notice, which is a card that CAN be clicked).
        ApplyDictationInteractionLock();

        if (dictation.Active)
        {
            ShowDictationCompact();
            return;
        }
        // The failure is shown even if the card was not on screen (model missing, no
        // microphone): the reason is exactly what the user needs to read, and without
        // this branch dictation stayed mute (RF-6).
        if (dictation.Phase == DictationPhase.Error)
        {
            ShowDictationCompact();
            return;
        }
        if (!_dictationViewShown) return;

        // Idle: the card is withdrawn and the Island goes back to its own business (RF-9).
        _dictationViewShown = false;
        StopDictationBars();
        FallbackFromDictationView();
    }

    /// <summary>
    /// Presents the card. NOTE: it has no expanded view and is not navigated to, so it
    /// lives outside the screens (like Bluetooth's and the charger's). With the
    /// session in progress the feature is exclusive and the notice policy does not arm
    /// a deadline: the card lasts as long as the dictation, not as long as a notice.
    /// </summary>
    private void ShowDictationCompact()
    {
        if (!DictationModeAvailable() || _dictation == null) return;
        // A FOREIGN exclusive (the timer alert) wins and is modal until it closes: the
        // card waits instead of taking the surface away - the alert would have no way
        // left to withdraw. Dictation is not cut: it keeps capturing and writing,
        // which is what matters; it just does not paint its card. Same veto as
        // Bluetooth and the charger, which also do not present over an exclusive.
        if (AnotherFeatureExclusive()) return;
        _dictationViewShown = true;
        // The waves only run with the mic open: a failure notice animates nothing.
        if (DictationActive()) StartDictationBars(); else StopDictationBars();
        RefreshDictationUI();
        ShowCompactView(IslandContentMode.Dictation, DictationFeature, RefreshDictationUI,
            forceNotice: true, restartNotice: true);
    }

    /// <summary>
    /// The dictation card stopped being the view in front: it folds back to the current
    /// active item or to rest, without leaving the notice's surface (same route as the
    /// charger).
    /// </summary>
    private void FallbackFromDictationView()
    {
        if (_noticeForced) ClearTemporaryNotice();
        if (RecoverScreensAfterMemberLost()) return;
        _expanded = false;
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } vigente && ShowScreenOfFeature(vigente))
            return;
        HidePerMode();
    }

    // ------------------------------------------------------------------
    // Painting
    // ------------------------------------------------------------------

    /// <summary>
    /// Paints the state: microphone and waves in white - or with the current artwork's
    /// accent -, no text while recording (the center is for what has to be READ), and
    /// the message while transcribing or failing.
    /// </summary>
    private void RefreshDictationUI()
    {
        var dictation = _dictation;
        if (dictation == null) return;

        RefreshDictationIndicatorBrush();
        DictationGlyph.Foreground = _dictationIndicatorBrush;
        DictationGlyph.Symbol = dictation.Phase == DictationPhase.Error
            ? Wpf.Ui.Controls.SymbolRegular.MicOff24
            : Wpf.Ui.Controls.SymbolRegular.Mic24;

        DictationStatus.Text = dictation.Phase switch
        {
            DictationPhase.Transcribing => IslandStrings.Get("IslandDictationTranscribing", "Transcribing…"),
            DictationPhase.Error => IslandStrings.Get(dictation.MessageKey ?? "", dictation.MessageFallback ?? ""),
            _ => "",
        };
        DictationCompactGrid.ToolTip = DictationTooltip(dictation);
    }

    /// <summary>Label on hover: which hotkey to hold and which model is used.</summary>
    private string DictationTooltip(DictationService dictation)
    {
        string hotkey = DictationHotkey.IsValid(SettingsManager.Current.DictationHotkey)
            ? SettingsManager.Current.DictationHotkey
            : DictationHotkey.Default;
        return dictation.Phase switch
        {
            DictationPhase.Listening => IslandStrings.Format("IslandDictationTooltipListening",
                "Listening… hold {0} to dictate", hotkey),
            DictationPhase.Transcribing => IslandStrings.Get("IslandDictationTranscribing", "Transcribing…"),
            DictationPhase.Error => IslandStrings.Get(dictation.MessageKey ?? "", dictation.MessageFallback ?? ""),
            _ => IslandStrings.Format("IslandTooltipDictation", "Dictate by holding {0}", hotkey),
        };
    }

    private void BuildDictationBars()
    {
        DictationWave.Children.Clear();
        RefreshDictationIndicatorBrush();
        for (int i = 0; i < _dictationBars.Length; i++)
        {
            var bar = new Border
            {
                Width = 3,
                Height = DictationBarMin,
                CornerRadius = new CornerRadius(1.5),
                Background = _dictationIndicatorBrush,
                Margin = new Thickness(0, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _dictationBars[i] = bar;
            DictationWave.Children.Add(bar);
        }
    }

    /// <summary>
    /// Shares the microphone and wave color: the system accent when the artwork setting
    /// is off; with the setting on it uses the displayed artwork's accent, and white
    /// while there is no valid artwork yet.
    /// </summary>
    private void RefreshDictationIndicatorBrush()
    {
        bool useAlbumAccent = SettingsManager.Current.UseAlbumArtAsAccentColor;
        Brush next = !useAlbumAccent
            || (_displayedAlbumArt != null && AlbumAccent.HasAlbumColor)
                ? AlbumAccent.Brush
                : DictationWhiteBrush;
        if (ReferenceEquals(next, _dictationIndicatorBrush)) return;

        _dictationIndicatorBrush = next;
        DictationGlyph.Foreground = next;
        foreach (var bar in _dictationBars)
        {
            if (bar != null) bar.Background = next;
        }
    }

    private void StartDictationBars()
    {
        if (_dictationBarsTimer == null)
        {
            _dictationBarsTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = DictationBarInterval };
            _dictationBarsTimer.Tick += OnDictationBarsTick;
        }
        if (!_dictationBarsTimer.IsEnabled) _dictationBarsTimer.Start();
    }

    private void StopDictationBars()
    {
        _dictationBarsTimer?.Stop();
        Array.Clear(_dictationLevels);
        foreach (var bar in _dictationBars)
        {
            if (bar != null) bar.Height = DictationBarMin;
        }
    }

    /// <summary>
    /// The wave enters from the right (new data is always the edge): the bars shift one
    /// position and the last one takes the current level, with a smooth fall so that
    /// silence does not cut dead (RF-2).
    /// </summary>
    private void OnDictationBarsTick(object? sender, EventArgs e)
    {
        var dictation = _dictation;
        if (dictation == null || !dictation.Active)
        {
            StopDictationBars();
            return;
        }
        RefreshDictationIndicatorBrush();

        for (int i = 0; i < _dictationLevels.Length - 1; i++)
        {
            _dictationLevels[i] = _dictationLevels[i + 1];
        }
        // The raw level jumps a lot between blocks; the high value is kept so the wave can breathe.
        double incoming = Math.Clamp(dictation.Level, 0, 1);
        _dictationLevels[^1] = Math.Max(incoming, _dictationLevels[^1] * 0.6);

        for (int i = 0; i < _dictationBars.Length; i++)
        {
            double value = _dictationLevels[i];
            double height = DictationBarMin + Math.Pow(value, 0.7) * (DictationBarMax - DictationBarMin);
            _dictationBars[i].Height = height;
        }
    }
}
