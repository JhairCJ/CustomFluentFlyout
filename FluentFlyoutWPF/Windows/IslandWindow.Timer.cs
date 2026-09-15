// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Temporizador del Fluent Island (spec 002): segundo contenido del contenedor
/// junto a música. Compacto con icono + restante + progreso opcional; expandido
/// con tiempo libre + controles a la izquierda y presets a la derecha; aviso a
/// cero con despliegue forzado; navegación entre modos con rueda y flechas.
/// </summary>
public partial class IslandWindow
{
    private readonly IslandTimer _timer = new();
    private int _timerMode; // 0 = música, 1 = temporizador
    private bool _pendingTimerAlert;
    private TimeSpan _staged = TimeSpan.Zero; // valor de los reels, origen personalizado
    private bool _timerInputCustom = true;
    // Anti-reaparición tras descartar: el ratón sigue encima y el poll re-expandiría.
    private const int TimerReshowSnoozeSeconds = 2;
    private DateTime _timerSnoozeUntil = DateTime.MinValue;
    // Arrastre de reel: píxeles por unidad y estado del gesto.
    private const double ReelPixelsPerUnit = 24;
    private bool _reelDragging;
    private string _reelDragUnit = "";
    private double _reelDragStartY;
    private int _reelDragStartVal;

    private bool TimerModeAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandTimerEnabled;

    private bool TimerKeepsAlive() =>
        TimerModeAvailable() && (_timer.IsCounting || _timer.State == IslandTimerState.Alerting || _pendingTimerAlert);

    private void InitTimer()
    {
        _timer.Finished += OnTimerFinished;
        TimerPresetList.ItemsSource = SettingsManager.Current.IslandTimerPresets;
    }

    /// <summary>
    /// Ajuste de habilitación: al apagar con cuenta activa se cancela (RF-10).
    /// </summary>
    public void RefreshTimerEnabled()
    {
        if (!TimerModeAvailable())
        {
            _timer.Cancel();
            _pendingTimerAlert = false;
            if (_timerMode == 1)
            {
                _timerMode = 0;
                if (_expanded)
                {
                    // Solo vuelve la música si el contenido musical está
                    // habilitado y el snapshot la tiene disponible.
                    var session = MusicContentShown() ? Current() : null;
                    if (session != null) RefreshUi(session);
                    else { ClearMusicResidue(); HidePerMode(); }
                }
                else HidePerMode();
            }
        }
        RefreshTimerUI();
        ApplyTimerContentVisibility();
    }

    private void RefreshTimerUI()
    {
        UpdateArrows();
        if (!TimerModeAvailable()) return;
        TimerRemaining.Text = IslandTimer.FormatHms(_timer.Remaining);
        TimerProgressZone.Visibility = SettingsManager.Current.IslandTimerShowProgress
            ? Visibility.Visible : Visibility.Collapsed;
        double track = TimerProgressTrack.ActualWidth;
        TimerProgressFill.Width = track > 0 ? track * _timer.ElapsedFraction : 0;
        bool idle = _timer.State == IslandTimerState.Idle;
        if (idle) PaintReels();
        TimerRunOrigin.Text = _timer.OriginLabel;
        TimerRunRemaining.Text = IslandTimer.FormatHms(_timer.Remaining);
        TimerRunPauseGlyph.Symbol = _timer.State == IslandTimerState.Paused
            ? Wpf.Ui.Controls.SymbolRegular.Play24
            : Wpf.Ui.Controls.SymbolRegular.Pause24;
        TimerStartBtn.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        TimerRestartBtn.Visibility = idle && _timer.Configured > TimeSpan.Zero
            ? Visibility.Visible : Visibility.Collapsed;
        TimerAlertName.Text = _timer.OriginLabel;
    }

    private void PaintReels()
    {
        long total = (long)_staged.TotalSeconds;
        ReelH.Text = $"{total / 3600:00}";
        ReelM.Text = $"{(total % 3600) / 60:00}";
        ReelS.Text = $"{total % 60:00}";
    }

