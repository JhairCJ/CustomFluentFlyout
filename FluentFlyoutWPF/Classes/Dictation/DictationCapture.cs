using NAudio.Wave;
using System.IO;

namespace FluentFlyoutWPF.Classes.Dictation;

internal interface IDictationCaptureDevice : IDisposable
{
    event EventHandler<WaveInEventArgs>? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
}

internal sealed class WaveInCaptureDevice : IDictationCaptureDevice
{
    // Constructed by the command worker, without a WPF SynchronizationContext.
    private readonly WaveIn _wave = new()
    {
        DeviceNumber = -1, // WAVE_MAPPER: reopen the current default input, not device index zero.
        WaveFormat = new WaveFormat(16_000, 16, 1), BufferMilliseconds = 100, NumberOfBuffers = 4,
    };
    public event EventHandler<WaveInEventArgs>? DataAvailable
    { add => _wave.DataAvailable += value; remove => _wave.DataAvailable -= value; }
    public event EventHandler<StoppedEventArgs>? RecordingStopped
    { add { _wave.RecordingStopped += value; _startFailure += value; }
      remove { _wave.RecordingStopped -= value; _startFailure -= value; } }
    public void StartRecording()
    {
        try { _wave.StartRecording(); }
        catch (Exception ex)
        {
            // WaveIn only launches RecordThread after opening, allocating buffers
            // and waveInStart succeed. A failed Start has no live recording thread.
            _startFailure?.Invoke(this, new StoppedEventArgs(ex));
            throw;
        }
    }
    private event EventHandler<StoppedEventArgs>? _startFailure;
    public void StopRecording() => _wave.StopRecording();
    public void Dispose() => _wave.Dispose();
}

/// <summary>Owns buffers until RecordingStopped, including after a driver timeout.</summary>
internal sealed class DictationCapture : IDisposable
{
    private readonly Func<IDictationCaptureDevice> _create;
    private readonly TimeSpan _stopTimeout;
    private readonly SemaphoreSlim _operations = new(1);
    private readonly object _samples = new();
    private Session? _device;
    private CapturedRecording? _recording;
    private Exception? _writeError;
    private long _generation, _lastDataTick;
    private volatile bool _active, _disposed;
    private System.Threading.Timer? _watchdog;
    private volatile float _level;
    public float Level => _level;
    public int Revives { get; private set; }
    internal Exception? LastDeviceError { get; private set; }
    public event Action? StorageFailed;
    internal bool HasDevice => Volatile.Read(ref _device) != null;

