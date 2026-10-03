// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Controls.TaskbarWidget;
using FluentFlyoutWPF;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Wpf.Ui.Controls;

namespace FluentFlyout.Controls;

/// <summary>Media controls hosted inside the widget's existing surface. No window or background.</summary>
public partial class TaskbarWidgetExpandedContent : UserControl
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private MainWindow? _mainWindow;
    private readonly System.Threading.Timer _progressTimer;
    private volatile bool _active, _disposed;
    private bool _drag, _albumArtHovering, _isPaused = true;
    private int _progressRefreshPending, _progressVersion;
    private int _titleVersion, _artistVersion;
    private string _titleTarget = string.Empty, _artistTarget = string.Empty;
    private GlobalSystemMediaTransportControlsSession? _seekSession, _observedSession, _publishedSession;
    private int _progressRefreshAgain, _albumFlipVersion;
    private bool _albumFlipRunning;
    private BitmapImage? _albumFlipArt, _displayedArt;
    private readonly SymbolIcon _playIcon = new(SymbolRegular.Play24, filled: true) { FontSize = 22 };
    private readonly SymbolIcon _pauseIcon = new(SymbolRegular.Pause24, filled: true) { FontSize = 22 };
    public Action<bool>? TrackNavigation { get; set; }
    public Action? ToggleExpansion { get; set; }

    public TaskbarWidgetExpandedContent()
    {
        InitializeComponent();
        ApplyButtonStyle();
        _progressTimer = new System.Threading.Timer(ProgressTick, null, Timeout.Infinite, Timeout.Infinite);
        Unloaded += (_, _) => SetActive(false);
    }

    public void ApplyButtonStyle()
    {
        var radius = new CornerRadius(SettingsManager.Current.TaskbarWidgetButtonHoverRadius);
        PreviousButton.CornerRadius = radius;
        PlayPauseButton.CornerRadius = radius;
        NextButton.CornerRadius = radius;
    }

    // Layout endpoints used by the host's shared spring. These transforms belong
    // to expansion; text-row translations inside them still belong to song changes.
    public FrameworkElement[] MorphElements => [AlbumArtBorder, TitleMorph, ArtistMorph,
        PreviousButton, PlayPauseButton, NextButton];

    public void ApplyTextStyle(System.Windows.Controls.TextBlock title, System.Windows.Controls.TextBlock artist)
    {
        TitleText.FontFamily = title.FontFamily;
        TitleText.FontWeight = title.FontWeight;
        ArtistText.FontFamily = artist.FontFamily;
        ArtistText.FontWeight = artist.FontWeight;
        ArtistText.FontStyle = artist.FontStyle;
        ArtistText.Opacity = artist.Opacity;
    }

    public void SetMorphProgress(double progress)
    {
        SeekRow.Opacity = Math.Clamp((progress - 0.35) / 0.65, 0, 1);
    }

    public void SetMainWindow(MainWindow mainWindow) => _mainWindow = mainWindow;

    public void PublishSong(string title, string artist, BitmapImage? art, bool backwards = false)
    {
        var session = _mainWindow?.GetTaskbarSession()?.ControlSession;
        bool flip = _active && !string.IsNullOrEmpty(_titleTarget)
            && (_titleTarget != title || _artistTarget != artist || !ReferenceEquals(session, _publishedSession));
        _publishedSession = session;
        if (_titleTarget != title)
        {
            _titleTarget = title;
            int version = ++_titleVersion;
            UpdateTextRow(TitleText, title, backwards, () => version == _titleVersion);
        }
        if (_artistTarget != artist)
        {
            _artistTarget = artist;
            int version = ++_artistVersion;
            UpdateTextRow(ArtistText, artist, backwards, () => version == _artistVersion);
        }
        TitleText.ToolTip = title;
        ArtistText.ToolTip = artist;
        if (flip || _albumFlipRunning) StartAlbumFlip(art);
        else SetAlbumArt(art);
        Interlocked.Increment(ref _progressVersion);
        if (_active)
        {
            RefreshObservedSession();
            RequestProgressRefresh();
        }
    }

    private void UpdateTextRow(System.Windows.Controls.TextBlock row, string text, bool backwards, Func<bool> current)
    {
        var transform = (TranslateTransform)row.RenderTransform;
        double from = transform.X;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = from;
        if (!_active || !TaskbarWidgetAnimationEnvironment.AreAnimationsEnabled
            || SettingsManager.Current.TaskbarWidgetSongChangeAnimation != 1 || string.IsNullOrEmpty(row.Text))
        {
            row.Text = text;
            transform.X = 0;
            return;
        }
        var half = TimeSpan.FromMilliseconds(TaskbarWidgetAnimationEnvironment.GetDurationMs() / 2.0);
        double distance = row.ActualWidth + 8;
        var exit = new DoubleAnimation(from, backwards ? distance : -distance, half)
        { EasingFunction = TaskbarWidgetAnimationEnvironment.GetEasing(_mainWindow, true) };
        exit.Completed += (_, _) =>
        {
            if (!current()) return;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = backwards ? -distance : distance;
            row.Text = text;
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(transform.X, 0, half)
            { EasingFunction = TaskbarWidgetAnimationEnvironment.GetEasing(_mainWindow, true) });
        };
        transform.BeginAnimation(TranslateTransform.XProperty, exit);
    }

    public void ApplyPlaybackState(bool paused, GlobalSystemMediaTransportControlsSessionPlaybackControls? controls)
    {
        _isPaused = paused;
        PlayPauseButton.Icon = paused ? _playIcon : _pauseIcon;
        PreviousButton.IsEnabled = controls?.IsPreviousEnabled == true;
        PlayPauseButton.IsEnabled = controls?.IsPlayEnabled == true || controls?.IsPauseEnabled == true;
        NextButton.IsEnabled = controls?.IsNextEnabled == true;
        if (_active) RefreshObservedSession();
    }

    public void SetActive(bool active)
    {
        if (_disposed) return;
        _active = active;
        Interlocked.Increment(ref _progressVersion);
        _progressTimer.Change(active ? 0 : Timeout.Infinite, active ? 100 : Timeout.Infinite);
        RefreshObservedSession();
        if (active) RequestProgressRefresh();
        if (!active)
        {
            SetAlbumArt(_albumFlipArt ?? _displayedArt);
            ++_titleVersion;
            ++_artistVersion;
            UpdateTextRow(TitleText, _titleTarget, false, () => true);
            UpdateTextRow(ArtistText, _artistTarget, false, () => true);
            _drag = false;
            _seekSession = null;
            Seekbar.ReleaseMouseCapture();
        }
    }

    public void DisposeResources()
    {
        if (_disposed) return;
        SetActive(false);
        _disposed = true;
        _progressTimer.Dispose();
    }

    private void SongInfo_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleExpansion?.Invoke();
    }

    private void UpdateAlbumOverlay()
    {
        bool hasArt = AlbumArt.ImageSource != null;
        bool chevron = _albumArtHovering && _mainWindow?.GetTaskbarSessionCount() > 1;
        AlbumChevron.Visibility = chevron ? Visibility.Visible : Visibility.Collapsed;
        AlbumPlaceholder.Visibility = hasArt ? Visibility.Collapsed : Visibility.Visible;
        AlbumArt.Opacity = chevron ? 0.4 : 1;
    }

    private void RefreshObservedSession()
    {
        var current = _active ? _mainWindow?.GetTaskbarSession()?.ControlSession : null;
        if (ReferenceEquals(current, _observedSession)) return;
        if (_observedSession != null)
        {
            try
            {
                _observedSession.TimelinePropertiesChanged -= SessionStateChanged;
                _observedSession.PlaybackInfoChanged -= SessionStateChanged;
            }
            catch (Exception ex) { Logger.Debug(ex, "Taskbar session ended during event cleanup"); }
        }
        _observedSession = current;
        Interlocked.Increment(ref _progressVersion);
        if (current != null)
        {
            try
            {
                current.TimelinePropertiesChanged += SessionStateChanged;
                current.PlaybackInfoChanged += SessionStateChanged;
            }
            catch (Exception ex) { Logger.Debug(ex, "Taskbar session notifications unavailable; polling remains active"); }
            RequestProgressRefresh();
        }
    }

    private void SessionStateChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        if (_active && ReferenceEquals(sender, _observedSession)) RequestProgressRefresh();
    }

    private void RequestProgressRefresh()
    {
        if (!_active || _disposed) return;
        _ = Task.Run(() => ProgressTick(null));
    }

    private void ProgressTick(object? state)
    {
        if (_disposed || !_active) return;
        if (Interlocked.Exchange(ref _progressRefreshPending, 1) == 1)
        {
            Interlocked.Exchange(ref _progressRefreshAgain, 1);
            return;
        }
        int version = Volatile.Read(ref _progressVersion);
        double minimumSeconds = 0, positionSeconds = 0, maximumSeconds = 0;
        bool seekable = false, playing = false;
        GlobalSystemMediaTransportControlsSessionPlaybackControls? controls = null;
        GlobalSystemMediaTransportControlsSession? session = null;
        try
        {
            session = _mainWindow?.GetTaskbarSession()?.ControlSession;
            if (session != null)
            {
                var playback = session.GetPlaybackInfo();
                controls = playback?.Controls;
                playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var timeline = session.GetTimelineProperties();
                if (timeline.MaxSeekTime > timeline.MinSeekTime)
                {
                    var position = playing
                        ? timeline.Position + (DateTimeOffset.UtcNow - timeline.LastUpdatedTime)
                        : timeline.Position;
                    minimumSeconds = timeline.MinSeekTime.TotalSeconds;
                    maximumSeconds = timeline.MaxSeekTime.TotalSeconds;
                    positionSeconds = Math.Clamp(position.TotalSeconds, minimumSeconds, maximumSeconds);
                    seekable = controls?.IsPlaybackPositionEnabled == true;
                }
            }
        }
        catch { /* A disappearing session is retried by the next notification/tick. */ }
        if (Dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref _progressRefreshPending, 0);
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_disposed || !_active || version != Volatile.Read(ref _progressVersion)) return;
                RefreshObservedSession();
                if (version != Volatile.Read(ref _progressVersion) || !ReferenceEquals(session, _observedSession)) return;
                if (_drag && Mouse.LeftButton == MouseButtonState.Released) _drag = false;
                ApplyPlaybackState(!playing, controls);
                ApplyProgress(minimumSeconds, positionSeconds, maximumSeconds, seekable);
            }
            finally
            {
                Interlocked.Exchange(ref _progressRefreshPending, 0);
                // Never lose a playback/timeline notification behind an older read.
                if (Interlocked.Exchange(ref _progressRefreshAgain, 0) == 1) RequestProgressRefresh();
            }
        });
    }

    private void ApplyAlbumArt(BitmapImage? art)
    {
        _displayedArt = art;
        AlbumArt.ImageSource = art;
        UpdateAlbumOverlay();
    }

    private void SetAlbumArt(BitmapImage? art)
    {
        ++_albumFlipVersion;
        _albumFlipRunning = false;
        _albumFlipArt = art;
        AlbumFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AlbumFlipScale.ScaleX = 1;
        ApplyAlbumArt(art);
    }

    private void StartAlbumFlip(BitmapImage? art)
    {
        _albumFlipArt = art;
        if (!TaskbarWidgetAnimationEnvironment.AreAnimationsEnabled || !_active)
        {
            SetAlbumArt(art);
            return;
        }
        if (_albumFlipRunning) return;
        _albumFlipRunning = true;
        int version = _albumFlipVersion;
        double halfMs = Math.Clamp(TaskbarWidgetAnimationEnvironment.GetDurationMs() * 0.35, 90, 200);
        var outgoing = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(halfMs))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        outgoing.Completed += (_, _) =>
        {
            if (version != _albumFlipVersion) return;
            ApplyAlbumArt(_albumFlipArt);
            var incoming = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(halfMs))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            incoming.Completed += (_, _) =>
            {
                if (version != _albumFlipVersion) return;
                AlbumFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                AlbumFlipScale.ScaleX = 1;
                _albumFlipRunning = false;
                if (!ReferenceEquals(_albumFlipArt, _displayedArt)) StartAlbumFlip(_albumFlipArt);
            };
            AlbumFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, incoming);
        };
        AlbumFlipScale.BeginAnimation(ScaleTransform.ScaleXProperty, outgoing);
    }

    private void ApplyProgress(double minimumSeconds, double positionSeconds, double maximumSeconds, bool seekable)
    {
        SeekRow.Visibility = maximumSeconds > minimumSeconds ? Visibility.Visible : Visibility.Hidden;
        if (maximumSeconds <= minimumSeconds) return;

        if (_drag) return;
        Seekbar.Minimum = 0;
        Seekbar.Maximum = maximumSeconds;
        Seekbar.Minimum = minimumSeconds;
        Seekbar.IsEnabled = seekable;
        Seekbar.Value = positionSeconds;
        PosText.Text = Fmt(TimeSpan.FromSeconds(positionSeconds));

        DurText.Text = FmtRemaining(TimeSpan.FromSeconds(maximumSeconds - positionSeconds));
        UpdateTimelineVisual();
    }

    private static string Fmt(TimeSpan t) => t.ToString(t.Hours > 0 ? @"h\:mm\:ss" : @"m\:ss");

    private static string FmtRemaining(TimeSpan t) => t.TotalSeconds < 1 ? "0:00" : "-" + Fmt(t);

    private void UpdateTimelineVisual()
    {
        double range = Seekbar.Maximum - Seekbar.Minimum;
        double ratio = range > 0 ? Math.Clamp((Seekbar.Value - Seekbar.Minimum) / range, 0, 1) : 0;
        TimelineProgress.Width = TimelineTrack.ActualWidth * ratio;
    }

    private void TimelineHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineVisual();

    private void Seekbar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTimelineVisual();
        if (!_drag || Seekbar.Maximum <= 0)
            return;

        var position = TimeSpan.FromSeconds(Math.Clamp(Seekbar.Value, 0, Seekbar.Maximum));
        PosText.Text = Fmt(position);
        DurText.Text = FmtRemaining(TimeSpan.FromSeconds(Seekbar.Maximum) - position);
    }

    private void Seekbar_Down(object sender, MouseButtonEventArgs e)
    {
        if (!Seekbar.IsEnabled || _mainWindow?.GetTaskbarSession() is not { } session) return;
        _seekSession = session.ControlSession;
        _drag = true;
        Seekbar.CaptureMouse();
        SetSeekPosition(e);
        e.Handled = true;
    }

    private void Seekbar_Move(object sender, MouseEventArgs e)
    {
        if (!_drag) return;
        SetSeekPosition(e);
        e.Handled = true;
    }

    private void SetSeekPosition(MouseEventArgs e)
    {
        double ratio = Seekbar.ActualWidth > 0
            ? Math.Clamp(e.GetPosition(Seekbar).X / Seekbar.ActualWidth, 0, 1) : 0;
        Seekbar.Value = Seekbar.Minimum + ratio * (Seekbar.Maximum - Seekbar.Minimum);
    }

    private async void Seekbar_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_drag) return;
        var session = _seekSession;
        long ticks = TimeSpan.FromSeconds(Seekbar.Value).Ticks;
        _drag = false;
        _seekSession = null;
        Seekbar.ReleaseMouseCapture();
        e.Handled = true;
        try
        {
            // Never seek a different player if the selected session changed mid-drag.
            if (session != null && ReferenceEquals(session, _mainWindow?.GetTaskbarSession()?.ControlSession))
            {
                _mainWindow!.PinTaskbarSession(_mainWindow.GetTaskbarSession()!);
                await session.TryChangePlaybackPositionAsync(ticks);
                RequestProgressRefresh();
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "Taskbar widget seek failed"); }
    }

    private void Seekbar_LostCapture(object sender, MouseEventArgs e)
    {
        _drag = false;
        _seekSession = null;
    }

    // ------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        var session = _mainWindow?.GetTaskbarSession();
        if (session == null) return;
        try
        {
            _mainWindow!.PinTaskbarSession(session);
            TrackNavigation?.Invoke(false);
            await session.ControlSession.TrySkipPreviousAsync();
                RequestProgressRefresh();
        }
        catch (Exception ex) { Logger.Debug(ex, "Taskbar widget transport failed"); }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var session = _mainWindow?.GetTaskbarSession();
        if (session == null) return;
        try
        {
            _mainWindow!.PinTaskbarSession(session);
            await session.ControlSession.TryTogglePlayPauseAsync();
                RequestProgressRefresh();
        }
        catch (Exception ex) { Logger.Debug(ex, "Taskbar widget transport failed"); }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        var session = _mainWindow?.GetTaskbarSession();
        if (session == null) return;
        try
        {
            _mainWindow!.PinTaskbarSession(session);
            TrackNavigation?.Invoke(true);
            await session.ControlSession.TrySkipNextAsync();
                RequestProgressRefresh();
        }
        catch (Exception ex) { Logger.Debug(ex, "Taskbar widget transport failed"); }
    }

    private void Album_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _mainWindow?.CycleTaskbarSession();
        RefreshObservedSession();
        RequestProgressRefresh();
    }

    private void AlbumArt_MouseEnter(object sender, MouseEventArgs e)
    {
        _albumArtHovering = true;
        UpdateAlbumOverlay();
    }

    private void AlbumArt_MouseLeave(object sender, MouseEventArgs e)
    {
        _albumArtHovering = false;
        UpdateAlbumOverlay();
    }

}
