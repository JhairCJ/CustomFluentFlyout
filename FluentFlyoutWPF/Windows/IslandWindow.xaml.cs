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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using static WindowsMediaController.MediaManager;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Fluent Island: isla superior central. Oculta / compacta / expandida.
/// Sigue a la sesión que empezó última; expandido manda sobre compacto.
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

    public IslandWindow(MainWindow main)
    {
        _main = main;
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        CompactEq.Source = _eq.Bitmap;
        ExpandedEq.Source = _eq.Bitmap;
        // Nace invisible (RF-12): solo aparece al pasar a reproduciendo.
        Show(); // sin Show la ventana no existe; se muestra vacía (solo franja hover) hasta el primer play
        Visibility = Visibility.Visible; // visible para cazar hover, pero sin isla ni línea
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
    }

    // --- fuente: la sesión que empezó última (RF-9) ---

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

    // --- eventos ---

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
                if (_expanded) RefreshUi(session, status); // ya expandido: actualizar sin encoger
                else ShowCompact(session, status);
            }
            else if (session.Id == _currentId || NewestPlaying() == null)
            {
                // RF-10: la prioritaria se pausa existiendo otra → pasar a la siguiente.
                // Si nada sigue sonando se oculta aunque el evento venga de otra sesión.
                // El glifo se pinta también al ocultar: si no, quedaba clavado en ⏸.
                _lastStatus = status ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
                PaintGlyph();
                var next = NewestPlaying();
                if (next != null) { _currentId = next.Id; ShowCompact(next); }
                else HidePerMode();
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
            // Cambio de pista/app actualiza sin arrastrar la anterior (lee siempre lo último)
            if (_expanded || CompactBorder.Visibility == Visibility.Visible) RefreshUi(show);
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
            if (next != null) { _currentId = next.Id; ShowCompact(next); }
            else
            {
                _lastStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
                PaintGlyph();
                HidePerMode();
            }
        });
    }

    // --- estados ---

    private void ShowCompact(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null)
    {
        _hideCts?.Cancel();
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) { CollapseAll(); return; }
        RefreshUi(session, knownStatus);
        _expanded = false;
        ExpandedBorder.Visibility = Visibility.Collapsed;
        CompactBorder.Visibility = Visibility.Visible;
        UpdateLine();
        PositionTopCenter();
    }

    private void HidePerMode()
    {
        _hideCts?.Cancel();
        if (_expanded) return; // el expandido manda mientras el puntero está en zona
        if (SettingsManager.Current.IslandVisibilityMode == 1)
        {
            // Aviso temporal: 4 s y oculta (RF-6)
            var cts = _hideCts = new CancellationTokenSource();
            _ = Task.Delay(4000).ContinueWith(_ =>
                Dispatcher.Invoke(() => { if (!cts.IsCancellationRequested) CollapseAll(); }));
        }
        else CollapseAll();
    }

    private void CollapseAll()
    {
        _expanded = false;
        CompactBorder.Visibility = Visibility.Collapsed;
        ExpandedBorder.Visibility = Visibility.Collapsed;
        UpdateLine();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!SettingsManager.Current.IslandEnabled || Suppressed()) return;
        var session = Current() ?? NewestPlaying() ?? FirstAllowed();
        if (session == null) return;
        ExpandSession(session);
    }

    // ponytail: la franja se sondea en pantalla (píxeles físicos); el MouseEnter de
    // una ventana transparente topmost falla según z-order y esa era la tasa de fallos
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
        _currentId = session.Id;
        RefreshUi(session);
        _expanded = true; // RF-3: el expandido manda sobre el compacto
        CompactBorder.Visibility = Visibility.Collapsed;
        ExpandedBorder.Visibility = Visibility.Visible;
        UpdateLine();
        PositionTopCenter();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_drag || Mouse.LeftButton == MouseButtonState.Pressed) return; // interactuando (seek): no replegar
        LeaveHover();
    }

    private void LeaveHover()
    {
        // RF-4: repliegue inmediato
        if (!_expanded) return;
        _expanded = false;
        ExpandedBorder.Visibility = Visibility.Collapsed;
        var session = Current();
        var playing = session?.ControlSession?.GetPlaybackInfo()?.PlaybackStatus
            == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (playing && !Suppressed())
        {
            CompactBorder.Visibility = Visibility.Visible;
            UpdateLine();
            PositionTopCenter();
        }
        else HidePerMode();
    }

    // --- presentación ---

    private void RefreshUi(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackStatus? knownStatus = null)
    {
        // Glifo 100% event-sourced como el widget: manda el estado del evento;
        // la reconsulta solo vale en arranque en frío (_lastStatus nulo), porque
        // GSMTC tarda en asentarse y pisaba el valor fresco con uno rancio.
        if (knownStatus != null) _lastStatus = knownStatus;
        else if (_lastStatus == null) _lastStatus = SafeStatus(session);
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
        catch { /* RF-7: sin mensajes técnicos, placeholders */ }
        SongTitle.Text = title;
        SongArtist.Text = artist;
        CompactTitle.Text = title;
        CompactArt.Source = art;
        ExpandedArt.Source = art;
        bool hasArt = art != null;
        CompactNote.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;
        ExpandedNote.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;
        UpdateSeek(session);
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
                bool playing = session.ControlSession.GetPlaybackInfo()?.PlaybackStatus
                    == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var pos = playing
                    ? tl.Position + (DateTime.Now - tl.LastUpdatedTime.DateTime)
                    : tl.Position; // en pausa: congelada, sin extrapolar con el reloj
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (pos > tl.EndTime) pos = tl.EndTime;
                Seekbar.Maximum = tl.MaxSeekTime.TotalSeconds;
                if (!_drag)
                {
                    Seekbar.Value = pos.TotalSeconds;
                    PosText.Text = Fmt(pos);
                }
                DurText.Text = Fmt(tl.MaxSeekTime);
                return;
            }
        }
        catch { }
        Seekbar.Maximum = 100; Seekbar.Value = 0; PosText.Text = "0:00"; DurText.Text = "0:00";
    }

    private void Tick()
    {
        // ponytail: el toggle se aplica por sondeo (500 ms), sin eventos; basta para un on/off
        if (!SettingsManager.Current.IslandEnabled)
        {
            CollapseAll();
            Visibility = Visibility.Collapsed;
            return;
        }
        if (Suppressed())
        {
            if (ExpandedBorder.Visibility == Visibility.Visible || CompactBorder.Visibility == Visibility.Visible)
                CollapseAll();
            Visibility = Visibility.Collapsed; // en inmersivo ni franja hover
            return;
        }
        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        // ponytail: autocura por sondeo; si un MouseLeave se perdió, _expanded se
        // quedaba atascado y el aviso temporal jamás se armaba
        if (_expanded && !_drag && !WindowHelper.IsMouseOverWindow(this)) LeaveHover();
        SyncEq();
        var s = Current();
        if (s != null && _expanded) UpdateSeek(s);
        UpdateLine();
    }

    // Ecualizador con ajustes propios: corre mientras la isla está habilitada;
    // el nº de barras se aplica por sondeo, el resto (sensibilidad, suavizado)
    // lo lee el motor en cada frame sin eventos.
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
        // La línea es la que descubre la franja invisible: sale siempre que el
        // hover reviviría el island (misma condición que Window_MouseEnter).
        bool alive = CompactBorder.Visibility == Visibility.Visible
            || ExpandedBorder.Visibility == Visibility.Visible
            || Current() != null || NewestPlaying() != null || FirstAllowed() != null;
        ActivityLine.Visibility = SettingsManager.Current.IslandActivityLine && alive
            ? Visibility.Visible : Visibility.Collapsed;
        var eqVis = SettingsManager.Current.IslandEqEnabled ? Visibility.Visible : Visibility.Collapsed;
        CompactEq.Visibility = eqVis;
        ExpandedEq.Visibility = eqVis;
    }

    private int _appliedStyle = -1;

    // Estilo notch: esquinas superiores cuadradas fundidas con el borde de la
    // pantalla, sin borde arriba y compacto más estrecho (como la referencia).
    private void ApplyStyle()
    {
        int style = Math.Clamp(SettingsManager.Current.IslandStyle, 0, 1);
        if (style == _appliedStyle) return;
        _appliedStyle = style;
        if (style == 1)
        {
            CompactBorder.Width = 200;
            CompactBorder.CornerRadius = new CornerRadius(0, 0, 15, 15);
            CompactBorder.BorderThickness = new Thickness(1, 0, 1, 1);
            ExpandedBorder.CornerRadius = new CornerRadius(0, 0, 20, 20);
            ExpandedBorder.BorderThickness = new Thickness(1, 0, 1, 1);
        }
        else
        {
            CompactBorder.Width = 240;
            CompactBorder.CornerRadius = new CornerRadius(17);
            CompactBorder.BorderThickness = new Thickness(1);
            ExpandedBorder.CornerRadius = new CornerRadius(18);
            ExpandedBorder.BorderThickness = new Thickness(1);
        }
    }

    private void PositionTopCenter()
    {
        var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
        if (primary.monitorArea.Width == 0) return;
        // workArea viene en píxeles físicos; Left/Top van en DIPs (igual que MainWindow).
        double rawW = (ActualWidth > 0 ? ActualWidth : Width) * primary.dpiX / 96.0;
        Left = (primary.workArea.Left + primary.workArea.Width / 2 - rawW / 2) * 96.0 / primary.dpiX;
        Top = primary.workArea.Top * 96.0 / primary.dpiY;
        WindowHelper.SetTopmost(this);
    }

    // RF-11: oculta en pantalla completa o juego, solo monitor principal
    private bool Suppressed()
    {
        if (FullscreenDetector.IsFullscreenApplicationRunning()) return true;
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && NativeMethods.GetWindowRect(fg, out var r))
            {
                var primary = MonitorUtil.GetMonitors().FirstOrDefault(m => m.isPrimary);
                if (primary.monitorArea.Width != 0 &&
                    r.Left <= primary.monitorArea.Left && r.Top <= primary.monitorArea.Top &&
                    r.Right >= primary.monitorArea.Right && r.Bottom >= primary.monitorArea.Bottom)
                    return true;
            }
        }
        catch { }
        return false;
    }

    // --- controles (RF-5) sobre la sesión más reciente ---

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (Current() is { } s) await s.ControlSession.TrySkipPreviousAsync();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        // Sin refresco ciego: el evento de cambio de estado actualiza el glifo.
        // El refresco a 250 ms pisaba estados en transición y lo dejaba clavado.
        if (Current() is { } s) await s.ControlSession.TryTogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (Current() is { } s) await s.ControlSession.TrySkipNextAsync();
    }

    private void Seekbar_Down(object sender, MouseButtonEventArgs e)
    {
        _drag = true;
        if (sender is Slider sl)
        {
            var p = e.GetPosition(sl);
            double ratio = sl.ActualWidth > 0 ? Math.Clamp(p.X / sl.ActualWidth, 0, 1) : 0;
            sl.Value = ratio * sl.Maximum;
        }
    }

    private async void Seekbar_Up(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (Current() is { } s && sender is Slider sl)
            {
                var pos = TimeSpan.FromSeconds(Math.Max(sl.Value, 1));
                await s.ControlSession.TryChangePlaybackPositionAsync(pos.Ticks);
            }
        }
        catch { }
        finally { _drag = false; }
    }

    public void Dispose()
    {
        _tick.Stop();
        _hoverPoll.Stop();
        _hideCts?.Cancel();
        _eq.Dispose();
        var mm = _main.mediaManager;
        mm.OnAnyPlaybackStateChanged -= OnPlayState;
        mm.OnAnyMediaPropertyChanged -= OnMediaProp;
        mm.OnAnySessionClosed -= OnClosed;
    }
}
