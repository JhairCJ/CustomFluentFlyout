// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Controls.TaskbarWidget;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FluentFlyoutWPF.Classes
{
    /// <summary>
    /// Loopback spectrum engine behind the taskbar equalizer.
    ///
    /// Two threads, one hand-off each way:
    ///   capture thread: WASAPI loopback -> ring buffer -> silence gate ->
    ///     FFT (skipped while the gate says quiet) -> writes <see cref="_targetValues"/>.
    ///     Targets are the ONLY thing the capture thread owns, and it writes them
    ///     raw (no EMA, no decay): silence writes zeros there within ~1-2 ms of
    ///     the audio actually stopping, so the bars start falling on the very
    ///     next frame — there is no hold, no freeze window, no stale buffer
    ///     path. That immediacy is what kills the old "hangs ~200 ms on
    ///     silence" behaviour for good.
    ///   render thread (UI): eases <see cref="_barValues"/> toward the targets
    ///     with frame-rate-independent attack/release (the ONLY smoothing in
    ///     the pipeline), eases the bar color toward the album accent over the
    ///     rasterizes only the bars that changed into the WriteableBitmap.
    /// </summary>
    public class Visualizer : IDisposable
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Fuente viva de ajustes del ecualizador. Dos presets: Taskbar (los de
        /// siempre) e Island (propios de Fluent Island). Los Func se leen en cada
        /// frame, así que los sliders se aplican sin eventos.
        /// </summary>
        public sealed class Options
        {
            public Func<bool> Enabled = () => SettingsManager.Current.TaskbarVisualizerEnabled;
            public Func<bool> HighRefreshRate = () => SettingsManager.Current.TaskbarVisualizerHighRefreshRate;
            public Func<int> BarCount = () => SettingsManager.Current.TaskbarVisualizerBarCount;
            public Func<bool> CenteredBars = () => SettingsManager.Current.TaskbarVisualizerCenteredBars;
            public Func<bool> Baseline = () => SettingsManager.Current.TaskbarVisualizerBaseline;
            public Func<bool> BaselineAutoHide = () => SettingsManager.Current.TaskbarVisualizerBaselineAutoHide;
            public Func<int> Sensitivity = () => SettingsManager.Current.TaskbarVisualizerAudioSensitivity;
            public Func<int> PeakLevel = () => SettingsManager.Current.TaskbarVisualizerAudioPeakLevel;
            public Func<int> Smoothing = () => SettingsManager.Current.TaskbarVisualizerSmoothing;
            public Action<bool> SetHasContent = v => SettingsManager.Current.TaskbarVisualizerHasContent = v;

            public static Options Taskbar { get; } = new();
            public static Options Island { get; } = new()
            {
                Enabled = () => SettingsManager.Current.IslandEqEnabled,
                HighRefreshRate = () => false, // ponytail: barras pequeñas, 30 FPS basta
                BarCount = () => SettingsManager.Current.IslandEqBarCount,
                CenteredBars = () => false,
                Baseline = () => false,
                BaselineAutoHide = () => false,
                Sensitivity = () => SettingsManager.Current.IslandEqSensitivity,
                PeakLevel = () => 3,
                Smoothing = () => SettingsManager.Current.IslandEqSmoothing,
                SetHasContent = _ => { }, // la isla se muestra/oculta por reproducción, no por contenido
            };
        }

        private readonly Options _opts;
        private int _barCount = 10;
        private const int ImageWidth = 76 * 3;
        private const int ImageHeight = 32 * 3;
        private const int BarSpacing = 2 * 3;

        private WasapiRecorder? _capture;
        private MMDevice? _renderDevice;
        private float[]? _barValues;
        private float[]? _targetValues;
        private WriteableBitmap? _bitmap;
        private volatile bool _isRunning;
        private readonly object _lock = new();

        // Last drawn rect per bar, so static bars are skipped entirely instead of
        // re-rasterized every frame. Only changed bars are cleared, redrawn and
        // marked dirty. Reset (empty) whenever the bar count changes.
        private int[] _prevBarY = [];
        private int[] _prevBarEndY = [];

        // FFT layout. The ring holds raw samples; each hop the last FftLength
        // samples are windowed into _fftWork and transformed.
        private const int FftLength = 4096;
        private const int FftOrder = 12; // log2(FftLength)
        // Overlapping hop in high-refresh mode. 512 (~94 FFT/s @48kHz) is plenty:
        // the render-thread attack/release smoothing (tens/hundreds of ms) dominates
        // what the eye sees, so 256 would double FFT + sqrt CPU for zero visual gain.
        private const int FftHopHighRefresh = 512;
        private readonly float[] _ring = new float[FftLength];
        private readonly Complex[] _fftWork = new Complex[FftLength];
        private int _ringPos;
        private int _samplesSinceHop;
        private int _fftHop;

        // Precomputed Hamming window: evaluating FastFourierTransform.HammingWindow
        // (a cosine) 4096 times per FFT costs more than this table's memory.
        private readonly float[] _windowTable = new float[FftLength];

        // Silence gate: RMS over the newest GateWindow samples (~5 ms at 48 kHz),
        // re-evaluated every GateTick samples. True loopback silence (pause,
        // stopped player) is exact zeros, so a -80 dBFS cut is unambiguous.
        // The gate answers within one tick (~1.3 ms) instead of waiting for
        // the 85 ms FFT window to drain, and the FFT is skipped outright
        // while the gate says quiet.
        private const int GateWindow = 256; // power of two, ~5.3 ms at 48 kHz
        private const int GateTick = 64; // re-evaluate every ~1.3 ms
        private const float SilenceRms = 1e-4f; // ~ -80 dBFS
        private readonly float[] _gateSquares = new float[GateWindow];
        private float _gateSum;
        private int _gatePos;
        private int _samplesSinceGate;
        private volatile bool _gateSilent = true;

        // Precomputed per-bar FFT bin ranges + high-frequency boost. Rebuilt only
        // when (bar count, sample rate, sensitivity, peak) changes; the per-FFT
        // path then does no Math.Pow at all.
        private struct BandRange
        {
            public int StartBin;
            public int EndBin;
            public float Boost;
        }
        private BandRange[] _bandTable = [];
        private int _bandKeyBars = -1;
        private int _bandKeyRate = -1;
        private int _bandKeySens = -1;
        private int _bandKeyPeak = -1;
        private float _bandMinDb;
        private float _bandMaxDb;

        // Cached at Start so the audio callback never dereferences _capture
        // (which StopCapture can null mid-callback) and never touches settings.
        private int _bytesPerSample;
        private int _sampleRate;

        private System.Timers.Timer? _captureWatchdog;
        private DateTime _lastDataAvailableUtc = DateTime.MinValue;
        private int _restartInProgress; // 0=false, 1=true (Interlocked)
        private string? _deviceId; // track current device ID for restart logic

        // Render loop, driven either by CompositionTarget.Rendering (monitor
        // refresh rate) or by a 30 FPS DispatcherTimer when high refresh rate
        // is disabled.
        private DispatcherTimer? _renderTimer;
        private volatile bool _renderLoopActive;
        private int _renderLoopRequested; // 0=false, 1=true (Interlocked)
        private readonly Stopwatch _renderStopwatch = new();
        private double _lastRenderTime;

        // Last time a spectrum window held real, measurable signal (stamped on
        // the capture thread). Only the auto-hide grace below reads it — the
        // bars themselves react to silence instantly through the gate.
        private DateTime _lastAudibleUtc = DateTime.MinValue;
        // Grace before the baseline auto-hide actually hides, so quiet passages
        // in a song don't flicker the container off and on.
        private const int AutoHideGraceMs = 800;
        private bool _hasContent;
        // Once the last callback is older than this, the pipe is considered dead
        // and the render thread treats targets as zero until callbacks resume.
        private const int DataStallMs = 150;
        private bool _disposed;

        // Frame-rate independent attack/release smoothing (seconds). Driven by
        // the TaskbarVisualizerSmoothing setting (0 = snappy, 100 = silky);
        // resolved once per change in EnsureSmoothing, never per bar. This is
        // the ONLY smoothing in the pipeline: the capture thread writes raw
        // per-FFT intensities straight into the targets.
        private double _attackSeconds = 0.036;
        private double _releaseSeconds = 0.49;
        private int _smoothingKey = -1;

        // Accent color transition: the bars ease from the previous album color
        // to the new one over the same duration as the other song-change
        // animations instead of snapping. A retarget mid-flight restarts from
        // the color actually on screen, so rapid song changes never jump.
        // _drawnArgb doubles as the dirty-check color: a frame whose resolved
        // color differs repaints every bar.
        private int _drawnArgb = -1;
        private int _colorFromArgb = -1;
        private int _colorToArgb = -1;
        private DateTime _colorAnimStartUtc = DateTime.MinValue;

        public WriteableBitmap? Bitmap
        {
            get
            {
                lock (_lock)
                {
                    return _bitmap;
                }
            }
        }

        public Visualizer(Options? opts = null)
        {
            _opts = opts ?? Options.Taskbar;
            InitializeBitmap();

            for (int i = 0; i < FftLength; i++)
                _windowTable[i] = (float)FastFourierTransform.HammingWindow(i, FftLength);

            _fftHop = _opts.HighRefreshRate() ? FftHopHighRefresh : FftLength;

            ResizeBarList(_opts.BarCount());
            AudioDeviceMonitor.Instance.DefaultDeviceChanged += OnDefaultDeviceChanged;
            TryRegisterSystemEvents();
        }

        private void TryRegisterSystemEvents()
        {
            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                // On some environments (e.g. non-interactive sessions), SystemEvents may not be available.
                Logger.Warn(ex, "Failed to register SystemEvents handlers for visualizer auto-restart");
            }
        }

        private void TryUnregisterSystemEvents()
        {
            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to unregister SystemEvents handlers for visualizer auto-restart");
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (!_opts.Enabled())
                return;

            // When unlocking after device disconnect (e.g. Bluetooth earbuds), WASAPI loopback can get stuck.
            // Restart capture on unlock / logon to recover without user action.
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon)
            {
                RequestRestart($"session switch: {e.Reason}");
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (!_opts.Enabled())
                return;

            if (e.Mode == PowerModes.Resume)
            {
                RequestRestart("power resume");
            }
        }

        private void InitializeBitmap()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                lock (_lock)
                {
                    _bitmap = new WriteableBitmap(ImageWidth, ImageHeight, 96, 96, PixelFormats.Bgra32, null);
                }
            });
        }

        private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
        {
            _deviceId = e.DeviceId;

            // Even if capture isn't currently running (e.g. restart attempt failed while the device was reconfiguring),
            // we still want to try restarting as soon as Windows reports a usable default endpoint again.
            if (!_opts.Enabled())
                return;
            RequestRestart("default audio output device changed");
        }

        private void RequestRestart(string reason)
        {
            if (!_opts.Enabled())
                return;

            if (Interlocked.Exchange(ref _restartInProgress, 1) == 1)
                return;

            Logger.Info($"Restarting visualizer ({reason})");

            Task.Run(async () =>
            {
                try
                {
                    // Capture-only teardown: the loop and the bars survive the
                    // gap (pause/track change must not alter the visualizer).
                    StopCapture();

                    // Retry with backoff for as long as the visualizer stays
                    // enabled and alive: giving up after N attempts left the last
                    // frame frozen on screen forever.
                    int attempt = 0;
                    while (!_isRunning && !_disposed && _opts.Enabled())
                    {
                        await Task.Delay(Math.Min(500 * (1 << Math.Min(attempt, 4)), 5000));
                        attempt++;
                        Start();
                        if (_isRunning)
                            return;
                        Logger.Warn($"Visualizer restart attempt {attempt} failed, retrying...");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Visualizer restart failed");
                }
                finally
                {
                    Interlocked.Exchange(ref _restartInProgress, 0);
                }
            });
        }

        public void ResizeBarList(int newBarCount)
        {
            _barCount = newBarCount;
            _barValues = new float[_barCount];
            _targetValues = new float[_barCount];
        }

        public void Start()
        {
            if (_isRunning)
                return;

            // Reallocate only on a count change: restart gaps (device reconfigure)
            // must not blank the bars mid-song.
            if (_barValues == null || _barValues.Length != _barCount
                || _targetValues == null || _targetValues.Length != _barCount)
            {
                ResizeBarList(_barCount);
            }

            // Fresh capture state: any audio still sitting in the ring from a
            // previous session would otherwise ghost into the first windows.
            Array.Clear(_ring, 0, _ring.Length);
            Array.Clear(_gateSquares, 0, _gateSquares.Length);
            _ringPos = 0;
            _samplesSinceHop = 0;
            _gateSum = 0;
            _gatePos = 0;
            _samplesSinceGate = 0;
            _gateSilent = true;
            _lastAudibleUtc = DateTime.MinValue;
            _fftHop = _opts.HighRefreshRate() ? FftHopHighRefresh : FftLength;

            try
            {
                // Explicitly bind to the current default render endpoint.
                // Using the parameterless capture can throw transient COM errors when the default endpoint is
                // reconfiguring (e.g. Bluetooth earbuds disconnect/reconnect around lock/unlock).
                _renderDevice?.Dispose();
                _renderDevice = string.IsNullOrWhiteSpace(_deviceId)
                     ? AudioDeviceMonitor.Instance.GetDefaultRenderDevice()
                     : AudioDeviceMonitor.Instance.GetDeviceById(_deviceId) ?? AudioDeviceMonitor.Instance.GetDefaultRenderDevice();

                if (_renderDevice == null)
                {
                    return;
                }

                _capture = new WasapiRecorderBuilder()
                    .WithDevice(_renderDevice)
                    .WithLoopbackCapture()
                    .Build();
                _bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
                _sampleRate = _capture.WaveFormat.SampleRate;
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                _isRunning = true;
                _lastDataAvailableUtc = DateTime.UtcNow;

                // Dead-capture watchdog: ticks every second and only restarts
                // capture — it never touches visuals (bars rest at zero through
                // the data-stall fallback in RenderFrame). Published via
                // Interlocked.Exchange so a concurrent StopCapture can never
                // miss it (the old callback-then-assign race leaked a running
                // timer that kept restarting a stopped capture).
                var watchdog = new System.Timers.Timer(1000)
                {
                    AutoReset = true
                };
                watchdog.Elapsed += (_, _) =>
                {
                    if (_isRunning && !_disposed
                        && _opts.Enabled()
                        && DateTime.UtcNow - _lastDataAvailableUtc > TimeSpan.FromSeconds(2))
                    {
                        RequestRestart("no capture callbacks for over 2s");
                    }
                };
                watchdog.Start();
                var previousWatchdog = Interlocked.Exchange(ref _captureWatchdog, watchdog);
                previousWatchdog?.Stop();
                previousWatchdog?.Dispose();

                // Pinned baseline (baseline without auto-hide) is visible from
                // the start, even before the first audio frame arrives.
                if (_opts.Baseline()
                    && !_opts.BaselineAutoHide())
                {
                    EnsureRenderLoop();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to start visualizer");
            }
        }

        public void Stop()
        {
            if (!_isRunning)
                return;

            StopCapture();

            StopRenderLoop();

            // Park the visuals (disable path only — never on restart gaps, so a
            // reconfigure never blanks or freezes the bars mid-song).
            if (_barValues != null) Array.Clear(_barValues, 0, _barValues.Length);
            if (_targetValues != null) Array.Clear(_targetValues, 0, _targetValues.Length);
            SetHasContent(false);
        }

        /// <summary>
        /// Tears down capture only, keeping the render loop and the last visuals:
        /// restart gaps (e.g. endpoint reconfigure on track change) glide to rest
        /// through the data-stall fallback instead of blanking or freezing mid-song.
        /// </summary>
        private void StopCapture()
        {
            _isRunning = false;

            _capture?.DataAvailable -= OnDataAvailable;
            _capture?.RecordingStopped -= OnRecordingStopped;
            _capture?.Dispose();
            _capture = null;

            _renderDevice?.Dispose();
            _renderDevice = null;

            // Atomic take: pairs with the Exchange publish in Start so neither
            // side can lose the timer, whichever thread wins the race.
            var watchdog = Interlocked.Exchange(ref _captureWatchdog, null);
            if (watchdog != null)
            {
                watchdog.Stop();
                watchdog.Dispose();
            }
        }

        // -------------------------------------------------------------------
        // Capture thread: samples in, bar targets out. Nothing else.
        // -------------------------------------------------------------------

        private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            if (!_isRunning)
                return;

            // Any callback — even an empty one — proves the capture is alive.
            _lastDataAvailableUtc = DateTime.UtcNow;

            if (buffer.IsEmpty)
                return;

            // WASAPI flags the packet Silent when the endpoint renders nothing:
            // feed true zeros so the silence gate answers immediately instead
            // of measuring whatever stale bytes the buffer holds.
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                int silentSamples = buffer.Length / Math.Max(_bytesPerSample, 1);
                for (int i = 0; i < silentSamples; i++)
                    PushSample(0f);
                return;
            }

            // Note on channels: every sample advances the ring, including the
            // right channel of an interleaved stereo stream. That deliberate
            // quirk preserves the band response this visualizer was tuned with
            // (bin mapping, boosts); a mono downmix would shift everything an
            // octave up and overdrive the boosted top bars.
            if (_bytesPerSample == 4)
            {
                var samples = MemoryMarshal.Cast<byte, float>(buffer);
                for (int i = 0; i < samples.Length; i++)
                    PushSample(samples[i]);
            }
            else if (_bytesPerSample == 2)
            {
                var samples = MemoryMarshal.Cast<byte, short>(buffer);
                for (int i = 0; i < samples.Length; i++)
                    PushSample(samples[i] * (1f / 32768f));
            }
            // Unknown sample format: leave targets untouched rather than spin
            // garbage into the ring; the watchdog restarts a broken capture.
        }

        private void PushSample(float s)
        {
            _ring[_ringPos] = s;
            _ringPos = (_ringPos + 1) & (FftLength - 1);

            // Sliding gate energy over the newest GateWindow samples: O(1) per
            // sample, no per-window rescan.
            float sq = s * s;
            _gateSum -= _gateSquares[_gatePos];
            _gateSquares[_gatePos] = sq;
            _gateSum += sq;
            _gatePos = (_gatePos + 1) & (GateWindow - 1);

            if (++_samplesSinceGate >= GateTick)
            {
                _samplesSinceGate = 0;
                EvaluateGate();
            }

            if (++_samplesSinceHop >= _fftHop)
            {
                _samplesSinceHop = 0;
                RunSpectrum();
            }
        }

        /// <summary>
        /// The silence gate is authoritative and immediate: the moment the recent
        /// audio is quiet, targets go to zero and the FFT stops running until
        /// sound returns. Bars therefore start falling one frame after real
        /// silence — no hold, no drain lag, no 200 ms hang. No hashes, no
        /// clocks: just the short-window RMS. A dead pipe that keeps calling
        /// back is covered by the data-stall fallback in RenderFrame plus the
        /// capture watchdog, not here.
        /// </summary>
        private void EvaluateGate()
        {
            float rms = (float)Math.Sqrt(Math.Max(_gateSum, 0f) / GateWindow);

            _gateSilent = rms < SilenceRms;

            if (_gateSilent && _targetValues != null)
                Array.Clear(_targetValues, 0, _targetValues.Length);
        }

        private void RunSpectrum()
        {
            // Quiet gate: targets are already zero and there is nothing new to
            // measure — the FFT (~85 ms window still draining old music) must
            // not resurrect stale targets over the zeros.
            if (_gateSilent)
                return;

            // Copy the sliding window into the work buffer (in chronological
            // order), applying the precomputed Hamming window.
            for (int j = 0; j < FftLength; j++)
            {
                int src = _ringPos + j;
                if (src >= FftLength) src -= FftLength;
                _fftWork[j].X = _ring[src] * _windowTable[j];
                _fftWork[j].Y = 0;
            }

            FastFourierTransform.FFT(true, FftOrder, _fftWork);

            EnsureSmoothing();
            EnsureBandTable(_sampleRate);

            var targets = _targetValues;
            if (targets == null)
                return;

            int count = Math.Min(Math.Min(_barCount, _bandTable.Length), targets.Length);
            bool audible = false;

            for (int i = 0; i < count; i++)
            {
                int startBin = _bandTable[i].StartBin;
                int endBin = _bandTable[i].EndBin;

                float maxAmplitude = 0;
                for (int j = startBin; j < endBin; j++)
                {
                    float amplitude = (float)Math.Sqrt(_fftWork[j].X * _fftWork[j].X + _fftWork[j].Y * _fftWork[j].Y);
                    if (amplitude > maxAmplitude)
                        maxAmplitude = amplitude;
                }

                maxAmplitude *= _bandTable[i].Boost;

                if (maxAmplitude < 0.001f) maxAmplitude = 0.001f;

                float db = 20f * (float)Math.Log10(maxAmplitude);

                float intensity = (db - _bandMinDb) / (_bandMaxDb - _bandMinDb);
                intensity = Math.Clamp(intensity, 0f, 1f);

                // Single source of truth: raw intensity goes straight to the
                // target. All smoothing lives in the render thread
                // (attack/release in RenderFrame) — no second EMA here that
                // would stack release times and hang the fall.
                targets[i] = intensity;
                if (intensity > 0.01f)
                    audible = true;
            }

            if (audible)
            {
                _lastAudibleUtc = DateTime.UtcNow;
                EnsureRenderLoop();
            }
        }

        /// <summary>
        /// Resolves the smoothing setting (0-100) into time constants once per
        /// change. Slider feel: attack 12ms (instant punch) .. 60ms, release
        /// 80ms (lively) .. 900ms (slow melt). Default (50) is roughly 36ms
        /// attack / 490ms release. Idempotent and safe to call from either thread.
        /// </summary>
        private void EnsureSmoothing()
        {
            int s = _opts.Smoothing();
            if (s < 0) s = 0;
            else if (s > 100) s = 100;
            if (s == _smoothingKey)
                return;
            _smoothingKey = s;

            float t = s / 100f;
            _attackSeconds = 0.012 + t * 0.048;
            _releaseSeconds = 0.08 + t * 0.82;
        }

        /// <summary>
        /// Rebuilds the per-bar FFT bin ranges, boosts and dB range only when the
        /// inputs change. The per-FFT path above then performs zero Math.Pow calls.
        /// </summary>
        private void EnsureBandTable(int sampleRate)
        {
            int bars = _barCount;
            int sens = _opts.Sensitivity();
            int peak = _opts.PeakLevel();

            if (_bandTable.Length == bars
                && _bandKeyBars == bars
                && _bandKeyRate == sampleRate
                && _bandKeySens == sens
                && _bandKeyPeak == peak)
                return;

            const double minFreq = 40;   // Hz
            const double maxFreq = 8000; // Hz
            double frequencyPerBin = (double)sampleRate / FftLength;
            double ratio = maxFreq / minFreq;

            var table = new BandRange[Math.Max(bars, 0)];
            for (int i = 0; i < table.Length; i++)
            {
                double startFreq = minFreq * Math.Pow(ratio, (double)i / bars);
                double endFreq = minFreq * Math.Pow(ratio, (double)(i + 1) / bars);

                int startBin = (int)(startFreq / frequencyPerBin);
                int endBin = (int)(endFreq / frequencyPerBin);

                if (endBin <= startBin) endBin = startBin + 1;
                if (endBin >= FftLength / 2) endBin = FftLength / 2 - 1;
                if (startBin < 0) startBin = 0;

                float progress = bars > 0 ? (float)i / bars : 0f;
                table[i] = new BandRange
                {
                    StartBin = startBin,
                    EndBin = endBin,
                    Boost = 1.0f + (progress * 75.0f)
                };
            }

            _bandTable = table;
            _bandKeyBars = bars;
            _bandKeyRate = sampleRate;
            _bandKeySens = sens;
            _bandKeyPeak = peak;
            _bandMinDb = (sens * -10f) - 30f;
            _bandMaxDb = (peak * 10f) - 30f;
        }

        // -------------------------------------------------------------------
        // Render thread (UI): targets in, pixels out.
        // -------------------------------------------------------------------

        private void EnsureRenderLoop()
        {
            if (!_isRunning || _renderLoopActive)
                return;
            if (Interlocked.CompareExchange(ref _renderLoopRequested, 1, 0) == 1)
                return;

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_renderLoopActive || !_isRunning)
                        return;
                    StartRenderLoopCore();
                }
                finally
                {
                    Interlocked.Exchange(ref _renderLoopRequested, 0);
                }
            });
        }

        private void StartRenderLoopCore()
        {
            StopRenderLoopCore();
            _renderLoopActive = true;
            _renderStopwatch.Restart();
            _lastRenderTime = 0;

            if (_opts.HighRefreshRate())
            {
                // CompositionTarget.Rendering fires once per composited frame,
                // i.e. at the monitor's refresh rate.
                CompositionTarget.Rendering += OnRenderingFrame;
            }
            else
            {
                _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(1000.0 / 30)
                };
                _renderTimer.Tick += OnRenderTimerTick;
                _renderTimer.Start();
            }
        }

        /// <summary>
        /// Restarts the render loop so a high refresh rate toggle takes effect immediately.
        /// </summary>
        public void RestartRenderLoop()
        {
            if (!_isRunning || !_renderLoopActive)
                return;

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (!_isRunning)
                    return;
                // The high-refresh toggle changes the audio-thread hop: refresh the
                // cached value together with the render loop so both switch atomically.
                _fftHop = _opts.HighRefreshRate() ? FftHopHighRefresh : FftLength;
                StopRenderLoopCore();
                StartRenderLoopCore();
            });
        }

        private void StopRenderLoop()
        {
            if (Application.Current.Dispatcher.CheckAccess())
            {
                StopRenderLoopCore();
            }
            else
            {
                Application.Current.Dispatcher.Invoke(StopRenderLoopCore);
            }
        }

        private void StopRenderLoopCore()
        {
            _renderLoopActive = false;
            CompositionTarget.Rendering -= OnRenderingFrame;
            _renderTimer?.Stop();
            _renderTimer = null;
        }

        private void OnRenderingFrame(object? sender, EventArgs e)
        {
            RenderFrame();
        }

        private void OnRenderTimerTick(object? sender, EventArgs e)
        {
            RenderFrame();
        }

        private void RenderFrame()
        {
            if (!_isRunning || !_renderLoopActive)
                return;

            double now = _renderStopwatch.Elapsed.TotalSeconds;
            double dt = now - _lastRenderTime;
            _lastRenderTime = now;
            if (dt <= 0 || dt > 0.25)
                dt = 1.0 / 60.0;

            EnsureSmoothing();

            var bars = _barValues;
            var targets = _targetValues;
            int count = bars == null || targets == null
                ? 0
                : Math.Min(_barCount, Math.Min(bars.Length, targets.Length));

            // Dead-capture fallback: with no callbacks at all (device stall,
            // restart gap), targets are read as zero so the bars glide to rest
            // instead of freezing on stale values. Live silence never reaches
            // this branch — the gate has already zeroed the targets themselves.
            // Single clock read per frame: the stall and grace checks share it.
            DateTime frameUtc = DateTime.UtcNow;
            bool dataStale = (frameUtc - _lastDataAvailableUtc).TotalMilliseconds > DataStallMs;

            float attackFactor = 1f - (float)Math.Exp(-dt / _attackSeconds);
            float releaseFactor = 1f - (float)Math.Exp(-dt / _releaseSeconds);

            bool resting = true;
            for (int i = 0; i < count; i++)
            {
                float target = dataStale ? 0f : targets[i];
                float current = bars![i];

                float next = target > current
                    ? current + (target - current) * attackFactor
                    : current + (target - current) * releaseFactor;

                // Settle exactly at zero instead of crawling asymptotically
                // forever and keeping the "resting" check (and the auto-hide)
                // from ever firing.
                if (next is < 0.0005f and > -0.0005f)
                    next = 0f;

                bars[i] = next;
                if (next > 0.01f)
                    resting = false;
            }

            // Container visibility. Without auto-hide the visualizer never
            // collapses once shown (pause and track gaps keep flat bars, not a
            // hide/show jump). With baseline auto-hide it hides once the bars
            // have visibly settled and the grace has elapsed, and reappears the
            // frame after audio returns.
            bool autoHides = _opts.Baseline()
                && _opts.BaselineAutoHide();
            bool audibleRecently = (frameUtc - _lastAudibleUtc).TotalMilliseconds < AutoHideGraceMs;
            SetHasContent(!autoHides || audibleRecently || !resting);

            // Idle fast path: every bar settled and the on-screen color already
            // matches the accent — DrawBars would touch nothing, so skip the
            // bitmap Lock/Unlock round-trip entirely (the dominant cost of a
            // quiet frame). Any new audio, height or color change takes the
            // normal path below on the very next frame.
            if (resting)
            {
                SolidColorBrush idleBrush = AlbumAccent.Brush;
                int idleArgb = (idleBrush.Color.R << 16) | (idleBrush.Color.G << 8) | idleBrush.Color.B;
                if (idleArgb == _drawnArgb)
                    return;
            }

            UpdateBitmap();
        }

        private void SetHasContent(bool value)
        {
            if (_hasContent == value)
                return;
            _hasContent = value;
            _opts.SetHasContent(value);
        }

        private void UpdateBitmap()
        {
            if (_bitmap == null)
                return;

            lock (_lock)
            {
                if (_bitmap == null)
                    return;

                _bitmap.Lock();

                try
                {
                    unsafe
                    {
                        IntPtr pBackBuffer = _bitmap.BackBuffer;
                        int stride = _bitmap.BackBufferStride;
                        int bufferSize = stride * ImageHeight;

                        Span<byte> buffer = new Span<byte>(pBackBuffer.ToPointer(), bufferSize);

                        // DrawBars clears/redraws only changed bars and reports their
                        // bounding box; unchanged frames mark nothing dirty.
                        if (DrawBars(stride, buffer, out int dirtyX, out int dirtyY, out int dirtyW, out int dirtyH))
                            _bitmap.AddDirtyRect(new Int32Rect(dirtyX, dirtyY, dirtyW, dirtyH));
                    }
                }
                finally
                {
                    _bitmap.Unlock();
                }
            }
        }

        /// <summary>
        /// Resolves the bar color for this frame, easing toward <paramref name="targetArgb"/>
        /// over the shared song-change animation duration, so the equalizer's color
        /// transitions between songs instead of snapping. Snaps instantly when widget
        /// animations are disabled. Runs on the UI thread (render loop).
        /// </summary>
        private int ResolveBarColor(int targetArgb)
        {
            if (!TaskbarWidgetAnimationEnvironment.AreAnimationsEnabled)
            {
                _colorToArgb = targetArgb;
                _colorFromArgb = targetArgb;
                return targetArgb;
            }

            if (targetArgb != _colorToArgb)
            {
                // Retarget mid-flight from the color actually on screen right
                // now, so rapid song changes never jump.
                _colorFromArgb = _drawnArgb < 0 ? targetArgb : _drawnArgb;
                _colorToArgb = targetArgb;
                _colorAnimStartUtc = DateTime.UtcNow;
            }

            if (_drawnArgb == _colorToArgb)
                return _colorToArgb;

            double totalMs = Math.Max(TaskbarWidgetAnimationEnvironment.GetDurationMs(), 1);
            double t = (DateTime.UtcNow - _colorAnimStartUtc).TotalMilliseconds / totalMs;
            if (t >= 1)
                return _colorToArgb;

            // Ease-out cubic, matching the song-change entrances elsewhere.
            t = 1 - Math.Pow(1 - t, 3);
            return LerpRgb(_colorFromArgb, _colorToArgb, t);
        }

        private static int LerpRgb(int fromArgb, int toArgb, double t)
        {
            int fr = (fromArgb >> 16) & 0xFF, fg = (fromArgb >> 8) & 0xFF, fb = fromArgb & 0xFF;
            int tr = (toArgb >> 16) & 0xFF, tg = (toArgb >> 8) & 0xFF, tb = toArgb & 0xFF;
            int r = (int)Math.Round(fr + (tr - fr) * t);
            int g = (int)Math.Round(fg + (tg - fg) * t);
            int b = (int)Math.Round(fb + (tb - fb) * t);
            return (r << 16) | (g << 8) | b;
        }

        /// <summary>
        /// Draws bars whose rect or color changed since the last frame, clearing only
        /// their old+new area. Returns whether anything changed plus the bounding box
        /// of all touched pixels for a single dirty rect.
        /// </summary>
        private unsafe bool DrawBars(int stride, Span<byte> buffer, out int dirtyX, out int dirtyY, out int dirtyW, out int dirtyH)
        {
            dirtyX = dirtyY = dirtyW = dirtyH = 0;

            // Resolve brush once
            SolidColorBrush brush = AlbumAccent.Brush;

            int targetArgb = (brush.Color.R << 16) | (brush.Color.G << 8) | brush.Color.B;
            // Smooth accent transition instead of an instant snap (see ResolveBarColor).
            int argb = ResolveBarColor(targetArgb);
            byte b = (byte)(argb & 0xFF);
            byte g = (byte)((argb >> 8) & 0xFF);
            byte r = (byte)((argb >> 16) & 0xFF);

            bool centeredBars = _opts.CenteredBars();
            int barBaseline = _opts.Baseline() ? 4 : 0;

            int centerY = ImageHeight / 2;

            // Horizontal layout
            ComputeLayout(ImageWidth, _barCount, BarSpacing,
                out int barWidth,
                out int offsetX);

            // Radius
            float baseRadius = GetCornerRadius();

            // AA constants
            const float aa = 1.25f;
            float invAA = 1f / aa;

            int count = Math.Min(_barCount, _barValues?.Length ?? 0);

            // Bar count (thus x positions and widths) changed: old pixels sit at stale
            // spots, so clear everything once and redraw all.
            if (_prevBarY.Length != count || _prevBarEndY.Length != count)
            {
                buffer.Clear();
                _prevBarY = new int[count];
                _prevBarEndY = new int[count];
                _drawnArgb = -1; // force a full repaint below
            }

            bool colorChanged = argb != _drawnArgb;
            _drawnArgb = argb;

            int minX = ImageWidth, minY = ImageHeight, maxX = 0, maxY = 0;

            for (int i = 0; i < count; i++)
            {
                int barX = offsetX + i * (barWidth + BarSpacing);

                int barHeight = GetBarHeight(_barValues[i], barBaseline);

                ComputeVertical(centeredBars, centerY, barHeight, out int barY, out int barEndY);

                int prevY = _prevBarY[i];
                int prevEndY = _prevBarEndY[i];

                if (!colorChanged && barY == prevY && barEndY == prevEndY)
                    continue;

                int clearTop = Math.Min(barY, prevY);
                int clearBottom = Math.Max(barEndY, prevEndY);
                ClearRect(buffer, stride, barX, clearTop, barWidth, clearBottom - clearTop);

                if (barHeight > 0)
                {
                    // Clamp radius per bar
                    float radius = ClampRadius(baseRadius, barWidth, barHeight);
                    float radiusSq = radius * radius;

                    RasterizeBar(
                        buffer, stride,
                        barX, barWidth,
                        barY, barEndY,
                        centeredBars,
                        radius, radiusSq, invAA,
                        b, g, r);
                }

                _prevBarY[i] = barY;
                _prevBarEndY[i] = barEndY;

                if (barX < minX) minX = barX;
                if (clearTop < minY) minY = clearTop;
                if (barX + barWidth > maxX) maxX = barX + barWidth;
                if (clearBottom > maxY) maxY = clearBottom;
            }

            if (maxX <= minX || maxY <= minY)
                return false;

            dirtyX = Math.Max(minX, 0);
            dirtyY = Math.Max(minY, 0);
            dirtyW = Math.Min(maxX, ImageWidth) - dirtyX;
            dirtyH = Math.Min(maxY, ImageHeight) - dirtyY;
            return dirtyW > 0 && dirtyH > 0;
        }

        /// <summary>
        /// Zeroes a pixel rect (clamped to the bitmap), used to erase a bar's old
        /// position before redrawing it at its new height.
        /// </summary>
        private void ClearRect(Span<byte> buffer, int stride, int x, int y, int w, int h)
        {
            int x0 = Math.Max(x, 0);
            int x1 = Math.Min(x + w, ImageWidth);
            int y0 = Math.Max(y, 0);
            int y1 = Math.Min(y + h, ImageHeight);
            if (x1 <= x0 || y1 <= y0)
                return;

            int rowBytes = (x1 - x0) << 2;
            int rowStart = (x0 << 2);
            for (int row = y0; row < y1; row++)
                buffer.Slice(row * stride + rowStart, rowBytes).Clear();
        }

        private static void ComputeLayout(
            int imageWidth,
            int barCount,
            int spacing,
            out int barWidth,
            out int offsetX)
        {
            int totalSpacing = (barCount - 1) * spacing;

            int availableWidth = imageWidth - totalSpacing - 1;

            barWidth = availableWidth / barCount;

            int usedWidth = barWidth * barCount + totalSpacing;

            // Center safely
            offsetX = (imageWidth - usedWidth) >> 1;
        }

        private void ComputeVertical(bool centered, int centerY, int height, out int y, out int endY)
        {
            if (centered)
            {
                int half = height >> 1; // faster than /2
                y = centerY - half;
                endY = centerY + half;
            }
            else
            {
                y = ImageHeight - height;
                endY = ImageHeight;
            }
        }

        private int GetBarHeight(float value, int baseline)
        {
            return Math.Max((int)(Math.Clamp(value, 0f, 1f) * ImageHeight), baseline);
        }

        private float GetCornerRadius()
        {
            return 6f / MathF.Max(1f, _barCount / 10f);
        }

        private static float ClampRadius(float r, int width, int height)
        {
            float max = MathF.Min(width, height) * 0.5f;
            return r > max ? max : r;
        }

        private unsafe void RasterizeBar(
            Span<byte> buffer,
            int stride,
            int barX,
            int barWidth,
            int barY,
            int barEndY,
            bool centeredBars,
            float radius,
            float radiusSq,
            float invAA,
            byte b, byte g, byte r)
        {
            float left = barX;
            float right = barX + barWidth;
            float top = barY;
            float bottom = barEndY;

            float innerLeft = left + radius;
            float innerRight = right - radius;
            float innerTop = top + radius;
            float innerBottom = bottom - radius;

            // Packed BGRA for the solid spans: identical bytes to WritePixel(..., 255).
            int packed = b | (g << 8) | (r << 16) | (255 << 24);

            int xEnd = barX + barWidth;
            int xs = Math.Max(barX, 0);
            int xe = Math.Min(xEnd, ImageWidth);
            // Integer span covered by the center fast path (same predicate as the
            // per-pixel version: x >= innerLeft && x <= innerRight).
            int solidXs = Math.Max(xs, (int)Math.Ceiling(innerLeft));
            int solidXe = Math.Min(xe, (int)Math.Floor(innerRight) + 1);

            fixed (byte* ptr = buffer)
            {
                for (int y = barY; y < barEndY && y < ImageHeight && y >= 0; y++)
                {
                    int* row32 = (int*)(ptr + y * stride);

                    // Fully straight rows (sides / flat bottom): the whole span is
                    // solid, no corner math at all.
                    if ((y >= innerTop && y <= innerBottom) || (!centeredBars && y >= innerBottom))
                    {
                        for (int x = xs; x < xe; x++)
                            row32[x] = packed;
                        continue;
                    }

                    // Corner row: bulk-fill the straight middle, SDF only the edges.
                    for (int x = solidXs; x < solidXe; x++)
                        row32[x] = packed;

                    for (int x = xs; x < solidXs; x++)
                        WriteCornerPixel(buffer, stride, x, y, innerLeft, innerRight, innerTop, innerBottom,
                            radius, radiusSq, invAA, b, g, r);
                    for (int x = solidXe; x < xe; x++)
                        WriteCornerPixel(buffer, stride, x, y, innerLeft, innerRight, innerTop, innerBottom,
                            radius, radiusSq, invAA, b, g, r);
                }
            }
        }

        private static void WriteCornerPixel(
            Span<byte> buffer,
            int stride,
            int x,
            int y,
            float innerLeft,
            float innerRight,
            float innerTop,
            float innerBottom,
            float radius,
            float radiusSq,
            float invAA,
            byte b, byte g, byte r)
        {
            // CORNERS (same SDF math as before, pixel-identical output)
            float cx = x < innerLeft ? innerLeft : (x > innerRight ? innerRight : x);
            float cy = y < innerTop ? innerTop : (y > innerBottom ? innerBottom : y);

            float dx = x - cx;
            float dy = y - cy;

            float distSq = dx * dx + dy * dy;
            float sdf = (distSq - radiusSq) / (2f * radius);

            float alpha = 0.5f - sdf * invAA;

            if (alpha <= 0f)
                return;

            if (alpha > 1f) alpha = 1f;

            int index = y * stride + (x << 2); // x * 4 (bitshift faster)
            if (index + 3 >= buffer.Length)
                return;

            WritePixel(buffer, index, b, g, r, (byte)(255 * alpha));
        }

        private static void WritePixel(Span<byte> buffer, int index, byte b, byte g, byte r, byte a)
        {
            buffer[index] = b;
            buffer[index + 1] = g;
            buffer[index + 2] = r;
            buffer[index + 3] = a;
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                Logger.Error(e.Exception, "Visualizer recording stopped due to an error");
                RequestRestart("recording stopped with error");
            }
            else if (_isRunning && _opts.Enabled())
            {
                // Unexpected stop without an error (e.g. the endpoint reconfigured
                // when the playback app changed format between tracks or sources).
                // Our own Stop() detaches this handler first, so any call arriving
                // here means the capture died on its own: revive it, otherwise the
                // equalizer stays dead until a device event or manual toggle.
                RequestRestart("recording stopped unexpectedly");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();

            StopRenderLoop();

            AudioDeviceMonitor.Instance.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            TryUnregisterSystemEvents();

            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            GC.SuppressFinalize(this);
        }
    }
}
