// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>Local PCM16 spool. The OS deletes it when closed, including after process exit.</summary>
internal sealed class CapturedRecording : IDisposable
{
    private const int SampleRate = 16_000;
    private readonly FileStream _stream = new(
        Path.Combine(Path.GetTempPath(), $"FluentFlyout-dictation-{Guid.NewGuid():N}.pcm"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
        FileOptions.DeleteOnClose | FileOptions.SequentialScan);

    public long Length => _stream.Length;

    public void Append(ReadOnlySpan<byte> pcm) => _stream.Write(pcm);

    /// <summary>
    /// At most 60 seconds of audio in RAM. Prefer a quiet 100 ms boundary in the last
    /// five seconds. Seek back to that boundary so every sample is read exactly once.
    /// </summary>
    public IEnumerable<float[]> ReadChunks(CancellationToken token)
    {
        _stream.Flush();
        _stream.Position = 0;
        byte[] pcm = new byte[SampleRate * 60 * 2];
        while (_stream.Position < _stream.Length)
        {
            token.ThrowIfCancellationRequested();
            int bytes = (int)Math.Min(pcm.Length, _stream.Length - _stream.Position);
            _stream.ReadExactly(pcm.AsSpan(0, bytes));
            int count = bytes / 2;
            float[] samples = new float[count];
            for (int i = 0; i < count; i++)
                samples[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f;

            if (_stream.Position < _stream.Length)
            {
                const int frame = SampleRate / 10;
                double lowestEnergy = double.MaxValue;
                int boundary = count;
                for (int start = count - SampleRate * 5; start + frame <= count; start += frame)
                {
                    double energy = 0;
                    for (int i = start; i < start + frame; i++) energy += samples[i] * samples[i];
                    if (energy < lowestEnergy)
                    {
                        lowestEnergy = energy;
                        boundary = start + frame / 2;
                    }
                }
                _stream.Seek(-(count - boundary) * 2L, SeekOrigin.Current);
                Array.Resize(ref samples, boundary);
            }
            yield return samples;
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); }
        catch (IOException ex)
        {
            // A failed flush (e.g. full disk) must not prevent the session's other
            // resources from being released. FileStream still closes its handle.
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Could not flush the temporary dictation recording");
        }
    }
}
