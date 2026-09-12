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
    private bool _hidingViaCompact; // expandido va a desaparecer: primero p->0, luego q->0
    private bool _fastHideQ; // ponytail: 2x q solo en compacto→círculo encadenado desde expandido
    private double _hexp = 172;
    private string _lastTrackKey = "";
    private int _popVersion;
    private bool _popPlaying;
    private double _pop; // 0..1 pulso de cambio de pista

    public IslandWindow(MainWindow main)
    {
        _main = main;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        CompactEq.Source = _eq.Bitmap;
        ExpandedEq.Source = _eq.Bitmap;
        ApplyStyle();
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
                if (_expanded) RefreshUi(session, status);
                else ShowCompact(session, status);
            }
            else if (session.Id == _currentId || NewestPlaying() == null)
            {
                var next = NewestPlaying();
                if (next != null)
                {
                    _currentId = next.Id;
                    if (_expanded) RefreshUi(next);
                    else ShowCompact(next);
                }
                else
                {
                    _lastStatus = status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
                    PaintGlyph();
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
            else ShowCompact(show);
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

    private void ShowCompact(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null)
    {
        _hideCts?.Cancel();
        _fastHideQ = false;
        _hidingViaCompact = false;
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) { SnapHidden(); return; }
        RefreshUi(session, knownStatus);
        _expanded = false;
        UpdateLine();
        PositionTopCenter();
        SyncMeasuredHeight();
        if (!AnimationsEnabled) SnapCompact();
        else { _pT = 0; _qT = 1; IslandBox.Visibility = Visibility.Visible; EnsureLoop(); }
    }

    private void HidePerMode()
    {
        _hideCts?.Cancel();
        if (_expanded)
        {
            if (SettingsManager.Current.IslandVisibilityMode == 1)
            {
                var cts = _hideCts = new CancellationTokenSource();
                _ = Task.Delay(4000).ContinueWith(_ =>
                    Dispatcher.Invoke(() => { if (cts.IsCancellationRequested) return; _expanded = false; GoHidden(); }));
                return;
            }
            _expanded = false;
            if (!IsNotch && AnimationsEnabled) { _hidingViaCompact = true; _fastHideQ = true; _pT = 0; _qT = 1; EnsureLoop(); return; }
            GoHidden(); return;
        }
        if (!IsNotch)
        {
            // compacto pill: 240→26
            if (SettingsManager.Current.IslandVisibilityMode == 1)
            {
                var cts = _hideCts = new CancellationTokenSource();
                _ = Task.Delay(4000).ContinueWith(_ =>
                    Dispatcher.Invoke(() => { if (!cts.IsCancellationRequested && !_expanded) GoHidden(); }));
                return;
            }
            GoHidden(); return;
        }
        if (SettingsManager.Current.IslandVisibilityMode == 1)
        {
            var cts = _hideCts = new CancellationTokenSource();
            _ = Task.Delay(4000).ContinueWith(_ =>
                Dispatcher.Invoke(() => { if (!cts.IsCancellationRequested && !_expanded) GoHidden(); }));
        }
        else GoHidden();
    }

    private void CollapseAll() => SnapHidden();

    private void GoHidden()
    {
        if (!AnimationsEnabled || !IsBoxShown) { SnapHidden(); return; }
        if (_hidingViaCompact) return;
        if (!IsNotch)
        {
            if (Math.Abs(_p) > 0.05) { _expanded = false; _hidingViaCompact = true; _fastHideQ = true; _pT = 0; _qT = 1; EnsureLoop(); return; }
            _qT = 0; EnsureLoop(); return;
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
    }

    private void SnapHidden()
    {
        _fastHideQ = false;
        _hidingViaCompact = false;
        _p = _pT = 0; _pv = 0;
        _q = _qT = 0; _qv = 0;
        _pop = 0; _popVersion++; _popPlaying = false;
        StopLoop();
        ApplyFrame();
        IslandBox.Visibility = Visibility.Collapsed;
        UpdateLine();
    }

    private void Box_MouseEnter(object sender, MouseEventArgs e)
    {
        _fastHideQ = false;
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
        double halfRaw = 240 * primary.dpiX / 96.0 + tol;
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
        _fastHideQ = false;
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
            return;
        }
        if (wasExpanded) return; // ya expandido: solo actualizar datos
        _pT = 1; _qT = 1;
        IslandBox.Visibility = Visibility.Visible;
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

    // Apple-ish: muelle subamortiguado suave. Si el usuario puso velocidad lenta,
    // bajamos rigidez para que se sienta más pesado sin romper.
    private void GetSpring(out double kP, out double cP, out double kQ, out double cQ)
    {
        int sp = SettingsManager.Current.FlyoutAnimationSpeed; // 0..5
        double slow = sp switch { 3 => 0.85, 4 => 0.7, 5 => 0.55, _ => 1.0 };
        kP = 520 * slow; cP = 34;
        kQ = 620 * slow; cQ = 36;
        if (IsNotch) { kP *= 1.05; kQ *= 1.05; }
        else { kQ = 200 * slow; cQ = 28; } // pill 2x más lento (solo q)
        if (!IsNotch && _fastHideQ && _qT == 0 && _q > 0.02) { kQ *= 9; cQ *= 2.9; }
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

        // Expandido→oculto (ponytail: 0.45 encadena q sin dwell, compacto→círculo queda en GoHidden)
        if (_hidingViaCompact && !IsNotch && _p < 0.45)
        {
            _hidingViaCompact = false;
            _qT = 0;
            ApplyFrame();
            return;
        }

        bool pSettled = Math.Abs(_p - _pT) < 0.002 && Math.Abs(_pv) < 0.02;
        bool qSettled = Math.Abs(_q - _qT) < 0.002 && Math.Abs(_qv) < 0.02;
        if (pSettled) { _p = _pT; _pv = 0; }
        if (qSettled) { _q = _qT; _qv = 0; if (_fastHideQ && _qT == 0) _fastHideQ = false; }
        bool popSettled = !_popPlaying;

        if (pSettled && qSettled && popSettled)
        {
            StopLoop();
            _lastTick = TimeSpan.Zero;
            if (_qT == 0 && _q == 0) { IslandBox.Visibility = Visibility.Collapsed; UpdateLine(); }
            // Si llegamos a compacto vía hidingViaCompact y no hay q pendiente, ya se ocultó arriba
        }
        else if (_qT == 0 && _q < 0.02 && qSettled)
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
            // Medir la altura expandida real con el ancho objetivo (480).
            // ExpandedLayer está siempre en el árbol (Opacity 0 cuando compacto),
            // así que es medible.
            ExpandedLayer.Measure(new Size(480, double.PositiveInfinity));
            double h = ExpandedLayer.DesiredSize.Height + 24; // padding vertical del box
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

        bool notch = IsNotch;
        double w, h;
        if (notch)
        {
            double compactW = 200;
            w = Lerp(compactW, 480, Smooth01(p));
            h = Lerp(34, _hexp, Smooth01(p));
            IslandBox.Width = w;
            IslandBox.Height = h;
            IslandBox.Opacity = q;
            IslandBox.CornerRadius = new CornerRadius(0, 0, 18, 18);
            BoxTranslate.Y = (1 - q) * -18;
            BoxScale.ScaleX = BoxScale.ScaleY = 1;
            IslandBox.RenderTransformOrigin = new Point(0.5, 0);
        }
        else
        {
            // Pill: oculto -> punto 26px (circular) -> cápsula 240px -> expandido 480px
            const double pillDot = 26;
            double dotT = Math.Clamp(q / 0.32, 0, 1);
            double stretchT = Smooth01(Math.Clamp((q - 0.18) / 0.82, 0, 1));
            double baseW = q < 0.32 ? pillDot : Lerp(pillDot, 240, stretchT);
            w = Lerp(baseW, 480, Smooth01(p));
            h = Lerp(34, _hexp, Smooth01(p));
            IslandBox.Width = w;
            IslandBox.Height = h;
            IslandBox.Opacity = Smooth01(Math.Clamp(q / 0.38, 0, 1));
            // Radio: círculo perfecto mientras es punto, cápsula después
            double cr = baseW <= pillDot + 0.5 && p < 0.02 ? pillDot / 2 : 17;
            if (p > 0.02) cr = 17; // expandido siempre pill
            IslandBox.CornerRadius = new CornerRadius(cr);
            BoxTranslate.Y = 0;
            BoxScale.ScaleX = BoxScale.ScaleY = Lerp(0.68, 1, Smooth01(dotT));
            IslandBox.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        // Crossfade de capas + morph del contenido (Apple: el álbum y el título respiran)
        // En pill, los elementos divergen desde el centro durante el estiramiento
        double compactOp, expandedOp;
        if (notch)
        {
            compactOp = 1 - Smooth01(Math.Clamp(p * 2.2, 0, 1));
            expandedOp = Smooth01(Math.Clamp((p - 0.12) / 0.88, 0, 1));
            CompactLayer.Opacity = compactOp * (0.7 + 0.3 * q);
            // Reset diverge translates when in notch so no residue
            CompactArtTranslate.X = 0;
            CompactTitleTranslate.X = 0;
            CompactTitleScale2.ScaleX = CompactTitleScale2.ScaleY = 1;
            CompactEqTranslate.X = 0;
            CompactTitle.Opacity = 1;
            CompactEq.Opacity = 1;
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

        if (notch)
            CompactScale.ScaleX = CompactScale.ScaleY = Lerp(1, 0.92, Smooth01(p));
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

    private void RefreshUi(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null)
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
        CompactArt.Source = art;
        ExpandedArt.Source = art;
        string trackKey = title + "\n" + artist + "\n" + (art != null);
        bool trackChanged = _lastTrackKey != "" && trackKey != _lastTrackKey;
        _lastTrackKey = trackKey;
        if (trackChanged) PlayTrackPop();
        BitmapHelper.GetDominantColors();
        bool hasArt = art != null;
        CompactNote.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;
        ExpandedNote.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;
        ApplyCapabilities(session);
        UpdateSeek(session);
        SyncMeasuredHeight();
        if (_expanded || _p > 0.05) ApplyFrame();
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
                DurText.Text = Fmt(tl.MaxSeekTime);
                return;
            }
        }
        catch { }
        Seekbar.Maximum = 100; Seekbar.Value = 0; PosText.Text = "0:00"; DurText.Text = "0:00";
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
            // En expandido la isla es de 480px de ancho
            if (_expanded || _p > 0.2) halfW = 240;
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
    }

    private int _appliedStyle = -1;

    private void ApplyStyle()
    {
        int style = Math.Clamp(SettingsManager.Current.IslandStyle, 0, 1);
        if (style == _appliedStyle) return;
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
        // CornerRadius lo gobierna ApplyFrame por frame (punto 26→cápsula)
        SyncMeasuredHeight();
        if (!_loopOn) ApplyFrame();
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

    private void Album_Click(object sender, MouseButtonEventArgs e)
    {
        var all = _main.mediaManager.CurrentMediaSessions.Values.Where(s => _main.IsSessionAllowed(s)).ToList();
        if (all.Count <= 1) return;
        int i = all.FindIndex(s => s.Id == _currentId);
        var next = all[(i + 1) % all.Count];
        _currentId = next.Id;
        _hideCts?.Cancel();
        if (_expanded) RefreshUi(next);
        else ShowCompact(next);
    }

    private void Seekbar_Down(object sender, MouseButtonEventArgs e) { _drag = true; if (sender is Slider sl) { var p = e.GetPosition(sl); double ratio = sl.ActualWidth > 0 ? Math.Clamp(p.X / sl.ActualWidth, 0, 1) : 0; sl.Value = ratio * sl.Maximum; } }
    private async void Seekbar_Up(object sender, MouseButtonEventArgs e) { try { if (Current() is { } s && sender is Slider sl) { var pos = TimeSpan.FromSeconds(Math.Max(sl.Value, 1)); await s.ControlSession.TryChangePlaybackPositionAsync(pos.Ticks); } } catch { } finally { _drag = false; } }

    public void Dispose()
    {
        _tick.Stop();
        _hoverPoll.Stop();
        _hideCts?.Cancel();
        StopLoop();
        _eq.Dispose();
        var mm = _main.mediaManager;
        mm.OnAnyPlaybackStateChanged -= OnPlayState;
        mm.OnAnyMediaPropertyChanged -= OnMediaProp;
        mm.OnAnySessionClosed -= OnClosed;
    }
}
