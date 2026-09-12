// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Utils;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using static WindowsMediaController.MediaManager;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Fluent Island: isla superior central. Oculta / compacta / expandida.
/// Sigue a la sesión que empezó última; expandido manda sobre compacto.
/// Animación de CAJA ÚNICA con muelles por frame (sin Storyboards): el
/// ancho/alto reales del IslandBox se interpolan, así no hay dos cuadrados
/// peleándose por Visibility. Re-apuntar a mitad de vuelo es gratis.
/// </summary>
public partial class IslandWindow : Window
{
    private const double ExpandedIslandWidth = 360;
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly Brush IslandBorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush MediaPlayingBrush = new SolidColorBrush(Color.FromRgb(0xB6, 0xF0, 0xB5));
    private static readonly Brush MediaPausedBrush = new SolidColorBrush(Color.FromRgb(0x76, 0x7B, 0x79));

    private readonly MainWindow _main;
    private readonly Dictionary<string, DateTime> _lastPlay = new();
    private string? _currentId;
    private bool _expanded;
    private bool _drag;
    private CancellationTokenSource? _hideCts;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _hoverPoll;
    private readonly Visualizer _eq = new(Visualizer.Options.Island);
    private int _eqBars = -1;
    private bool _eqRunning;

    // Muelles: p morph 0..1 (compacto->expandido), q entrada 0..1 (oculto->visible)
    private double _p, _pT, _pv;
    private double _q, _qT, _qv;
    private bool _loopOn;
    private bool _hidingViaCompact; // salida directa desde expandido: p y q van a 0 a la vez
    private double _hexp = 172;
    private string _lastTrackKey = "";
    private int _popVersion;
    private bool _popPlaying;
    private double _pop; // 0..1 pulso de cambio de pista
    private bool _albumArtHovering;
    private bool _hasAlbumCover;
    private BitmapImage? _displayedAlbumArt;
    private int _albumFlipVersion;
    private bool _albumFlipRunning;

    // Album-art background, matching the taskbar widget's blurred/rotating viewport.
    private BitmapImage? _backgroundIcon;
    private BitmapImage? _bakedIcon;
    private BitmapSource? _bakedBackground;
    private double _bakedSideDip;
    private int _bakedBlurRadius;
    private BitmapImage? _bakingIcon;
    private double _bakingSide;
    private int _bakingBlurRadius;
    private RotateTransform? _backgroundRotateTransform;
    private bool _backgroundRotationActive;
    private bool _backgroundRotationAnimationRunning;
    private bool _backgroundRotationPaused;
    private bool _backgroundRotationWasUp;
    private double _appliedRotationDurationSeconds;
    private int? _appliedDesiredFrameRate;
    private double _pausedRotationAngle;
    private BitmapSource? _backgroundCrossfadeTarget;
    private int _backgroundCrossfadeVersion;
    private int _backgroundGeneration;
    private bool _disposed;

    public IslandWindow(MainWindow main)
    {
        _main = main;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        ApplyAlbumArtRadius();
        CompactEq.Source = _eq.Bitmap;
        ExpandedEq.Source = _eq.Bitmap;
        ApplyStyle();
        UpdateBackgroundMode();
        SyncMeasuredHeight();
        SnapFrame();
        Show();
        Visibility = Visibility.Visible;
        PositionTopCenter();
        var mm = _main.mediaManager;
        mm.OnAnyPlaybackStateChanged += OnPlayState;
        mm.OnAnyMediaPropertyChanged += OnMediaProp;
        mm.OnAnySessionClosed += OnClosed;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        _hoverPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverPoll.Tick += (_, _) => PollFringeHover();
        _hoverPoll.Start();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowHelper.SetTopmost(this);
        PositionTopCenter();
        SyncMeasuredHeight();
        SnapFrame();
    }

    private void NotePlay(string id) { _lastPlay[id] = DateTime.Now; _currentId = id; }

    private MediaSession? NewestPlaying()
    {
        MediaSession? best = null;
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
        {
            if (!_main.IsSessionAllowed(s)) continue;
            if (s.ControlSession?.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
            if (best == null || (_lastPlay.TryGetValue(s.Id, out var t) && (!_lastPlay.TryGetValue(best.Id, out var bt) || t > bt)))
                best = s;
        }
        return best;
    }

    private MediaSession? Current()
    {
        if (_currentId == null) return null;
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
            if (s.Id == _currentId && _main.IsSessionAllowed(s)) return s;
        return null;
    }

    private MediaSession? FirstAllowed()
    {
        foreach (var s in _main.mediaManager.CurrentMediaSessions.Values)
            if (_main.IsSessionAllowed(s)) return s;
        return null;
    }

    private GlobalSystemMediaTransportControlsSessionPlaybackStatus? _lastStatus;

    private void OnPlayState(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo? info)
    {
        var status = info?.PlaybackStatus ?? session.ControlSession?.GetPlaybackInfo()?.PlaybackStatus;
        Dispatcher.Invoke(() =>
        {
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                NotePlay(session.Id);
                _lastStatus = status;
                PaintGlyph();
                if (!SettingsManager.Current.IslandShowOnPlayPause) return;
                if (_expanded) RefreshUi(session, status);
                else ShowCompact(session, status);
            }
            else if (session.Id == _currentId || NewestPlaying() == null)
            {
                var next = NewestPlaying();
                if (next != null)
                {
                    _currentId = next.Id;
                    _lastStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    PaintGlyph();
                    if (!SettingsManager.Current.IslandShowOnPlayPause) return;
                    if (_expanded) RefreshUi(next);
                    else ShowCompact(next);
                }
                else
                {
                    _lastStatus = status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
                    PaintGlyph();
                    if (SettingsManager.Current.IslandShowOnPause && status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                        ShowCompact(session, status);
                    else
                        HidePerMode();
                }
            }
        });
    }

    private void OnMediaProp(MediaSession session, GlobalSystemMediaTransportControlsSessionMediaProperties _)
    {
        Dispatcher.Invoke(() =>
        {
            var show = session.ControlSession?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                ? session : NewestPlaying();
            if (show == null) return;
            if (show.ControlSession?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                NotePlay(show.Id);
            if (_expanded || IslandBox.Visibility == Visibility.Visible) RefreshUi(show);
            else if (SettingsManager.Current.IslandShowOnTrackChange) ShowCompact(show);
        });
    }

    private void OnClosed(MediaSession session)
    {
        _lastPlay.Remove(session.Id);
        Dispatcher.Invoke(() =>
        {
            if (session.Id != _currentId) return;
            _currentId = null;
            var next = NewestPlaying();
            if (next != null)
            {
                _currentId = next.Id;
                if (!SettingsManager.Current.IslandShowOnPlayPause) return;
                if (_expanded) RefreshUi(next);
                else ShowCompact(next);
            }
            else
            {
                _lastStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
                PaintGlyph();
                HidePerMode();
            }
        });
    }

    // --- estados ---

    private bool IsBoxShown => IslandBox.Visibility == Visibility.Visible;

    private void ArmTemporaryHide()
    {
        if (SettingsManager.Current.IslandVisibilityMode != 1 || _expanded) return;
        _hideCts?.Cancel();
        var cts = _hideCts = new CancellationTokenSource();
        int ms = Math.Clamp(SettingsManager.Current.IslandVisibilityDuration, 1000, 10000);
        _ = Task.Delay(ms).ContinueWith(_ =>
            Dispatcher.Invoke(() => { if (!cts.IsCancellationRequested && !_expanded && !IsMouseOverBoxOrStrip()) GoHidden(); }));
    }

    private void ShowCompact(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null, bool forceAlbumFlip = false)
    {
        _hideCts?.Cancel();
        _hidingViaCompact = false;
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) { SnapHidden(); return; }
        RefreshUi(session, knownStatus, forceAlbumFlip);
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
        ArmTemporaryHide();
    }

    private void HidePerMode()
    {
        _hideCts?.Cancel();
        UpdateRotationPauseState();
        if (IsMouseOverBoxOrStrip())
        {
            if (!_expanded && Current() is { } session)
                ExpandSession(session);
            return;
        }
        if (_expanded)
        {
            _expanded = false;
            if (AnimationsEnabled) { _hidingViaCompact = true; _pT = 0; _qT = 0; EnsureLoop(); return; }
            GoHidden(); return;
        }
        GoHidden();
    }

    private void CollapseAll() => SnapHidden();

    private void GoHidden()
    {
        if (!AnimationsEnabled || !IsBoxShown) { SnapHidden(); return; }
        if (_hidingViaCompact) return;
        if (Math.Abs(_p) > 0.05)
        {
            _expanded = false; _hidingViaCompact = true; _pT = 0; _qT = 0; EnsureLoop(); return;
        }
        _qT = 0;
        EnsureLoop();
    }

    private void SnapCompact()
    {
        _p = _pT = 0; _pv = 0;
        _q = _qT = 1; _qv = 0;
        _pop = 0; _popVersion++; _popPlaying = false;
        StopLoop();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
    }

    private void SnapHidden()
    {
        _hidingViaCompact = false;
        _p = _pT = 0; _pv = 0;
        _q = _qT = 0; _qv = 0;
        _pop = 0; _popVersion++; _popPlaying = false;
        StopLoop();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Collapsed;
        UpdateRotationPauseState();
        UpdateLine();
        UpdateMediaStatusDot();
    }

    private void Box_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        var session = Current() ?? NewestPlaying() ?? FirstAllowed();
        if (session == null) return;
        ExpandSession(session);
    }

