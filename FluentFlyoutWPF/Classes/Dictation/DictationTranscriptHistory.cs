// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Text;
using System.Threading.Channels;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>Recovery copies written off the input/UI thread, with seven-day retention.</summary>
internal sealed class DictationTranscriptHistory : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private readonly record struct Entry(string? Text, DateTimeOffset Created);
    private readonly Channel<Entry> _queue = Channel.CreateUnbounded<Entry>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task _worker;
    private readonly System.Threading.Timer _cleanupTimer;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout", "Transcriptions");

    public DictationTranscriptHistory()
    {
        _worker = Task.Run(ProcessQueueAsync);
        // Clean on startup and hourly while running. Closed applications catch up
        // at their next start; cleanup never walks outside this dedicated folder.
        _cleanupTimer = new System.Threading.Timer(_ => QueueCleanup(), null,
            TimeSpan.Zero, TimeSpan.FromHours(1));
    }

    public void SaveAfterSubmission(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _queue.Writer.TryWrite(new Entry(text, DateTimeOffset.UtcNow));
    }

    public void QueueCleanup() => _queue.Writer.TryWrite(new Entry(null, default));

    private async Task ProcessQueueAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (entry.Text == null) CleanupExpired();
                else Save(entry);
            }
            catch (Exception ex)
            {
                // A full disk or locked file must not interrupt dictation or stop
                // subsequent backups/cleanup jobs.
                Logger.Error(ex, "Could not maintain the dictation recovery history");
            }
        }
    }

    private static void Save(Entry entry)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder,
            $"dictation-{entry.Created.LocalDateTime:yyyy-MM-dd_HH-mm-ss-fff}-{Guid.NewGuid():N}.txt");
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, entry.Text, new UTF8Encoding(false));
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void CleanupExpired()
    {
        if (!Directory.Exists(Folder)) return;
        // Leave user-created links and subdirectories alone.
        if ((File.GetAttributes(Folder) & FileAttributes.ReparsePoint) != 0) return;
        DateTime cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (string path in Directory.EnumerateFiles(Folder, "dictation-*.txt", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (File.GetCreationTimeUtc(path) <= cutoff) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn(ex, "Could not delete an expired dictation recovery copy");
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _queue.Writer.TryComplete();
        // Only shutdown waits for queued writes; dictation/input never waits on disk.
        if (!_worker.Wait(TimeSpan.FromSeconds(2)))
            Logger.Warn("Dictation history writes are still finishing at shutdown");
    }
}
