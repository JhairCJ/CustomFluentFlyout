// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Threading;
using System.Windows.Media.Imaging;
using FluentFlyout.Classes.Utils;

namespace FluentFlyoutWPF.Classes;

/// <summary>One bounded async re-read per pending song, shared by both media surfaces.</summary>
internal sealed class MediaArtworkPublication
{
    private readonly MediaArtworkBuffer _buffer = new();
    private readonly DispatcherTimer _timer;
    private readonly Func<MediaArtworkSnapshot, Task<MediaArtworkSnapshot?>> _read;
    private readonly Action<MediaArtworkSnapshot> _publish;
    private bool _reading;

    public MediaArtworkSnapshot? Published => _buffer.Published;

    public MediaArtworkPublication(Dispatcher dispatcher,
        Func<MediaArtworkSnapshot, Task<MediaArtworkSnapshot?>> read, Action<MediaArtworkSnapshot> publish)
    {
        _read = read;
        _publish = publish;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += OnDeadline;
    }

    public void Observe(MediaArtworkSnapshot snapshot)
    {
        if (_buffer.Offer(snapshot, Environment.TickCount64) is { } committed) _publish(committed);
        Schedule();
    }

    public void Cancel()
    {
        _buffer.Cancel();
        _timer.Stop();
    }

    private void Schedule()
    {
        _timer.Stop();
        if (_buffer.Pending == null || _reading) return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _buffer.Deadline - Environment.TickCount64));
        _timer.Start();
    }

    private async void OnDeadline(object? sender, EventArgs args)
    {
        _timer.Stop();
        if (_buffer.Pending is not { } pending || _reading) return;
        int version = _buffer.Version;
        _reading = true;
        MediaArtworkSnapshot? fresh = null;
        try
        {
            fresh = await _read(pending).WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        catch (Exception)
        {
            // A closed/unresponsive session publishes a placeholder, never stale artwork.
        }
        finally { _reading = false; }
        if (version == _buffer.Version && _buffer.Resolve(fresh, Environment.TickCount64) is { } committed)
            _publish(committed);
        Schedule();
    }
}

internal sealed record MediaArtworkSnapshot(object Session, string Title, string Artist, BitmapImage? Artwork)
{
    public bool SameTrack(MediaArtworkSnapshot other) => Equals(Session, other.Session)
        && Title == other.Title && Artist == other.Artist;
}

/// <summary>Deterministic song publication; callers supply monotonic milliseconds.</summary>
internal sealed class MediaArtworkBuffer
{
    public const int WaitMs = 350;
    public MediaArtworkSnapshot? Published { get; private set; }
    public MediaArtworkSnapshot? Pending { get; private set; }
    public long Deadline { get; private set; }
    public int Version { get; private set; }

    public MediaArtworkSnapshot? Offer(MediaArtworkSnapshot snapshot, long now)
    {
        if (snapshot == Published)
        {
            if (Pending != null) Cancel();
            return null;
        }
        bool samePending = Pending != null && snapshot.SameTrack(Pending);
        if (Pending != snapshot) ++Version;
        Pending = snapshot;
        if (!samePending) Deadline = now + WaitMs;
        // A repeated cover on a NEW track can be Chrome's stale intermediate cover.
        bool complete = snapshot.Artwork != null && (Published == null
            || snapshot.SameTrack(Published)
            || !AlbumArtworkSimilarity.AreSimilar(snapshot.Artwork, Published.Artwork));
        return complete ? Commit() : null;
    }

    public MediaArtworkSnapshot? Resolve(MediaArtworkSnapshot? fresh, long now)
    {
        if (Pending == null || now < Deadline) return null;
        if (fresh != null && !fresh.SameTrack(Pending)) return Offer(fresh, now);
        // Failure must clear the old cover rather than publishing a stale fallback.
        Pending = fresh ?? Pending with { Artwork = null };
        return Commit();
    }

    public void Cancel()
    {
        ++Version;
        Pending = null;
    }

    private MediaArtworkSnapshot Commit()
    {
        Published = Pending!;
        Cancel();
        return Published;
    }
}