    private void ApplyTimerContentVisibility()
    {
        bool timer = _timerMode == 1 && TimerModeAvailable();
        MusicCompactGrid.Visibility = timer ? Visibility.Collapsed : Visibility.Visible;
        TimerCompactGrid.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        bool alert = timer && _timer.State == IslandTimerState.Alerting;
        bool idle = timer && _timer.State == IslandTimerState.Idle;
        MusicExpandedTop.Visibility = timer ? Visibility.Collapsed : Visibility.Visible;
        ControlsRow.Visibility = timer ? Visibility.Collapsed : Visibility.Visible;
        if (timer)
        {
            SeekRow.Visibility = Visibility.Collapsed;
        }
        else if (MusicContentShown() && Current() is { } session)
        {
            ApplyCapabilities(session); // restaura SeekRow según la fuente
        }
        else
        {
            // Sin sesión que presentar: la fila de seek no se deja visible
            // (no hay vista musical vacía, 001 MOD RF-9).
            SeekRow.Visibility = Visibility.Collapsed;
        }
        // Los paneles config/progreso los conmuta el fundido; la alerta es instantánea.
        TimerAlert.Visibility = alert ? Visibility.Visible : Visibility.Collapsed;
        CrossfadeTimerPanels(showConfig: timer && idle && !alert, showRun: timer && !idle && !alert);
        UpdateArrows();
    }

    // Solo fundido de entrada: el saliente colapsa instantáneo para no medir
    // dos paneles apilados (eso inflaba la altura).
    private void CrossfadeTimerPanels(bool showConfig, bool showRun)
    {
        FadeInPanel(TimerExpanded, showConfig);
        FadeInPanel(TimerRunPanel, showRun);
    }

    // Fundido SOLO en la transición oculto->visible: la opacidad LOCAL del
    // panel es siempre 1 (el valor final), y la animación solo la conduce
    // durante los 150 ms del fundido. Así, repetir ApplyTimerContentVisibility
    // (actualizaciones del contenedor, settings, start/pause) nunca puede
    // revertir el panel a opacidad 0 y dejar la caja negra (001/002 ADDED RF-1).
    private static void FadeInPanel(UIElement el, bool show)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 1; // valor local = estado final; la animación solo cubre el gesto
        if (!show)
        {
            el.Visibility = Visibility.Collapsed;
            return;
        }
        if (el.Visibility == Visibility.Visible) return;
        el.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        fadeIn.Completed += (_, _) => el.BeginAnimation(UIElement.OpacityProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    // Todo cambio de estado del motor re-conmuta paneles + re-mide la altura.
    // El loop lo garantiza SyncMeasuredHeight si el objetivo cambió.
    private void RefreshTimerModeView()
    {
        ApplyTimerContentVisibility();
        RefreshTimerUI();
        SyncMeasuredHeight();
    }

    /// <summary>
    /// Red de seguridad del 001/002 ADDED RF-1: mientras el temporizador es el
    /// contenido activo y visible, su panel (compacto o expandido) tiene que
    /// estar presente. Si una actualización del contenedor dejara la caja sin
    /// ningún panel de timer (superficie negra), se re-aplica el contenido y
    /// se registra el incidente para diagnosticarlo.
    /// </summary>
    private void EnsureTimerContentShown()
    {
        if (_disposed || _timerMode != 1 || !IsBoxShown || !TimerModeAvailable()) return;
        if (_timer.State == IslandTimerState.Alerting
            && TimerAlert.Visibility == Visibility.Visible) return;
        bool compactOk = TimerCompactGrid.Visibility == Visibility.Visible;
        bool expandedOk = TimerExpanded.Visibility == Visibility.Visible
            || TimerRunPanel.Visibility == Visibility.Visible
            || TimerAlert.Visibility == Visibility.Visible;
        if (_expanded ? expandedOk : compactOk) return;
        Logger.Warn("Island: contenido del temporizador ausente con caja visible " +
            "(expanded={Expanded}, timerState={State}, compact={Compact}, " +
            "config={Config}, run={Run}, alert={Alert}); re-aplicando contenido",
            _expanded, _timer.State,
            TimerCompactGrid.Visibility, TimerExpanded.Visibility,
            TimerRunPanel.Visibility, TimerAlert.Visibility);
        ApplyTimerContentVisibility();
        RefreshTimerUI();
        SyncMeasuredHeight();
    }

    private void UpdateArrows()
    {
        // Flechas opcionales: solo con dos contenidos disponibles (002 MOD RF-9).
        bool show = _expanded && TimerModeAvailable() && MediaContentAvailable()
            && SettingsManager.Current.IslandTimerShowArrows && IsBoxShown;
        ModePrevBtn.Visibility = ModeNextBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTimerCompact()
    {
        _hideCts?.Cancel();
        _hidingViaCompact = false;
        if (!TimerModeAvailable() || Suppressed()) { SnapHidden(); return; }
        _timerMode = 1;
        SelectFeature("timer");
        _inactiveShown = false;
        RefreshTimerUI();
        ApplyTimerContentVisibility();
        _expanded = false;
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled) SnapCompact();
        else
        {
            _pT = 0;
            _qT = 1;
            IslandBox.Visibility = Visibility.Visible;
            UpdateMediaStatusDot();
            UpdateRotationPauseState();
            EnsureLoop();
        }
        // Sin aviso temporal: la cuenta visible sigue el modo del contenedor
        // quedando a la vista para controlarla de un vistazo (H2 del spec).
    }

    private void SnapExpandedTimer()
    {
        _inactiveShown = false;
        _p = _pT = 1; _pv = 0;
        _q = _qT = 1; _qv = 0;
        _hexpShown = _hexp;
        _pop = 0; _popPlaying = false;
        ApplyFrame();
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
    }

    private void ExpandTimer()
    {
        if (!TimerModeAvailable()) return;
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        _hideCts?.Cancel();
        _hidingViaCompact = false;
        bool wasExpanded = _expanded;
        _timerMode = 1;
        SelectFeature("timer");
        _inactiveShown = false;
        RefreshTimerUI();
        ApplyTimerContentVisibility();
        _expanded = true;
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled)
        {
            SnapExpandedTimer();
            return;
        }
        if (wasExpanded) return; // ya expandido: solo cambia el contenido
        _pT = 1; _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
        EnsureLoop();
    }