    private void PollFringeHover()
    {
        if (_expanded || _drag) return;
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        if (!NativeMethods.GetCursorPos(out var p)) return;
        var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
        if (primary.monitorArea.Width == 0) return;
        double tol = Math.Clamp(SettingsManager.Current.IslandHoverTolerance, 4, 30) * primary.dpiX / 96.0;
        double halfRaw = ExpandedIslandWidth * 0.5 * primary.dpiX / 96.0 + tol;
        double cx = primary.workArea.Left + primary.workArea.Width / 2;
        if (Math.Abs(p.X - cx) > halfRaw) return;
        if (p.Y < primary.workArea.Top - 2 || p.Y > primary.workArea.Top + tol + 4) return;
        var session = Current() ?? NewestPlaying() ?? FirstAllowed();
        if (session != null) ExpandSession(session);
    }

    private void ExpandSession(MediaSession session)
    {
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        _hideCts?.Cancel();
        _hidingViaCompact = false;
        _currentId = session.Id;
        bool wasExpanded = _expanded;
        RefreshUi(session);
        _expanded = true;
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled)
        {
            _p = _pT = 1; _pv = 0;
            _q = _qT = 1; _qv = 0;
            _pop = 0; _popPlaying = false;
            ApplyFrame();
            IslandBox.Visibility = Visibility.Visible;
            UpdateMediaStatusDot();
            UpdateRotationPauseState();
            return;
        }
        if (wasExpanded) return; // ya expandido: solo actualizar datos
        _pT = 1; _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
        UpdateMediaStatusDot();
        UpdateRotationPauseState();
        EnsureLoop();
    }

    private void Box_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_drag || Mouse.LeftButton == MouseButtonState.Pressed) return;
        LeaveHover();
    }

    private void LeaveHover()
    {
        if (!_expanded) return;
        _expanded = false;
        _hidingViaCompact = false;
        if (SettingsManager.Current.IslandVisibilityMode == 1) { HidePerMode(); return; }
        var session = Current();
        var playing = session?.ControlSession?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (playing && !Suppressed())
        {
            UpdateLine();
            PositionTopCenter();
            if (!AnimationsEnabled) { _p = _pT = 0; _pv = 0; ApplyFrame(); }
            else { _pT = 0; EnsureLoop(); }
        }
        else HidePerMode();
    }

    // --- motor de muelle ---

    private bool AnimationsEnabled => SettingsManager.Current.IslandAnimated && SettingsManager.Current.FlyoutAnimationSpeed != 0;
    private bool IsNotch => Math.Clamp(SettingsManager.Current.IslandStyle, 0, 1) == 1;
    private double IslandRadius => Math.Clamp(SettingsManager.Current.IslandBorderRadius, 0, 40);

    public void RefreshEnabledState()
    {
        if (!SettingsManager.Current.IslandEnabled)
        {
            SnapHidden();
            UpdateBackgroundMode();
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        if (!Suppressed())
        {
            var session = NewestPlaying();
            if (session != null) ShowCompact(session);
        }
        RefreshAppearance();
    }

    public void RefreshVisibilityState()
    {
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        var session = Current() ?? NewestPlaying() ?? FirstAllowed();
        if (session == null) return;

        var status = SafeStatus(session) ?? _lastStatus;
        bool showForStatus = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? SettingsManager.Current.IslandShowOnPlayPause
            : status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                && SettingsManager.Current.IslandShowOnPause;
        if (showForStatus)
        {
            _currentId = session.Id;
            if (_expanded) RefreshUi(session, status);
            else ShowCompact(session, status);
        }
        else if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
                 status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
        {
            HidePerMode();
        }
    }

    public void RefreshAppearance()
    {
        ApplyStyle();
        ApplyAlbumArtRadius();
        UpdateLine();
        ApplyFrame();
    }

    // Apple-ish: muelle subamortiguado suave, escalado con la duración global.
    private void GetSpring(out double kP, out double cP, out double kQ, out double cQ)
    {
        double configuredDuration = MainWindow.getDuration();
        double durationScale = configuredDuration > 0 ? configuredDuration / 300.0 : 1.0;
        double frequencyScale = 1.0 / (durationScale * durationScale);
        double dampingScale = 1.0 / durationScale;
        kP = 520 * frequencyScale; cP = 34 * dampingScale;
        kQ = 200 * frequencyScale; cQ = 28 * dampingScale; // ambos estilos emergen desde el centro como Island
        if (IsNotch) kP *= 1.05;
    }

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
        else dt = Math.Clamp((now - _lastTick).TotalSeconds, 1.0 / 240.0, 1.0 / 25.0);
        _lastTick = now;

        GetSpring(out double kP, out double cP, out double kQ, out double cQ);
        Step(ref _p, ref _pv, _pT, kP, cP, dt);
        Step(ref _q, ref _qv, _qT, kQ, cQ, dt);
        if (_popPlaying) StepPop(dt);
        ApplyFrame();

        bool pSettled = Math.Abs(_p - _pT) < 0.002 && Math.Abs(_pv) < 0.02;
        bool qSettled = Math.Abs(_q - _qT) < 0.002 && Math.Abs(_qv) < 0.02;
        if (pSettled) { _p = _pT; _pv = 0; }
        if (qSettled) { _q = _qT; _qv = 0; }

        bool popSettled = !_popPlaying;

        if (pSettled && qSettled && popSettled)
        {
            StopLoop();
            _lastTick = TimeSpan.Zero;
            if (_qT == 0 && _q == 0) { IslandBox.Visibility = Visibility.Collapsed; UpdateLine(); }
            // Si llegamos a compacto vía hidingViaCompact y no hay q pendiente, ya se ocultó arriba
        }
        else if (_qT == 0 && _q <= 0.12)
        {
            _hidingViaCompact = false;
            IslandBox.Visibility = Visibility.Collapsed;
            UpdateLine();
        }
    }

    private static void Step(ref double x, ref double v, double target, double k, double c, double dt)
    {
        double a = (target - x) * k - v * c;
        v += a * dt;
        x += v * dt;
        x = Math.Clamp(x, -0.15, 1.15); // deja un poco de overshoot visible
    }

    private double _popV;
    private double _popPhase; // 0 ida, 1 vuelta
    private void StepPop(double dt)
    {
        // Ida 0->1 rápida, vuelta 1->0 amortiguada: un solo ciclo
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

    private void SyncMeasuredHeight()
    {
        try
        {
            // Medir la altura expandida real con el ancho objetivo (360).
            // ExpandedLayer está siempre en el árbol (Opacity 0 cuando compacto),
            // así que es medible.
            ExpandedLayer.Measure(new Size(ExpandedIslandWidth, double.PositiveInfinity));
            double h = ExpandedLayer.DesiredSize.Height; // DesiredSize ya incluye el Margin vertical
            if (h > 60 && h < 260) _hexp = h;
        }
        catch { }
    }

    private static double Smooth01(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private void SnapFrame()
    {
        // Estado base coherente antes del primer frame
        _p = _pT; _q = _qT;
        _pv = _qv = 0;
        ApplyFrame();
    }

    private void ApplyFrame()
    {
        double p = Math.Clamp(_p, 0, 1);
        double q = Math.Clamp(_q, 0, 1);
        // Pop de pista atenúa con q (invisible -> no pulsa)
        double pop = _popPlaying ? _pop * q : 0;
        double exitTailOpacity = _qT == 0
            ? Math.Pow(Smooth01(Math.Clamp((q - 0.12) / 0.20, 0, 1)), 3)
            : 1;
        if (_hidingViaCompact)
            exitTailOpacity *= Math.Pow(Smooth01(Math.Clamp((p - 0.12) / 0.38, 0, 1)), 3);

        bool notch = IsNotch;
        double w, h;
        if (notch)
        {
            // Notch: mismo reveal que Island: punto central -> compacto -> expandido.
            const double notchDot = 26;
            double compactW = 200;
            double dotT = Math.Clamp(q / 0.32, 0, 1);
            double stretchT = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double baseW = q < 0.32 ? notchDot : Lerp(notchDot, compactW, stretchT);
            w = Lerp(baseW, ExpandedIslandWidth, Smooth01(p));
            h = Lerp(34, _hexp, Smooth01(p));
            IslandBox.Width = w;
            IslandBox.Height = h;
            double revealOpacity = Smooth01(Math.Clamp(q / 0.38, 0, 1));
            IslandBox.Opacity = revealOpacity * revealOpacity * exitTailOpacity;
            double radius = Math.Min(IslandRadius, Math.Min(w, h) / 2);
            IslandBox.CornerRadius = new CornerRadius(0, 0, radius, radius);
            BoxTranslate.Y = 0;
            BoxScale.ScaleX = BoxScale.ScaleY = Lerp(0.68, 1, Smooth01(dotT));
            IslandBox.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        else
        {
            // Pill: oculto -> punto 26px (circular) -> cápsula 240px -> expandido 480px
            const double pillDot = 26;
            double dotT = Math.Clamp(q / 0.32, 0, 1);
            double stretchT = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double baseW = q < 0.32 ? pillDot : Lerp(pillDot, 240, stretchT);
            w = Lerp(baseW, ExpandedIslandWidth, Smooth01(p));
            h = Lerp(34, _hexp, Smooth01(p));
            IslandBox.Width = w;
            IslandBox.Height = h;
            IslandBox.Opacity = Smooth01(Math.Clamp(q / 0.38, 0, 1)) * exitTailOpacity;
            // Radio: círculo perfecto mientras es punto, cápsula después
            double cr = baseW <= pillDot + 0.5 && p < 0.02
                ? pillDot / 2
                : Math.Min(IslandRadius, Math.Min(w, h) / 2);
            if (p > 0.02) cr = Math.Min(IslandRadius, Math.Min(w, h) / 2); // expandido siempre pill
            IslandBox.CornerRadius = new CornerRadius(cr);
            BoxTranslate.Y = 0;
            BoxScale.ScaleX = BoxScale.ScaleY = Lerp(0.68, 1, Smooth01(dotT));
            IslandBox.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        ApplyIslandClip(w, h, IslandBox.CornerRadius);
        LayoutBackground(w, h);

        // Crossfade de capas + morph del contenido (Apple: el álbum y el título respiran)
        // En pill, los elementos divergen desde el centro durante el estiramiento
        double compactOp, expandedOp;
        if (notch)
        {
            double stretchT2 = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double contentT = Math.Clamp((stretchT2 - 0.42) / 0.58, 0, 1);
            double dotT2 = Math.Clamp(q / 0.32, 0, 1);
            compactOp = (1 - Smooth01(Math.Clamp(p * 2.2, 0, 1))) * Smooth01(contentT);
            if (q < 0.32) compactOp = 0;
            else compactOp *= Lerp(0.85, 1, dotT2);
            expandedOp = Smooth01(Math.Clamp((p - 0.12) / 0.88, 0, 1));
            CompactLayer.Opacity = compactOp;
            CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.88, 1, Smooth01(contentT));
            if (q < 0.32)
                CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.75, 0.88, dotT2);

            // El contenido también florece desde el centro durante el reveal.
            double diverge = Math.Pow(Smooth01(stretchT2), 1.25);
            CompactArtTranslate.X = Lerp(42, 0, diverge);
            CompactTitleTranslate.X = Lerp(6, 0, diverge);
            CompactEqTranslate.X = Lerp(-36, 0, diverge);
            CompactTitleScale2.ScaleX = CompactTitleScale2.ScaleY = Lerp(0.92, 1, diverge);
            double titleOp = Smooth01(Math.Clamp((stretchT2 - 0.50) / 0.50, 0, 1));
            double eqOp = Smooth01(Math.Clamp((stretchT2 - 0.55) / 0.45, 0, 1));
            CompactTitle.Opacity = q < 0.32 ? 0 : titleOp;
            CompactEq.Opacity = q < 0.32 ? 0 : eqOp;
            CompactArtWrap.Opacity = q < 0.15 ? 0 : (q < 0.32 ? Smooth01(dotT2) : 1);
            double pScale = Lerp(1, 0.92, Smooth01(p));
            CompactScale.ScaleX *= pScale;
            CompactScale.ScaleY *= pScale;
        }
        else
        {
            double stretchT2 = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double contentT = Math.Clamp((stretchT2 - 0.42) / 0.58, 0, 1);
            double dotT2 = Math.Clamp(q / 0.32, 0, 1);
            compactOp = (1 - Smooth01(Math.Clamp(p * 2.2, 0, 1))) * Smooth01(contentT);
            if (q < 0.32) compactOp = 0;
            else compactOp *= Lerp(0.85, 1, dotT2);
            expandedOp = Smooth01(Math.Clamp((p - 0.12) / 0.88, 0, 1));
            CompactLayer.Opacity = compactOp;
            CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.88, 1, Smooth01(contentT));
            if (q < 0.32)
                CompactScale.ScaleX = CompactScale.ScaleY = Lerp(0.75, 0.88, dotT2);

            // Diverge from center — más apiñado al centro
            double diverge = Math.Pow(Smooth01(stretchT2), 1.25);
            CompactArtTranslate.X = Lerp(42, 0, diverge);
            CompactTitleTranslate.X = Lerp(6, 0, diverge);
            CompactEqTranslate.X = Lerp(-36, 0, diverge);
            CompactTitleScale2.ScaleX = CompactTitleScale2.ScaleY = Lerp(0.92, 1, diverge);
            double titleOp = Smooth01(Math.Clamp((stretchT2 - 0.50) / 0.50, 0, 1));
            double eqOp = Smooth01(Math.Clamp((stretchT2 - 0.55) / 0.45, 0, 1));
            CompactTitle.Opacity = q < 0.32 ? 0 : titleOp;
            CompactEq.Opacity = q < 0.32 ? 0 : eqOp;
            CompactArtWrap.Opacity = q < 0.15 ? 0 : (q < 0.32 ? Smooth01(dotT2) : 1);
        }
        CompactLayer.IsHitTestVisible = p < 0.6 && q > 0.35;
        ExpandedLayer.Opacity = expandedOp * (notch ? q : 1);
        ExpandedLayer.IsHitTestVisible = p > 0.4 && q > 0.4;

        double artS = Lerp(0.88, 1, Smooth01(Math.Clamp((p - 0.05) / 0.95, 0, 1)));
        // Pop suma un leve bump al arte/título en cambio de pista
        artS += pop * 0.06;
        ExpandedArtScale.ScaleX = ExpandedArtScale.ScaleY = artS;

        double titleS = Lerp(0.90, 1, Smooth01(Math.Clamp((p - 0.08) / 0.9, 0, 1))) + pop * 0.05;
        SongTitleScale.ScaleX = SongTitleScale.ScaleY = titleS;
        SongTitle.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.12) / 0.7, 0, 1)));
        SongTitleTranslate.Y = Lerp(6, 0, Smooth01(Math.Clamp((p - 0.12) / 0.7, 0, 1)));

        SongArtist.Opacity = Lerp(0, 0.5, Smooth01(Math.Clamp((p - 0.22) / 0.6, 0, 1)));
        SongArtistTranslate.Y = Lerp(6, 0, Smooth01(Math.Clamp((p - 0.22) / 0.6, 0, 1)));

        ExpandedEq.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.18) / 0.6, 0, 1)));
        SeekRow.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.30) / 0.5, 0, 1)));
        SeekTranslate.Y = Lerp(8, 0, Smooth01(Math.Clamp((p - 0.30) / 0.5, 0, 1)));
        ControlsRow.Opacity = Lerp(0, 1, Smooth01(Math.Clamp((p - 0.38) / 0.5, 0, 1)));
        ControlsTranslate.Y = Lerp(8, 0, Smooth01(Math.Clamp((p - 0.38) / 0.5, 0, 1)));
    }

    private void PlayTrackPop()
    {
        if (!AnimationsEnabled || _expanded) return;
        if (!IsBoxShown || _q < 0.6) return;
        if (_popPlaying) return;
        _popVersion++;
        _pop = 0; _popV = 0; _popPhase = 0; _popPlaying = true;
        EnsureLoop();
    }

    // --- presentación ---

    private void RefreshUi(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null, bool forceAlbumFlip = false)
    {
        var status = knownStatus ?? SafeStatus(session) ?? _lastStatus;
        if (status != null) _lastStatus = status;
        PaintGlyph();
        BitmapImage? art = null;
        string title = "Título desconocido", artist = "Artista desconocido";
        try
        {
            var props = session.ControlSession.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
            if (props != null)
            {
                if (!string.IsNullOrWhiteSpace(props.Title)) title = props.Title;
                if (!string.IsNullOrWhiteSpace(props.Artist)) artist = props.Artist;
                art = BitmapHelper.GetThumbnail(props.Thumbnail);
            }
        }
        catch { }
        SongTitle.Text = title;
        SongArtist.Text = artist;
        CompactTitle.Text = title;
        SetBackground(art);
        string trackKey = title + "\n" + artist + "\n" + (art != null);
        bool trackChanged = _lastTrackKey != "" && trackKey != _lastTrackKey;
        _lastTrackKey = trackKey;
        if (trackChanged || forceAlbumFlip)
            StartAlbumFlip(art);
        else if (!_albumFlipRunning)
            SetAlbumArt(art);
        if (trackChanged && SettingsManager.Current.IslandShowOnTrackChange) PlayTrackPop();
        BitmapHelper.GetDominantColors();
        ApplyCapabilities(session);
        UpdateSeek(session);
        SyncMeasuredHeight();
        if (_expanded || _p > 0.05) ApplyFrame();
    }

    private void SetAlbumArt(BitmapImage? art)
    {
        StopAlbumFlip();
        CompactArt.Source = art;
        ExpandedArt.Source = art;
        _displayedAlbumArt = art;
        _hasAlbumCover = art != null;
        CompactNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
        ExpandedNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
        UpdateAlbumArtOverlay();
    }

    private void StartAlbumFlip(BitmapImage? art)
    {
        StopAlbumFlip();
        if (!AnimationsEnabled)
        {
            SetAlbumArt(art);
            return;
        }

        _albumFlipRunning = true;
        int version = _albumFlipVersion;
        CompactArtFlipScale.ScaleX = ExpandedArtFlipScale.ScaleX = 1;
        CompactArtFlipScale.ScaleY = ExpandedArtFlipScale.ScaleY = 1;
        _hasAlbumCover = _displayedAlbumArt != null;
        UpdateAlbumArtOverlay();

        var outgoing = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        outgoing.Completed += (_, _) =>
        {
            if (version != _albumFlipVersion) return;
            CompactArt.Source = art;
            ExpandedArt.Source = art;
            _displayedAlbumArt = art;
            _hasAlbumCover = art != null;
            CompactNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
            ExpandedNote.Visibility = _hasAlbumCover ? Visibility.Collapsed : Visibility.Visible;
            UpdateAlbumArtOverlay();

            var incoming = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(170),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            incoming.Completed += (_, _) =>
            {
                if (version != _albumFlipVersion) return;
                _albumFlipRunning = false;
                CompactArtFlipScale.ScaleX = ExpandedArtFlipScale.ScaleX = 1;
                CompactArtFlipScale.ScaleY = ExpandedArtFlipScale.ScaleY = 1;
                UpdateAlbumArtOverlay();
            };
            CompactArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, incoming);
            ExpandedArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, incoming.Clone());
        };

        CompactArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, outgoing);
        ExpandedArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, outgoing.Clone());
    }

    private void StopAlbumFlip()
    {
        _albumFlipVersion++;
        _albumFlipRunning = false;
        CompactArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ExpandedArtFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CompactArtFlipScale.ScaleX = CompactArtFlipScale.ScaleY = 1;
        ExpandedArtFlipScale.ScaleX = ExpandedArtFlipScale.ScaleY = 1;
    }

    private void ApplyCapabilities(MediaSession session)
    {
        bool canPlay = false, canPrev = false, canNext = false, canSeek = false;
        try
        {
            var c = session.ControlSession.GetPlaybackInfo()?.Controls;
            if (c != null)
            {
                canPlay = c.IsPlayEnabled || c.IsPauseEnabled;
                canPrev = c.IsPreviousEnabled;
                canNext = c.IsNextEnabled;
            }
            canSeek = session.ControlSession.GetTimelineProperties().MaxSeekTime.TotalSeconds >= 1;
        }
        catch { }
        SeekRow.Visibility = canSeek ? Visibility.Visible : Visibility.Hidden;
        BtnPlay.IsEnabled = canPlay;
        BtnPlay.Opacity = canPlay ? 1 : 0.35;
        BtnPrev.IsEnabled = canPrev;
        BtnPrev.Opacity = canPrev ? 1 : 0.35;
        BtnNext.IsEnabled = canNext;
        BtnNext.Opacity = canNext ? 1 : 0.35;
    }

    private void PaintGlyph()
    {
        if (_lastStatus == null) return;
        PlayGlyph.Symbol = _lastStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? Wpf.Ui.Controls.SymbolRegular.Pause16 : Wpf.Ui.Controls.SymbolRegular.Play16;
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus? SafeStatus(MediaSession session)
    {
        try { return session.ControlSession?.GetPlaybackInfo()?.PlaybackStatus; }
        catch { return null; }
    }

    private static string Fmt(TimeSpan t) => t.ToString(t.Hours > 0 ? @"h\:mm\:ss" : @"m\:ss");

    private static string FmtRemaining(TimeSpan t) => "-" + Fmt(t < TimeSpan.Zero ? TimeSpan.Zero : t);

    private void UpdateTimelineVisual()
    {
        double maximum = Seekbar.Maximum;
        double ratio = maximum > 0 ? Math.Clamp(Seekbar.Value / maximum, 0, 1) : 0;
        TimelineProgress.Width = TimelineTrack.ActualWidth * ratio;
    }

    private void TimelineHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineVisual();

    private void Seekbar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTimelineVisual();
        if (!_drag || Seekbar.Maximum <= 0) return;
        var position = TimeSpan.FromSeconds(Math.Clamp(Seekbar.Value, 0, Seekbar.Maximum));
        PosText.Text = Fmt(position);
        DurText.Text = FmtRemaining(TimeSpan.FromSeconds(Seekbar.Maximum) - position);
    }

    private void UpdateSeek(MediaSession session)
    {
        try
        {
            var tl = session.ControlSession.GetTimelineProperties();
            if (tl.MaxSeekTime.TotalSeconds >= 1)
            {
                bool playing = session.ControlSession.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var pos = playing ? tl.Position + (DateTime.Now - tl.LastUpdatedTime.DateTime) : tl.Position;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (pos > tl.EndTime) pos = tl.EndTime;
                Seekbar.Maximum = tl.MaxSeekTime.TotalSeconds;
                if (!_drag) { Seekbar.Value = pos.TotalSeconds; PosText.Text = Fmt(pos); }
                DurText.Text = FmtRemaining(tl.MaxSeekTime - pos);
                UpdateTimelineVisual();
                return;
            }
        }
        catch { }
        Seekbar.Maximum = 100; Seekbar.Value = 0; PosText.Text = "0:00"; DurText.Text = "0:00";
        UpdateTimelineVisual();
    }

    private void Tick()
    {
        if (!SettingsManager.Current.IslandEnabled)
        {
            SnapHidden();
            Visibility = Visibility.Collapsed;
            return;
        }
        if (Suppressed())
        {
            if (IsBoxShown) SnapHidden();
            Visibility = Visibility.Collapsed;
            return;
        }
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        if (_expanded && !_drag && !IsMouseOverBoxOrStrip()) LeaveHover();
        SyncEq();
        var s = Current();
        if (s != null && _expanded) UpdateSeek(s);
        UpdateLine();
    }

    private bool IsMouseOverBoxOrStrip()
    {
        try
        {
            if (IsMouseOver) return true;
            // IslandBox y HoverStrip son hijos con Background (hit-testables),
            // pero Root es null-background: IsMouseOver de la Window puede ser falso
            // aunque el ratón esté en la franja.
            if (IslandBox.IsMouseOver || HoverStrip.IsMouseOver) return true;
            if (!NativeMethods.GetCursorPos(out var p)) return false;
            var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
            if (primary.monitorArea.Width == 0) return false;
            // Si el cursor está en la isla expandida (medida dinámica _hexp), no repliegues.
            double tol = Math.Clamp(SettingsManager.Current.IslandHoverTolerance, 4, 30) * primary.dpiX / 96.0;
            double halfW = (IsNotch ? 200 : 240) * 0.5;
            // En expandido la isla es de 360px de ancho
            if (_expanded || _p > 0.2) halfW = ExpandedIslandWidth * 0.5;
            halfW = halfW * primary.dpiX / 96.0 + tol;
            double cx = primary.workArea.Left + primary.workArea.Width / 2;
            if (Math.Abs(p.X - cx) > halfW) return false;
            double top = primary.workArea.Top;
            double bottom = top + (_expanded ? _hexp + 8 : 34) * primary.dpiY / 96.0 + tol;
            // No confundir la barra de tareas inferior con hover
            if (p.Y < top - 2 || p.Y > bottom) return false;
            return true;
        }
        catch { return false; }
    }

    private void SyncEq()
    {
        bool want = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandEqEnabled;
        if (want && !_eqRunning) { _eqRunning = true; _eqBars = -1; _eq.Start(); }
        else if (!want && _eqRunning) { _eqRunning = false; _eq.Stop(); }
        int bars = Math.Clamp(SettingsManager.Current.IslandEqBarCount, 1, 10);
        if (_eqRunning && bars != _eqBars) { _eqBars = bars; _eq.ResizeBarList(bars); }
    }

    private void UpdateLine()
    {
        ApplyStyle();
        HoverStrip.Height = Math.Clamp(SettingsManager.Current.IslandHoverTolerance, 4, 30);
        bool alive = IsBoxShown || _qT > 0.02 || Current() != null || NewestPlaying() != null || FirstAllowed() != null;
        ActivityLine.Visibility = SettingsManager.Current.IslandActivityLine && alive ? Visibility.Visible : Visibility.Collapsed;
        var eqVis = SettingsManager.Current.IslandEqEnabled ? Visibility.Visible : Visibility.Collapsed;
        CompactEq.Visibility = eqVis;
        ExpandedEq.Visibility = eqVis;
        UpdateMediaStatusDot();
    }

    private void UpdateMediaStatusDot()
    {
        var session = Current() ?? FirstAllowed();
        bool hidden = Visibility == Visibility.Visible && !IsBoxShown && !Suppressed();
        var status = session == null ? null : SafeStatus(session) ?? _lastStatus;
        if (!hidden || status == null)
        {
            MediaStatusDot.Visibility = Visibility.Collapsed;
            return;
        }

        MediaStatusDot.Background = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? MediaPlayingBrush
            : MediaPausedBrush;
        MediaStatusDot.Visibility = Visibility.Visible;
    }

    private int _appliedStyle = -1;

    private void ApplyStyle()
    {
        int style = Math.Clamp(SettingsManager.Current.IslandStyle, 0, 1);
        if (style != _appliedStyle)
        {
            _appliedStyle = style;
            if (style == 1)
            {
                IslandBox.BorderThickness = new Thickness(1, 0, 1, 1);
                CompactLayer.Width = 200;
            }
            else
            {
                IslandBox.BorderThickness = new Thickness(1);
                CompactLayer.Width = 240;
            }
        }

        IslandBox.BorderBrush = SettingsManager.Current.IslandBorderEnabled ? IslandBorderBrush : Brushes.Transparent;

        // CornerRadius lo gobierna ApplyFrame por frame (punto 26→cápsula)
        SyncMeasuredHeight();
        if (!_loopOn) ApplyFrame();
    }

    private void ApplyAlbumArtRadius()
    {
        double radius = Math.Clamp(SettingsManager.Current.IslandAlbumArtRadius, 0, 32);
        double compactRadius = Math.Min(radius, 11);
        double expandedRadius = Math.Min(radius, 32);
        var compactCorners = new CornerRadius(compactRadius);
        var expandedCorners = new CornerRadius(expandedRadius);

        CompactArtWrap.CornerRadius = compactCorners;
        CompactAlbumOverlay.CornerRadius = compactCorners;
        CompactArtWrap.Clip = CreateAlbumArtClip(22, compactRadius);
        CompactAlbumOverlay.Clip = CreateAlbumArtClip(22, compactRadius);

        ExpandedArtWrap.CornerRadius = expandedCorners;
        ExpandedAlbumOverlay.CornerRadius = expandedCorners;
        ExpandedArtWrap.Clip = CreateAlbumArtClip(64, expandedRadius);
        ExpandedAlbumOverlay.Clip = CreateAlbumArtClip(64, expandedRadius);
    }

    private static RectangleGeometry CreateAlbumArtClip(double size, double radius)
    {
        var clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
        clip.Freeze();
        return clip;
    }

    public void UpdateBackgroundMode()
    {
        ApplyBackgroundSettings();

        bool enabled = SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandBackgroundBlur;
        if (!enabled || _backgroundIcon == null)
        {
            StopBackgroundRotation();
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundImageNext.Visibility = Visibility.Collapsed;
            return;
        }

        BackgroundImage.Visibility = Visibility.Visible;
        if (SettingsManager.Current.IslandBackgroundRotate)
        {
            ApplyBackgroundRotation();
        }
        else
        {
            StopBackgroundRotation();
            LayoutBackgroundToFill();
            BeginBackgroundCrossfade(_backgroundIcon, Math.Max(MainWindow.getDuration(), 1));
        }

        UpdateRotationPauseState();
    }

    public void RefreshBackgroundRotationFrameRate()
    {
        if (!SettingsManager.Current.IslandBackgroundBlur ||
            !SettingsManager.Current.IslandBackgroundRotate ||
            !_backgroundRotationActive ||
            _backgroundRotateTransform == null)
            return;

        ApplyBackgroundRotation(_backgroundRotateTransform.Angle);
    }

    private void ApplyBackgroundSettings()
    {
        double opacity = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurIntensity, 0, 100) / 100.0;
        double radius = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurRadius, 0, 150);
        BackgroundImage.Opacity = opacity;
        BackgroundImageNext.Opacity = opacity;
        BackgroundImageBlurEffect.Radius = radius;
        BackgroundImageNextBlurEffect.Radius = radius;
    }

    private void ApplyBackgroundRotation(double? forcedStartAngle = null)
    {
        double width = IslandBox.Width > 0 ? IslandBox.Width : 240;
        double height = IslandBox.Height > 0 ? IslandBox.Height : 34;
        _backgroundRotationActive = true;

        _backgroundRotateTransform ??= new RotateTransform();
        BackgroundImage.RenderTransform = _backgroundRotateTransform;
        BackgroundImageNext.RenderTransform = _backgroundRotateTransform;
        BackgroundImage.Effect = null;
        BackgroundImageNext.Effect = null;
        BackgroundImage.CacheMode ??= new BitmapCache(0.5);
        BackgroundImageNext.CacheMode ??= new BitmapCache(0.5);

        double sizeMultiplier = Math.Max(SettingsManager.Current.IslandBackgroundRotateSize, 100) / 100.0;
        double discSide = Math.Max(Math.Max(ExpandedIslandWidth * sizeMultiplier, height * sizeMultiplier), ExpandedIslandWidth);
        double offsetX = discSide * 0.28;
        bool showLeftSide = SettingsManager.Current.IslandBackgroundRotateSide == 0;
        LayoutDiscLayer(BackgroundImage, width, height, discSide, offsetX, showLeftSide);
        LayoutDiscLayer(BackgroundImageNext, width, height, discSide, offsetX, showLeftSide);

        if (_backgroundIcon != null)
            UpdateBakedBackgroundAsync(_backgroundIcon, discSide);

        if (_backgroundRotationPaused)
            return;

        double durationSeconds = Math.Max(SettingsManager.Current.IslandBackgroundRotateDuration, 1);
        bool spinUp = SettingsManager.Current.IslandBackgroundRotateDirection == 1;
        int? desiredFrameRate = SettingsManager.Current.IslandBackgroundRotateHighRefreshRate ? null : 30;
        bool restart = forcedStartAngle.HasValue ||
                       !_backgroundRotationAnimationRunning ||
                       _backgroundRotationWasUp != spinUp ||
                       Math.Abs(_appliedRotationDurationSeconds - durationSeconds) > 0.01 ||
                       _appliedDesiredFrameRate != desiredFrameRate;
        if (!restart) return;

        double startAngle = forcedStartAngle ?? _backgroundRotateTransform.Angle;
        _backgroundRotationWasUp = spinUp;
        _backgroundRotationAnimationRunning = true;
        _appliedRotationDurationSeconds = durationSeconds;
        _appliedDesiredFrameRate = desiredFrameRate;
        var animation = new DoubleAnimation
        {
            From = startAngle,
            To = spinUp ? startAngle - 360 : startAngle + 360,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(animation, desiredFrameRate);
        _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void LayoutBackground(double width, double height)
    {
        BackgroundCanvas.Width = width;
        BackgroundCanvas.Height = height;
        if (_backgroundRotationActive)
        {
            double sizeMultiplier = Math.Max(SettingsManager.Current.IslandBackgroundRotateSize, 100) / 100.0;
            double discSide = Math.Max(Math.Max(ExpandedIslandWidth * sizeMultiplier, height * sizeMultiplier), ExpandedIslandWidth);
            double offsetX = discSide * 0.28;
            bool showLeftSide = SettingsManager.Current.IslandBackgroundRotateSide == 0;
            LayoutDiscLayer(BackgroundImage, width, height, discSide, offsetX, showLeftSide);
            LayoutDiscLayer(BackgroundImageNext, width, height, discSide, offsetX, showLeftSide);
        }
        else
        {
            LayoutBackgroundToFill();
        }
    }

    private void LayoutBackgroundToFill()
    {
        double width = IslandBox.Width > 0 ? IslandBox.Width : 240;
        double height = IslandBox.Height > 0 ? IslandBox.Height : 34;
        double side = Math.Max(Math.Max(width, height), 1);
        BackgroundCanvas.Width = width;
        BackgroundCanvas.Height = height;
        LayoutFillLayer(BackgroundImage, width, height, side);
        LayoutFillLayer(BackgroundImageNext, width, height, side);
    }

    private static void LayoutFillLayer(Image layer, double width, double height, double side)
    {
        layer.Width = side;
        layer.Height = side;
        layer.Margin = new Thickness(0);
        layer.Stretch = Stretch.UniformToFill;
        Canvas.SetLeft(layer, (width - side) / 2);
        Canvas.SetTop(layer, (height - side) / 2);
    }

    private static void LayoutDiscLayer(Image layer, double width, double height, double discSide, double offsetX, bool showLeftSide)
    {
        layer.Width = discSide;
        layer.Height = discSide;
        layer.Margin = new Thickness(0);
        layer.Stretch = Stretch.Fill;
        Canvas.SetLeft(layer, (width - discSide) / 2 + (showLeftSide ? offsetX : -offsetX));
        Canvas.SetTop(layer, (height - discSide) / 2);
    }

    private void SetBackground(BitmapImage? icon)
    {
        if (!ReferenceEquals(_backgroundIcon, icon))
        {
            _backgroundGeneration++;
            _bakingIcon = null;
        }
        _backgroundIcon = icon;
        if (icon == null)
        {
            StopBackgroundRotation();
            BackgroundImage.Source = null;
            ParkBackgroundNextLayer();
            BackgroundImage.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBackgroundMode();
    }

    private static BitmapSource? BakeBlurredBackground(BitmapImage icon, double discSide, double blurRadiusDips)
    {
        const int resolution = 256;
        double blurRadius = blurRadiusDips * resolution / Math.Max(discSide, 1);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawImage(icon, new Rect(0, 0, resolution, resolution));

        visual.Effect = new BlurEffect
        {
            Radius = blurRadius,
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Performance
        };

        var bitmap = new RenderTargetBitmap(resolution, resolution, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private async void UpdateBakedBackgroundAsync(BitmapImage icon, double discSide)
    {
        int version = _backgroundGeneration;
        double bakeSide = Math.Round(discSide / 16.0) * 16.0;
        int blurRadius = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurRadius, 0, 150);
        if (_bakedBackground != null && ReferenceEquals(_bakedIcon, icon) &&
            Math.Abs(_bakedSideDip - bakeSide) < 0.5 && _bakedBlurRadius == blurRadius)
        {
            if (_backgroundRotationActive && !ReferenceEquals(BackgroundImage.Source, _bakedBackground))
                BeginBackgroundCrossfade(_bakedBackground, Math.Max(MainWindow.getDuration(), 1));
            return;
        }

        if (ReferenceEquals(_bakingIcon, icon) && Math.Abs(_bakingSide - bakeSide) < 0.5 && _bakingBlurRadius == blurRadius)
            return;

        if (_bakedBackground == null)
        {
            BackgroundImage.Source = icon;
            BackgroundImage.Effect = BackgroundImageBlurEffect;
        }

        _bakingIcon = icon;
        _bakingSide = bakeSide;
        _bakingBlurRadius = blurRadius;
        BitmapSource? baked;
        try
        {
            baked = await Task.Run(() => BakeBlurredBackground(icon, bakeSide, blurRadius));
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to bake Fluent Island background");
            if (ReferenceEquals(_bakingIcon, icon)) _bakingIcon = null;
            return;
        }

        if (_disposed || version != _backgroundGeneration || !ReferenceEquals(_backgroundIcon, icon) ||
            !ReferenceEquals(_bakingIcon, icon) || Math.Abs(_bakingSide - bakeSide) >= 0.5 || _bakingBlurRadius != blurRadius)
            return;
        if (ReferenceEquals(_bakingIcon, icon) && Math.Abs(_bakingSide - bakeSide) < 0.5)
            _bakingIcon = null;
        if (baked == null) return;

        _bakedIcon = icon;
        _bakedBackground = baked;
        _bakedSideDip = bakeSide;
        _bakedBlurRadius = blurRadius;
        if (_backgroundRotationActive)
            BeginBackgroundCrossfade(baked, Math.Max(MainWindow.getDuration(), 1));
    }

    private void BeginBackgroundCrossfade(BitmapSource target, int durationMs)
    {
        if (BackgroundImage.Source == null || !AnimationsEnabled || durationMs <= 1)
        {
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
            return;
        }

        if (ReferenceEquals(BackgroundImage.Source, target) && _backgroundCrossfadeTarget == null)
        {
            ParkBackgroundNextLayer();
            return;
        }

        _backgroundCrossfadeVersion++;
        int version = _backgroundCrossfadeVersion;
        _backgroundCrossfadeTarget = target;
        BackgroundImageNext.BeginAnimation(OpacityProperty, null);
        BackgroundImageNext.Source = target;
        BackgroundImageNext.Visibility = Visibility.Visible;
        BackgroundImageNext.Opacity = 0;
        var fade = new DoubleAnimation
        {
            From = 0,
            To = BackgroundImage.Opacity,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (version != _backgroundCrossfadeVersion) return;
            BackgroundImage.Source = target;
            ParkBackgroundNextLayer();
        };
        BackgroundImageNext.BeginAnimation(OpacityProperty, fade);
    }

    private void ParkBackgroundNextLayer()
    {
        _backgroundCrossfadeTarget = null;
        BackgroundImageNext.BeginAnimation(OpacityProperty, null);
        BackgroundImageNext.Visibility = Visibility.Collapsed;
        BackgroundImageNext.Opacity = Math.Clamp(SettingsManager.Current.IslandBackgroundBlurIntensity, 0, 100) / 100.0;
    }

    private void CancelBackgroundCrossfade()
    {
        _backgroundCrossfadeVersion++;
        ParkBackgroundNextLayer();
    }

    private void UpdateRotationPauseState()
    {
        if (!_backgroundRotationActive) return;
        if (!SettingsManager.Current.IslandBackgroundRotate ||
            !SettingsManager.Current.IslandBackgroundBlur)
            return;

        if (_lastStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ||
            !IsVisible || !IsBoxShown)
            PauseBackgroundRotation();
        else
            ResumeBackgroundRotation();
    }

    private void PauseBackgroundRotation()
    {
        if (!_backgroundRotationAnimationRunning || _backgroundRotationPaused || _backgroundRotateTransform == null)
            return;

        _pausedRotationAngle = _backgroundRotateTransform.Angle;
        _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        _backgroundRotateTransform.Angle = _pausedRotationAngle;
        _backgroundRotationAnimationRunning = false;
        _backgroundRotationPaused = true;
    }

    private void ResumeBackgroundRotation()
    {
        if (!_backgroundRotationPaused) return;
        _backgroundRotationPaused = false;
        ApplyBackgroundRotation(_pausedRotationAngle);
    }

    private void StopBackgroundRotation()
    {
        _backgroundRotationActive = false;
        _backgroundRotationAnimationRunning = false;
        _backgroundRotationPaused = false;
        if (_backgroundRotateTransform != null)
        {
            _backgroundRotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
            _backgroundRotateTransform.Angle = 0;
        }
        CancelBackgroundCrossfade();
        BackgroundImage.CacheMode = null;
        BackgroundImageNext.CacheMode = null;
        BackgroundImage.RenderTransform = Transform.Identity;
        BackgroundImageNext.RenderTransform = Transform.Identity;
        BackgroundImage.Effect = BackgroundImageBlurEffect;
        BackgroundImageNext.Effect = BackgroundImageNextBlurEffect;
    }

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

    private void ApplyIslandClip(double width, double height, CornerRadius radius)
    {
        IslandBox.Clip = CreateIslandClip(width, height, radius);
    }

    private void PositionTopCenter()
    {
        var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
        if (primary.monitorArea.Width == 0) return;
        double rawW = Width * primary.dpiX / 96.0;
        Left = (primary.workArea.Left + primary.workArea.Width / 2 - rawW / 2) * 96.0 / primary.dpiX;
        Top = primary.workArea.Top * 96.0 / primary.dpiY;
        WindowHelper.SetTopmost(this);
    }

    private bool Suppressed()
    {
        if (FullscreenDetector.IsFullscreenApplicationRunning()) return true;
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && NativeMethods.GetWindowRect(fg, out var r))
            {
                var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
                if (primary.monitorArea.Width != 0 && r.Left <= primary.monitorArea.Left && r.Top <= primary.monitorArea.Top && r.Right >= primary.monitorArea.Right && r.Bottom >= primary.monitorArea.Bottom)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) { if (Current() is { } s) await s.ControlSession.TrySkipPreviousAsync(); }
    private async void PlayPause_Click(object sender, RoutedEventArgs e) { if (Current() is { } s) await s.ControlSession.TryTogglePlayPauseAsync(); }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (Current() is { } s) await s.ControlSession.TrySkipNextAsync(); }

    private void AlbumArt_MouseEnter(object sender, MouseEventArgs e)
    {
        _albumArtHovering = true;
        UpdateAlbumArtOverlay();
    }

    private void AlbumArt_MouseLeave(object sender, MouseEventArgs e)
    {
        _albumArtHovering = false;
        UpdateAlbumArtOverlay();
    }

    private void UpdateAlbumArtOverlay()
    {
        bool showChevron = _albumArtHovering && _hasAlbumCover && _main.GetTaskbarSessionCount() > 1;
        var visibility = showChevron ? Visibility.Visible : Visibility.Collapsed;
        CompactAlbumOverlay.Visibility = visibility;
        ExpandedAlbumOverlay.Visibility = visibility;
        CompactArt.Opacity = showChevron ? 0.4 : 1;
        ExpandedArt.Opacity = showChevron ? 0.4 : 1;
    }

    private void Album_Click(object sender, MouseButtonEventArgs e)
    {
        var all = _main.mediaManager.CurrentMediaSessions.Values.Where(s => _main.IsSessionAllowed(s)).ToList();
        if (all.Count <= 1) return;
        int i = all.FindIndex(s => s.Id == _currentId);
        var next = all[(i + 1) % all.Count];
        _currentId = next.Id;
        _hideCts?.Cancel();
        if (_expanded) RefreshUi(next, null, true);
        else ShowCompact(next, null, true);
    }

    private void Seekbar_Down(object sender, MouseButtonEventArgs e) { _drag = true; if (sender is Slider sl) { var p = e.GetPosition(sl); double ratio = sl.ActualWidth > 0 ? Math.Clamp(p.X / sl.ActualWidth, 0, 1) : 0; sl.Value = ratio * sl.Maximum; } }
    private async void Seekbar_Up(object sender, MouseButtonEventArgs e) { try { if (Current() is { } s && sender is Slider sl) { var pos = TimeSpan.FromSeconds(Math.Max(sl.Value, 0)); await s.ControlSession.TryChangePlaybackPositionAsync(pos.Ticks); } } catch { } finally { _drag = false; } }

    public void Dispose()
    {
        _disposed = true;
        _tick.Stop();
        _hoverPoll.Stop();
        _hideCts?.Cancel();
        StopLoop();
        StopBackgroundRotation();
        _eq.Dispose();
        var mm = _main.mediaManager;
        mm.OnAnyPlaybackStateChanged -= OnPlayState;
        mm.OnAnyMediaPropertyChanged -= OnMediaProp;
        mm.OnAnySessionClosed -= OnClosed;
    }
}