    private sealed class Session(IDictationCaptureDevice device, long generation)
    {
        public readonly IDictationCaptureDevice Device = device;
        public readonly long Generation = generation;
        public readonly TaskCompletionSource<Exception?> Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public EventHandler<WaveInEventArgs>? Data;
        public EventHandler<StoppedEventArgs>? Stop;
        public int Released;
        public bool StopRequested;
        public Task StopRequest = Task.CompletedTask;
        public CapturedRecording? QuarantinedAudio;
        public readonly long OpenTick = Environment.TickCount64;
        public long StopTick;
        public bool LateReleaseScheduled;
    }
    public DictationCapture(Func<IDictationCaptureDevice>? create = null, TimeSpan? stopTimeout = null)
    {
        _create = create ?? (() => new WaveInCaptureDevice());
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
    }
    public async Task StartAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        bool opening = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device != null) throw new InvalidOperationException("Microphone is still awaiting stop confirmation");
            opening = true;
            lock (_samples) { _recording = new(); _writeError = null; }
            _active = true;
            Revives = 0;
            LastDeviceError = null;
            OpenDevice();
            _watchdog = new(_ => _ = ReviveAsync(), null, 1000, 1000);
        }
        catch (TimeoutException)
        {
            lock (_samples)
            {
                // Keep already-valid PCM with its quarantined owner, and reject any
                // subsequent driver data. It is cleaned only after the drain confirms.
                if (_device != null) _device.QuarantinedAudio = _recording;
                _recording = null;
                _level = 0;
            }
            throw;
        }
        catch
        {
            if (!opening) throw;
            _active = false;
            // Start can fail after the driver has launched its recording thread.
            // Request stop and retain ownership rather than freeing uncertain buffers.
            try { if (_device != null) await CloseDeviceAsync().ConfigureAwait(false); }
            finally { lock (_samples) { _recording?.Dispose(); _recording = null; } }
            throw;
        }
        finally { _operations.Release(); }
    }
    private void OpenDevice()
    {
        var session = new Session(_create(), ++_generation);
        session.Data = (_, data) => OnData(session, data);
        session.Stop = (_, stopped) => session.Stopped.TrySetResult(stopped.Exception);
        session.Device.DataAvailable += session.Data;
        session.Device.RecordingStopped += session.Stop; // BEFORE StartRecording
        _device = session;
        SessionDiagnostics.Mark(DiagnosticStage.CaptureOpen, session.Generation);
        session.Device.StartRecording();
        SessionDiagnostics.Mark(DiagnosticStage.CaptureReady, session.Generation, Environment.TickCount64 - session.OpenTick);
        Volatile.Write(ref _lastDataTick, Environment.TickCount64);
    }
    public async Task<CapturedRecording?> StopAsync()
    {
        _active = false;
        Interlocked.Exchange(ref _watchdog, null)?.Dispose();
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseDeviceAsync().ConfigureAwait(false);
            lock (_samples)
            {
                var recording = _recording;
                _recording = null;
                _level = 0;
                if (_writeError != null)
                { recording?.Dispose(); throw new IOException("Could not save dictation audio", _writeError); }
                return recording;
            }
        }
        catch (TimeoutException)
        {
            lock (_samples)
            {
                if (_device != null) _device.QuarantinedAudio = _recording;
                _recording = null;
                _level = 0;
            }
            throw;
        }
        catch
        {
            lock (_samples) { _recording?.Dispose(); _recording = null; }
            throw;
        }
        finally { _operations.Release(); }
    }
    private async Task CloseDeviceAsync()
    {
        var session = _device;
        if (session == null) return;
        if (!session.StopRequested)
        {
            session.StopRequested = true;
            session.StopTick = Environment.TickCount64;
            SessionDiagnostics.Mark(DiagnosticStage.CaptureStop, session.Generation);
            session.StopRequest = Task.Run(async () =>
            {
                // Also covers WaveIn's Starting -> Capturing race: an initial stop
                // can be overwritten by DoRecording, so repeat until confirmed.
                bool reported = false;
                while (!session.Stopped.Task.IsCompleted)
                {
                    try { session.Device.StopRecording(); }
                    catch (Exception ex)
                    {
                        if (!reported) NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Microphone stop request failed; awaiting confirmation");
                        reported = true;
                    }
                    if (!session.Stopped.Task.IsCompleted) await Task.Delay(100).ConfigureAwait(false);
                }
            });
        }
        try
        {
            await Task.WhenAll(session.Stopped.Task, session.StopRequest).WaitAsync(_stopTimeout).ConfigureAwait(false);
            var error = await session.Stopped.Task.ConfigureAwait(false);
            Release(session);
            if (error != null)
            {
                LastDeviceError = error;
                SessionDiagnostics.Mark(DiagnosticStage.CaptureError, session.Generation, Environment.TickCount64 - session.StopTick);
                NLog.LogManager.GetCurrentClassLogger().Warn(error, "Microphone stopped with an error; retaining recorded PCM");
            }
        }
        catch (TimeoutException)
        {
            // One quarantined device prevents any further opens. Its final callback
            // can release it, but must never restart a cancelled recording.
            _active = false;
            SessionDiagnostics.Mark(DiagnosticStage.CaptureTimeout, session.Generation, Environment.TickCount64 - session.StopTick);
            if (!session.LateReleaseScheduled) { session.LateReleaseScheduled = true; _ = ReleaseLateAsync(session); }
            throw new TimeoutException("Microphone did not confirm its stop within five seconds");
        }
    }
    private async Task ReleaseLateAsync(Session session)
    {
        await Task.WhenAll(session.Stopped.Task, session.StopRequest).ConfigureAwait(false);
        await _operations.WaitAsync().ConfigureAwait(false);
        try { Release(session); }
        finally { _operations.Release(); }
    }
    private void Release(Session session)
    {
        if (Interlocked.Exchange(ref session.Released, 1) != 0) return;
        session.Device.DataAvailable -= session.Data;
        session.Device.RecordingStopped -= session.Stop;
        session.Device.Dispose();
        session.QuarantinedAudio?.Dispose();
        session.QuarantinedAudio = null;
        SessionDiagnostics.Mark(DiagnosticStage.CaptureRelease, session.Generation, Environment.TickCount64 - session.StopTick);
        if (ReferenceEquals(_device, session)) _device = null;
    }
    private void OnData(Session session, WaveInEventArgs data)
    {
        lock (_samples)
        {
            if (!ReferenceEquals(_device, session) || _recording == null || _writeError != null) return;
            Volatile.Write(ref _lastDataTick, Environment.TickCount64);
            try { _recording.Append(data.Buffer.AsSpan(0, data.BytesRecorded)); }
            catch (Exception ex) { _writeError = ex; StorageFailed?.Invoke(); return; }
            float sum = 0;
            for (int i = 0; i + 1 < data.BytesRecorded; i += 2)
            { float sample = (short)(data.Buffer[i] | data.Buffer[i + 1] << 8) / 32768f; sum += sample * sample; }
            _level = data.BytesRecorded == 0 ? 0 : Math.Clamp(MathF.Sqrt(sum / (data.BytesRecorded / 2)) * 9, 0, 1);
        }
    }
    private async Task ReviveAsync()
    {
        if (!_active || Environment.TickCount64 - Volatile.Read(ref _lastDataTick) < 2000
            || !await _operations.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (!_active) return;
            await CloseDeviceAsync().ConfigureAwait(false);
            if (!_active || _disposed) return;
            Revives++;
            OpenDevice();
        }
        catch (Exception ex)
        { _active = false; NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Microphone recovery failed"); StorageFailed?.Invoke(); }
        finally { _operations.Release(); }
    }
    public void Dispose()
    {
        _disposed = true;
        _active = false;
        Interlocked.Exchange(ref _watchdog, null)?.Dispose();
        _ = DisposeAsync();
    }
    internal async Task DisposeAsync()
    {
        _disposed = true;
        try { (await StopAsync().ConfigureAwait(false))?.Dispose(); }
        catch (Exception ex) { NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Microphone teardown awaiting driver"); }
    }
}