    private void OnTimerFinished() => Dispatcher.Invoke(ShowTimerAlert);

    /// <summary>
    /// Aviso a cero: fuerza el despliegue aunque estuviera oculto; con acceso
    /// exclusivo persistente atraviesa supresión (001 RF-8/14, 002 RF-2).
    /// </summary>
    private void ShowTimerAlert()
    {
        if (!TimerModeAvailable()) return;
        if (!SettingsManager.Current.IslandEnabled) { _pendingTimerAlert = true; return; }
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        _hideCts?.Cancel();
        _hidingViaCompact = false;
        _timerMode = 1;
        SelectFeature("timer");
        _inactiveShown = false;
        RefreshTimerUI();
        ApplyTimerContentVisibility();
        _expanded = true;
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled)
        {
            SnapExpandedTimer();
            return;
        }
        _pT = 1; _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
        EnsureLoop();
    }

    private void CycleMode()
    {
        // Alerta modal: hasta X o reinicio no se sale al resto de modos.
        if (_timer.State == IslandTimerState.Alerting) return;
        if (!TimerModeAvailable()) return;
        if (_timerMode == 0)
        {
            if (_expanded) ExpandTimer();
            else ShowTimerCompact();
        }
        else
        {
            // Rueda abajo cambia solo al siguiente contenido DISPONIBLE
            // (002 MOD RF-9): sin snapshot musical no hay música a la que saltar.
            var session = Current();
            if (session == null) return;
            if (_expanded) ExpandSession(session);
            else ShowMusicCompact(session);
        }
        // Nunca se toca el motor de cuenta: cambiar de vista no cancela ni
        // reinicia la cuenta del temporizador (002 MOD RF-9, RF-13).
    }

    // --- controles del expandido ---

    private void TimerCompact_Click(object sender, MouseButtonEventArgs e) => ExpandTimer();

    private void ModeArrow_Click(object sender, RoutedEventArgs e) => CycleMode();

    private static int WrapUnit(int value, int count) => ((value % count) + count) % count;

    private void SplitStaged(out int h, out int m, out int s)
    {
        long total = (long)_staged.TotalSeconds;
        h = (int)(total / 3600);
        m = (int)((total % 3600) / 60);
        s = (int)(total % 60);
    }

    private void ReelStep(string unit, int dir)
    {
        SplitStaged(out int h, out int m, out int s);
        switch (unit)
        {
            case "H": h = WrapUnit(h + dir, 25); break;
            case "M": m = WrapUnit(m + dir, 60); break;
            default: s = WrapUnit(s + dir, 60); break;
        }
        _staged = new TimeSpan(h, m, s);
        _timerInputCustom = true;
        TimerPresetList.SelectedItem = null;
        TimerStatus.Text = "";
        RefreshTimerUI();
    }

    private void Reel_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string unit) return;
        _reelDragging = true;
        _reelDragUnit = unit;
        _reelDragStartY = e.GetPosition(this).Y;
        SplitStaged(out int h, out int m, out int s);
        _reelDragStartVal = unit switch { "H" => h, "M" => m, _ => s };
        el.CaptureMouse();
        e.Handled = true;
    }

    private void Reel_Move(object sender, MouseEventArgs e)
    {
        if (!_reelDragging || sender is not FrameworkElement el || !el.IsMouseCaptured) return;
        // Arriba aumenta, abajo reduce; el valor envuelve (reel infinito).
        int steps = (int)((_reelDragStartY - e.GetPosition(this).Y) / ReelPixelsPerUnit);
        SplitStaged(out int h, out int m, out int s);
        int current = _reelDragUnit switch { "H" => h, "M" => m, _ => s };
        int count = _reelDragUnit == "H" ? 25 : 60;
        int next = WrapUnit(_reelDragStartVal + steps, count);
        if (next == current) return;
        switch (_reelDragUnit)
        {
            case "H": h = next; break;
            case "M": m = next; break;
            default: s = next; break;
        }
        _staged = new TimeSpan(h, m, s);
        _timerInputCustom = true;
        TimerPresetList.SelectedItem = null;
        TimerStatus.Text = "";
        PaintReels();
    }

    private void Reel_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.IsMouseCaptured) el.ReleaseMouseCapture();
        _reelDragging = false;
        RefreshTimerUI();
    }

    // Rueda sobre un dígito: arriba reduce, abajo aumenta (criterio del dueño).
    private void Reel_Wheel(object sender, MouseWheelEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.Tag is not string unit) return;
        ReelStep(unit, e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void TimerPresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimerPresetList.SelectedItem is TimerPreset preset)
        {
            _timerInputCustom = false;
            _staged = TimeSpan.FromSeconds(preset.DurationSeconds);
            TimerStatus.Text = "";
            RefreshTimerUI();
        }
    }

    private void TimerStart_Click(object sender, RoutedEventArgs e)
    {
        TimeSpan duration;
        string origin;
        if (!_timerInputCustom && TimerPresetList.SelectedItem is TimerPreset preset)
        {
            duration = TimeSpan.FromSeconds(preset.DurationSeconds);
            origin = preset.Name;
        }
        else
        {
            duration = _staged;
            origin = "Timer";
        }
        if (!_timer.Start(duration, origin))
            TimerStatus.Text = "Duración no válida: usa 00:00:01 a 24:00:00.";
        else
        {
            NoteFeatureEvent("timer");
            TimerStatus.Text = "";
        }
        RefreshTimerModeView();
    }

    private void TimerPause_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.State == IslandTimerState.Running) _timer.Pause();
        else if (_timer.State == IslandTimerState.Paused) { _timer.Resume(); NoteFeatureEvent("timer"); }
        RefreshTimerModeView();
    }

    private void TimerRestart_Click(object sender, RoutedEventArgs e)
    {
        _timer.Restart();
        _staged = _timer.Configured;
        TimerStatus.Text = "";
        RefreshTimerModeView();
    }

    private void TimerCancel_Click(object sender, RoutedEventArgs e)
    {
        _timer.Cancel();
        _staged = TimeSpan.Zero;
        TimerStatus.Text = "";
        RefreshTimerModeView();
    }

    private void TimerAlertRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.Configured > TimeSpan.Zero)
            _timer.Start(_timer.Configured, _timer.OriginLabel);
        RefreshTimerModeView();
    }

    private void TimerAlertDismiss_Click(object sender, RoutedEventArgs e)
    {
        _timer.Cancel();
        _staged = TimeSpan.Zero;
        _timerMode = 0;
        SelectFeature("media");
        // El ratón sigue encima: sin esto HidePerMode retorna y el 00:00:00 queda visible.
        _timerSnoozeUntil = DateTime.UtcNow.AddSeconds(TimerReshowSnoozeSeconds);
        ApplyTimerContentVisibility();
        RefreshTimerUI();
        // Solo el snapshot musical decide (desacoplado del control multimedia):
        var session = Current();
        if (session == null)
        {
            // Sin sesión: limpiar restos musicales antes de resolver el reposo
            // (002 MOD RF-14): pieza inactiva o nada según el toggle.
            ClearMusicResidue();
            _expanded = false;
            ShowInactiveOrHidden();
        }
        else if (_expanded) RefreshUi(session);
        else ShowMusicCompact(session);
    }
}
