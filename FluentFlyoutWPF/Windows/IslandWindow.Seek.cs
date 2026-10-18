using FluentFlyoutWPF.Classes;
using System.Windows;
using Windows.Media.Control;
using static WindowsMediaController.MediaManager;

namespace FluentFlyoutWPF.Windows;

public partial class IslandWindow
{
    private GlobalSystemMediaTransportControlsSession? _seekObserved;
    private IslandTimeline? _seekTimeline;
    private int _seekVersion;
    private bool _seekReadPending, _seekReadAgain;
    private bool _seekCanPlay, _seekCanPrevious, _seekCanNext;
    private long _nextSeekRead, _seekPaintTimestamp;
    private DateTimeOffset _seekPaintUtc;
    private double _seekPaintedSecond = double.NaN, _seekPaintedRemaining = double.NaN;

    private bool SeekVisible => !_disposed && IsVisible && IsBoxShown && _expanded
        && !_inactiveShown && ViewShowsFeature("media");

    private void ShutdownSeek()
    {
        ++_seekVersion;
        if (_seekObserved != null)
        {
            try
            {
                _seekObserved.TimelinePropertiesChanged -= OnSeekStateChanged;
                _seekObserved.PlaybackInfoChanged -= OnSeekStateChanged;
            }
            catch (Exception ex) { Logger.Debug(ex, "Island timeline session ended during cleanup"); }
        }
        _seekObserved = null;
        _seekTimeline = null;
        _seekCanPlay = _seekCanPrevious = _seekCanNext = false;
        _seekReadAgain = false;
        _nextSeekRead = 0;
        _seekPaintedSecond = _seekPaintedRemaining = double.NaN;
    }

    private void OnSeekStateChanged(GlobalSystemMediaTransportControlsSession sender, object args) => QueueIslandUi(() =>
    {
        if (!ReferenceEquals(sender, _seekObserved) || !SeekVisible) return;
        _nextSeekRead = 0;
        if (_seekReadPending) _seekReadAgain = true;
        if (Current() is { } session) UpdateSeek(session);
    });

    private void UpdateSeek(MediaSession session)
    {
        if (_disposed || _drag || !SeekVisible) return;
        if (!ReferenceEquals(_seekObserved, session.ControlSession))
        {
            ShutdownSeek();
            _seekObserved = session.ControlSession;
            try
            {
                _seekObserved.TimelinePropertiesChanged += OnSeekStateChanged;
                _seekObserved.PlaybackInfoChanged += OnSeekStateChanged;
            }
            catch (Exception ex) { Logger.Debug(ex, "Island timeline event registration failed"); }
        }
        if (IslandClock.Milliseconds >= _nextSeekRead)
        {
            if (!_seekReadPending) _ = ReadSeekAsync(_seekObserved, _seekVersion);
        }
        PaintSeek();
    }

    private async Task ReadSeekAsync(GlobalSystemMediaTransportControlsSession session, int version)
    {
        _seekReadPending = true;
        try
        {
            var timeline = await Task.Run(() =>
            {
                try
                {
                    var info = session.GetPlaybackInfo();
                    var value = session.GetTimelineProperties();
                    var timeline = new IslandTimeline(value.MinSeekTime.TotalSeconds, value.MaxSeekTime.TotalSeconds,
                        value.Position.TotalSeconds, value.LastUpdatedTime,
                        info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        info?.Controls?.IsPlaybackPositionEnabled == true);
                    return (Timeline: timeline, Play: info?.Controls is { } controls && (controls.IsPlayEnabled || controls.IsPauseEnabled),
                        Previous: info?.Controls?.IsPreviousEnabled == true, Next: info?.Controls?.IsNextEnabled == true);
                }
                catch { return (Timeline: default(IslandTimeline), Play: false, Previous: false, Next: false); }
            });
            if (_disposed || version != _seekVersion || !ReferenceEquals(session, _seekObserved)) return;
            _seekTimeline = timeline.Timeline;
            _seekCanPlay = timeline.Play;
            _seekCanPrevious = timeline.Previous;
            _seekCanNext = timeline.Next;
            _nextSeekRead = IslandClock.Milliseconds + 1000;
            _seekPaintTimestamp = IslandClock.Milliseconds;
            _seekPaintUtc = DateTimeOffset.UtcNow;
            if (SeekVisible && !_drag) PaintSeek();
        }
        catch (Exception ex) { Logger.Debug(ex, "Island timeline refresh failed"); }
        finally
        {
            _seekReadPending = false;
            bool again = _seekReadAgain;
            _seekReadAgain = false;
            if (!_disposed && SeekVisible && (again || version != _seekVersion) && Current() is { } current)
            {
                _nextSeekRead = 0;
                UpdateSeek(current);
            }
        }
    }

    private void PaintSeek()
    {
        BtnPlay.IsEnabled = _seekCanPlay;
        BtnPrev.IsEnabled = _seekCanPrevious;
        BtnNext.IsEnabled = _seekCanNext;
        BtnPlay.Opacity = _seekCanPlay ? 1 : 0.35;
        BtnPrev.Opacity = _seekCanPrevious ? 1 : 0.35;
        BtnNext.Opacity = _seekCanNext ? 1 : 0.35;
        if (_seekTimeline is not { Valid: true } timeline)
        {
            Seekbar.IsEnabled = false;
            SeekRow.Visibility = Visibility.Hidden;
            return;
        }
        SeekRow.Visibility = Visibility.Visible;
        Seekbar.IsEnabled = timeline.Seekable;
        if (Seekbar.Minimum != timeline.Minimum || Seekbar.Maximum != timeline.Maximum)
        {
            Seekbar.Minimum = 0;
            Seekbar.Maximum = timeline.Maximum;
            Seekbar.Minimum = timeline.Minimum;
        }
        double position = timeline.PositionAt(_seekPaintUtc.AddMilliseconds(IslandClock.Milliseconds - _seekPaintTimestamp));
        Seekbar.Value = position;
        double second = Math.Floor(position), remaining = Math.Ceiling(timeline.Maximum - position);
        if (_seekPaintedSecond != second) { _seekPaintedSecond = second; PosText.Text = Fmt(TimeSpan.FromSeconds(second)); }
        if (_seekPaintedRemaining != remaining) { _seekPaintedRemaining = remaining; DurText.Text = FmtRemaining(TimeSpan.FromSeconds(remaining)); }
        UpdateTimelineVisual();
    }
}
