// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls.TaskbarWidget;
using FluentFlyoutWPF;
using FluentFlyoutWPF.Classes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Wpf.Ui.Controls;

namespace FluentFlyout.Controls;

/// <summary>Media controls hosted inside the widget's existing surface. No window or background.</summary>
public partial class TaskbarWidgetExpandedContent : UserControl
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private MainWindow? _mainWindow;
    private readonly DispatcherTimer _progressTimer;
    private DateTimeOffset _nextProgressRead, _positionUpdatedAt;
    private double _minimumSeconds, _positionSeconds, _maximumSeconds;
    private double _paintedPositionSecond = double.NaN, _paintedDurationSecond = double.NaN;
    private bool _progressPlaying, _progressSeekable;
    private volatile bool _active, _disposed;
    private bool _drag, _albumArtHovering, _isPaused = true;
    private int _progressRefreshPending, _progressVersion;
    private int _titleVersion, _artistVersion;
    private string _titleTarget = string.Empty, _artistTarget = string.Empty;
    private GlobalSystemMediaTransportControlsSession? _seekSession, _observedSession;
    private int _progressRefreshAgain;
    private AlbumArtTransition? _albumTransition;
    private bool _canPrevious, _canPlayPause, _canNext;
    private double _morphProgress;
    private double _compactPreviousIconSize = 16, _compactPlayIconSize = 16, _compactNextIconSize = 16;
    private readonly SymbolIcon _playIcon = new(SymbolRegular.Play24, filled: true) { FontSize = 22 };
    private readonly SymbolIcon _pauseIcon = new(SymbolRegular.Pause24, filled: true) { FontSize = 22 };
    public Action<bool>? TrackNavigation { get; set; }
    public Action? ToggleExpansion { get; set; }

    public TaskbarWidgetExpandedContent()
    {
        InitializeComponent();
        ApplyButtonStyle();
        _progressTimer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(100) };
        _progressTimer.Tick += ProgressTick;
        IsVisibleChanged += (_, _) => UpdateProgressTimer();
        Unloaded += (_, _) => SetActive(false);
    }

    public void ApplyButtonStyle()
    {
        var radius = new CornerRadius(SettingsManager.Current.TaskbarWidgetButtonHoverRadius);
        PreviousButton.CornerRadius = radius;
        PlayPauseButton.CornerRadius = radius;
        NextButton.CornerRadius = radius;
    }

    // Layout endpoints used by the host's shared transition. These transforms belong
    // to expansion; text-row translations inside them still belong to song changes.
    public FrameworkElement[] MorphElements => [AlbumArtBorder, TitleMorph, ArtistMorph,
        PreviousButton, PlayPauseButton, NextButton];

    public void ApplyTextStyle(System.Windows.Controls.TextBlock title, System.Windows.Controls.TextBlock artist)
    {
        // Both rows grow by the same factor, preserving the user's typography.
        TitleText.FontSize = title.FontSize / TaskbarWidgetControl.CompactContentScale;
        ArtistText.FontSize = artist.FontSize / TaskbarWidgetControl.CompactContentScale;
        TitleText.FontFamily = title.FontFamily;
        TitleText.FontWeight = title.FontWeight;
        ArtistText.FontFamily = artist.FontFamily;
        ArtistText.FontWeight = artist.FontWeight;
        ArtistText.FontStyle = artist.FontStyle;
        ArtistText.Opacity = artist.Opacity;
        // Empty TextBlocks must reserve the same line box as populated rows.
        TitleMorph.Height = Math.Ceiling(TitleText.FontSize * TitleText.FontFamily.LineSpacing);
        ArtistMorph.Height = Math.Ceiling(ArtistText.FontSize * ArtistText.FontFamily.LineSpacing);
        SettleTextRows();
    }

    public void SetCompactButtonIconSizes(double previous, double playPause, double next)
    {
        _compactPreviousIconSize = previous;
        _compactPlayIconSize = playPause;
        _compactNextIconSize = next;
        SetMorphProgress(_morphProgress);
    }

    public void SetMorphProgress(double progress)
    {
        _morphProgress = Math.Clamp(progress, 0, 1);
        SeekRow.Opacity = Math.Clamp((progress - 0.35) / 0.65, 0, 1);
        // Keep glyph layout fixed. Changing FontSize here remeasures/recenters the
        // button template on every frame, fighting the shared-element transform.
        ScaleGlyph(PreviousIcon, _compactPreviousIconSize);
        ScaleGlyph(NextIcon, _compactNextIconSize);
        ScaleGlyph(_playIcon, _compactPlayIconSize);
        ScaleGlyph(_pauseIcon, _compactPlayIconSize);
        double visibility = _morphProgress;
        PreviousButton.Opacity = (_canPrevious ? 1 : 0.5) * visibility;
        PlayPauseButton.Opacity = (_canPlayPause ? 1 : 0.5) * visibility;
        NextButton.Opacity = (_canNext ? 1 : 0.5) * visibility;
    }

    private void ScaleGlyph(SymbolIcon icon, double compactSize)
    {
        double from = compactSize / (22 * TaskbarWidgetControl.CompactContentScale);
        double scale = from + (1 - from) * _morphProgress;
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        if (icon.RenderTransform is not ScaleTransform transform || transform.IsFrozen)
            icon.RenderTransform = transform = new ScaleTransform();
        transform.ScaleX = transform.ScaleY = scale;
    }

    public void SetMainWindow(MainWindow mainWindow) => _mainWindow = mainWindow;

    public void PublishSong(string title, string artist, BitmapImage? art, bool backwards = false)
    {
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
        StartAlbumFlip(art);
        if (_active) RequestProgressRefresh();
    }

    private void UpdateTextRow(System.Windows.Controls.TextBlock row, string text, bool backwards, Func<bool> current)
    {
        var slot = ReferenceEquals(row, TitleText) ? TitleMorph : ArtistMorph;
        var transform = (TranslateTransform)row.RenderTransform;
        double from = transform.X;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = from;
        if (!_active || !TaskbarWidgetAnimationEnvironment.AreAnimationsEnabled
            || SettingsManager.Current.TaskbarWidgetSongChangeAnimation != 1 || string.IsNullOrEmpty(row.Text))
        {
            row.Text = text;
            slot.Visibility = string.IsNullOrEmpty(text) ? Visibility.Hidden : Visibility.Visible;
            transform.X = 0;
            return;
        }
        slot.Visibility = Visibility.Visible;
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
            slot.Visibility = string.IsNullOrEmpty(text) ? Visibility.Hidden : Visibility.Visible;
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(transform.X, 0, half)
            { EasingFunction = TaskbarWidgetAnimationEnvironment.GetEasing(_mainWindow, true) });
        };
        transform.BeginAnimation(TranslateTransform.XProperty, exit);
    }

    private void SettleTextRows()
    {
        ++_titleVersion;
        ++_artistVersion;
        Park(TitleText, TitleMorph, _titleTarget);
        Park(ArtistText, ArtistMorph, _artistTarget);
        static void Park(System.Windows.Controls.TextBlock row, Border slot, string text)
        {
            var transform = (TranslateTransform)row.RenderTransform;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            row.Text = text;
            slot.Visibility = string.IsNullOrEmpty(text) ? Visibility.Hidden : Visibility.Visible;
        }
    }

    internal void SettleTransitions()
    {
        SetAlbumArt(AlbumTransition.Target);
        SettleTextRows();
    }

    public void ApplyPlaybackState(bool paused, GlobalSystemMediaTransportControlsSessionPlaybackControls? controls)
    {
        _isPaused = paused;
        PlayPauseButton.Icon = paused ? _playIcon : _pauseIcon;
        _canPrevious = controls?.IsPreviousEnabled == true;
        _canPlayPause = controls?.IsPlayEnabled == true || controls?.IsPauseEnabled == true;
        _canNext = controls?.IsNextEnabled == true;
        // Match compact controls: unavailable actions are dimmed and ignore input,
        // rather than entering the UI library's outlined disabled visual state.
        PreviousButton.IsHitTestVisible = PreviousButton.Focusable = _canPrevious;
        PlayPauseButton.IsHitTestVisible = PlayPauseButton.Focusable = _canPlayPause;
        NextButton.IsHitTestVisible = NextButton.Focusable = _canNext;
        SetMorphProgress(_morphProgress);
    }

    public void SetActive(bool active)
    {
        if (_disposed) return;
        _active = active;
        if (active) ApplyAlbumArt(AlbumArt.ImageSource as BitmapImage);
        Interlocked.Increment(ref _progressVersion);
        if (!active) RefreshObservedSession(null);
        UpdateProgressTimer();
        if (!active)
        {
            SettleTransitions();
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
        _progressTimer.Tick -= ProgressTick;
    }

    private void SongInfo_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (SettingsManager.Current.TaskbarWidgetExpandOnClick && sender is FrameworkElement block)
            TaskbarWidgetAnimationEnvironment.Pulse(block);
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

    private void RefreshObservedSession(GlobalSystemMediaTransportControlsSession? current)
    {
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
        _minimumSeconds = _positionSeconds = _maximumSeconds = 0;
        _paintedPositionSecond = _paintedDurationSecond = double.NaN;
        _progressPlaying = _progressSeekable = false;
        SeekRow.Visibility = Visibility.Hidden;
        if (current != null)
        {
            try
            {
                current.TimelinePropertiesChanged += SessionStateChanged;
                current.PlaybackInfoChanged += SessionStateChanged;
            }
            catch (Exception ex) { Logger.Debug(ex, "Taskbar session notifications unavailable; polling remains active"); }
        }
    }

    private void SessionStateChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        if (_active && ReferenceEquals(sender, _observedSession)) RequestProgressRefresh();
    }

    private void RequestProgressRefresh()
    {
        if (!_active || _disposed || Dispatcher.HasShutdownStarted) return;
        // Coalesce before queuing work, so a notification burst creates one worker.
        if (Interlocked.Exchange(ref _progressRefreshPending, 1) == 1)
        {
            Interlocked.Exchange(ref _progressRefreshAgain, 1);
            return;
        }
        // Select on the owning dispatcher, just like the widget's transport buttons.
        // The worker reads that exact session; it never resolves a player of its own.
        if (Dispatcher.CheckAccess()) StartProgressRead();
        else Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartProgressRead));
    }

    private void StartProgressRead()
    {
        if (_disposed || !_active)
        {
            Interlocked.Exchange(ref _progressRefreshPending, 0);
            return;
        }
        try
        {
            var session = _mainWindow?.GetTaskbarSession()?.ControlSession;
            RefreshObservedSession(session);
            int version = Volatile.Read(ref _progressVersion);
            _ = Task.Run(() => ReadProgress(session, version));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _progressRefreshPending, 0);
            Logger.Debug(ex, "Taskbar timeline session selection failed; retaining the last valid position");
        }
    }

    private void UpdateProgressTimer()
    {
        if (_disposed || !_active || !IsVisible)
        {
            _progressTimer.Stop();
            return;
        }
        _nextProgressRead = DateTimeOffset.UtcNow.AddSeconds(1);
        _progressTimer.Start();
        RequestProgressRefresh();
    }

    private void ProgressTick(object? sender, EventArgs e)
    {
        if (_disposed || !_active || !IsVisible) { _progressTimer.Stop(); return; }
        var now = DateTimeOffset.UtcNow;
        // The 100 ms paint uses the last snapshot; only the 1 s recovery poll and
        // actual playback/timeline events cross into the player's process.
        if (now >= _nextProgressRead)
        {
            _nextProgressRead = now.AddSeconds(1);
            RequestProgressRefresh();
        }
        ApplyCachedProgress(now);
    }

    private void ApplyCachedProgress(DateTimeOffset now)
    {
        double position = _positionSeconds + (_progressPlaying ? (now - _positionUpdatedAt).TotalSeconds : 0);
        ApplyProgress(_minimumSeconds, Math.Clamp(position, _minimumSeconds, _maximumSeconds),
            _maximumSeconds, _progressSeekable);
    }

    private void ReadProgress(GlobalSystemMediaTransportControlsSession? session, int version)
    {
        if (_disposed || !_active)
        {
            Interlocked.Exchange(ref _progressRefreshPending, 0);
            return;
        }
        var updatedAt = DateTimeOffset.UtcNow;
        double minimumSeconds = 0, positionSeconds = 0, maximumSeconds = 0;
        bool seekable = false, playing = false;
        GlobalSystemMediaTransportControlsSessionPlaybackControls? controls = null;
        bool readSucceeded = false;
        try
        {
            if (session != null)
            {
                var playback = session.GetPlaybackInfo();
                controls = playback?.Controls;
                playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var timeline = session.GetTimelineProperties();
                var snapshot = new IslandTimeline(timeline.MinSeekTime.TotalSeconds, timeline.MaxSeekTime.TotalSeconds,
                    timeline.Position.TotalSeconds, timeline.LastUpdatedTime, playing,
                    controls?.IsPlaybackPositionEnabled == true);
                if (snapshot.Valid)
                {
                    minimumSeconds = snapshot.Minimum;
                    maximumSeconds = snapshot.Maximum;
                    positionSeconds = snapshot.PositionAt(updatedAt);
                    seekable = snapshot.Seekable;
                }
                readSucceeded = true;
            }
        }
        catch (Exception ex) { Logger.Debug(ex, "Taskbar timeline read failed; retaining the last valid position"); }
        if (Dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref _progressRefreshPending, 0);
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            try
            {
                if (_disposed || !_active || version != Volatile.Read(ref _progressVersion)
                    || !ReferenceEquals(session, _observedSession) || !readSucceeded) return;
                if (_drag && Mouse.LeftButton == MouseButtonState.Released) _drag = false;
                ApplyPlaybackState(!playing, controls);
                _minimumSeconds = minimumSeconds;
                _positionSeconds = positionSeconds;
                _maximumSeconds = maximumSeconds;
                _positionUpdatedAt = updatedAt;
                _progressPlaying = playing;
                _progressSeekable = seekable;
                _progressTimer.Interval = TimeSpan.FromMilliseconds(playing ? 100 : 1000);
                _nextProgressRead = DateTimeOffset.UtcNow.AddSeconds(1);
                ApplyCachedProgress(DateTimeOffset.UtcNow);
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
        if (_active) AlbumAccent.SetTaskbarArtwork(art);
        AlbumArt.ImageSource = art;
        UpdateAlbumOverlay();
    }

    private AlbumArtTransition AlbumTransition => _albumTransition ??=
        new(ApplyAlbumArt, (AlbumFlipSurface, AlbumFlipScale));

    private void SetAlbumArt(BitmapImage? art) => AlbumTransition.Set(art);

    private void StartAlbumFlip(BitmapImage? art) => AlbumTransition.Show(art,
        TaskbarWidgetAnimationEnvironment.AreAnimationsEnabled && _active,
        TaskbarWidgetAnimationEnvironment.GetDurationMs(), SettingsManager.Current.AlbumArtChangeAnimation == 0);

    private void ApplyProgress(double minimumSeconds, double positionSeconds, double maximumSeconds, bool seekable)
    {
        TimelineProgress.Background = AlbumAccent.TaskbarBrush;
        SeekRow.Visibility = maximumSeconds > minimumSeconds ? Visibility.Visible : Visibility.Hidden;
        if (maximumSeconds <= minimumSeconds) return;

        if (_drag) return;
        // Avoid resetting/coercing Value twice on every tick when bounds are unchanged.
        bool boundsChanged = Seekbar.Minimum != minimumSeconds || Seekbar.Maximum != maximumSeconds;
        if (boundsChanged)
        {
            Seekbar.Minimum = 0;
            Seekbar.Maximum = maximumSeconds;
            Seekbar.Minimum = minimumSeconds;
        }
        Seekbar.IsEnabled = seekable;
        Seekbar.Value = positionSeconds;
        if (boundsChanged) UpdateTimelineVisual();
        double positionSecond = Math.Floor(positionSeconds);
        double durationSecond = Math.Floor(maximumSeconds);
        if (_paintedPositionSecond != positionSecond)
        {
            _paintedPositionSecond = positionSecond;
            PosText.Text = Fmt(TimeSpan.FromSeconds(positionSecond));
        }
        if (_paintedDurationSecond != durationSecond)
        {
            _paintedDurationSecond = durationSecond;
            DurText.Text = Fmt(TimeSpan.FromSeconds(durationSecond));
        }
    }

    private static string Fmt(TimeSpan t) => t.ToString(t.Hours > 0 ? @"h\:mm\:ss" : @"m\:ss");

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
        DurText.Text = Fmt(TimeSpan.FromSeconds(Math.Floor(Seekbar.Maximum)));
    }

    private void Seekbar_Down(object sender, MouseButtonEventArgs e)
    {
        if (!Seekbar.IsEnabled || _mainWindow?.GetTaskbarSession() is not { } session) return;
        _seekSession = session.ControlSession;
        _drag = true;
        _paintedPositionSecond = _paintedDurationSecond = double.NaN;
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
        if (!_canPrevious) return;
        var session = _mainWindow?.GetTaskbarSession();
        if (session == null) return;
        TaskbarWidgetAnimationEnvironment.Pulse(PreviousButton);
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
        if (!_canPlayPause) return;
        var session = _mainWindow?.GetTaskbarSession();
        if (session == null) return;
        TaskbarWidgetAnimationEnvironment.Pulse(PlayPauseButton);
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
        if (!_canNext) return;
        var session = _mainWindow?.GetTaskbarSession();
        if (session == null) return;
        TaskbarWidgetAnimationEnvironment.Pulse(NextButton);
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
        if (_mainWindow?.GetTaskbarSessionCount() > 1)
            TaskbarWidgetAnimationEnvironment.Pulse(AlbumArtBorder);
        _mainWindow?.CycleTaskbarSession();
        Interlocked.Increment(ref _progressVersion);
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
