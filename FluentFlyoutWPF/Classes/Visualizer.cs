// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyout.Windows;
using FluentFlyoutWPF.Classes.Utils;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FluentFlyoutWPF.Classes
{
    public class Visualizer : IDisposable
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public static int BarCount = 10;
        private readonly int ImageWidth = 76 * 3;
        private readonly int ImageHeight = 32 * 3;
        private readonly int BarSpacing = 2 * 3;

        private WasapiLoopbackCapture? _capture;
        private MMDevice? _renderDevice;
        private static float[]? _barValues;
        private static float[]? _targetValues;
        private WriteableBitmap? _bitmap;
        private bool _isRunning;
        private readonly object _lock = new();

        // Last drawn rect per bar, so static bars are skipped entirely instead of
        // re-rasterized every frame. Only changed bars are cleared, redrawn and
        // marked dirty. Reset (empty) whenever the bar count changes.
        private int[] _prevBarY = [];
        private int[] _prevBarEndY = [];
        private int _prevBarsArgb = -1;

        private readonly int _fftLength = 4096;
        private const int FftHop = 256; // overlapping FFT hop for smooth high-refresh updates
        private const int FftOrder = 12; // log2(_fftLength); hoisted out of the per-FFT path
        private int _fftPos = 0;
        private int _samplesSinceFft = 0;
        private readonly Complex[] _fftBuffer;
        private readonly Complex[] _fftWork;

        // Precomputed Hamming window: the previous code evaluated
        // FastFourierTransform.HammingWindow(j) (a cosine) 4096 times per FFT, up to
        // ~190 FFTs/s on the capture thread in high-refresh mode. Same values, ~0 cost.
        private readonly float[] _windowTable;

        // Precomputed per-bar FFT bin ranges + high-frequency boost. Rebuilt only when
        // (bar count, sample rate, sensitivity, peak) changes; the per-FFT path then does
        // no Math.Pow / Math.Log at all.
        private struct BandRange
        {
            public int StartBin;
            public int EndBin;
            public float Boost;
        }
        private BandRange[] _bandTable = [];
        private int _bandKeyBars;
        private int _bandKeyRate;
        private int _bandKeySens;
        private int _bandKeyPeak;
        private float _bandMinDb;
        private float _bandMaxDb;

        // FFT hop cached alongside the render-loop mode: read on every audio callback,
        // so it must not go through SettingsManager per callback.
        private int _fftHop;

        private System.Timers.Timer? _captureWatchdog;
        private DateTime _lastDataAvailableUtc = DateTime.MinValue;
        private int _restartInProgress; // 0=false, 1=true (Interlocked)
        private string? _deviceId; // track current device ID for restart logic

        // Render loop, driven either by CompositionTarget.Rendering (monitor refresh rate)
        // or by a 30 FPS DispatcherTimer when high refresh rate is disabled.
        private DispatcherTimer? _renderTimer;
        private volatile bool _renderLoopActive;
        private int _renderLoopRequested; // 0=false, 1=true (Interlocked)
        private readonly Stopwatch _renderStopwatch = new();
        private double _lastRenderTime;
        private bool _lastHasContent;
        private int _monitorRefreshRate;

        // Frame-rate independent attack/release smoothing (seconds). Driven by the
        // TaskbarVisualizerSmoothing setting (0 = snappy, 100 = silky); resolved once
        // per frame in EnsureSmoothing, never per bar.
        private double _attackSeconds = 0.036;
        private double _releaseSeconds = 0.49;
        private float _targetAlpha = 0.575f;
        private int _smoothingKey = -1;

        private readonly struct BarGeometry
        {
            public readonly float Left, Right, Top, Bottom;
            public readonly float InnerLeft, InnerRight, InnerTop, InnerBottom;

            public BarGeometry(int x, int width, int y, int endY, float radius)
            {
                Left = x;
                Right = x + width;
                Top = y;
                Bottom = endY;

                InnerLeft = Left + radius;
                InnerRight = Right - radius;
                InnerTop = Top + radius;
                InnerBottom = Bottom - radius;
            }
        }

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

        public Visualizer()
        {
            InitializeBitmap();

            _fftBuffer = new Complex[_fftLength];
            _fftWork = new Complex[_fftLength];

            _windowTable = new float[_fftLength];
            for (int i = 0; i < _fftLength; i++)
                _windowTable[i] = (float)FastFourierTransform.HammingWindow(i, _fftLength);

            _fftHop = SettingsManager.Current.TaskbarVisualizerHighRefreshRate ? FftHop : _fftLength;

            ResizeBarList(SettingsManager.Current.TaskbarVisualizerBarCount);
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
            if (!SettingsManager.Current.TaskbarVisualizerEnabled)
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
            if (!SettingsManager.Current.TaskbarVisualizerEnabled)
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
            if (!SettingsManager.Current.TaskbarVisualizerEnabled)
                return;
            RequestRestart("default audio output device changed");
        }

        private void RequestRestart(string reason)
        {
            if (!SettingsManager.Current.TaskbarVisualizerEnabled)
                return;

            if (Interlocked.Exchange(ref _restartInProgress, 1) == 1)
                return;

            Logger.Info($"Restarting visualizer ({reason})");

            Task.Run(async () =>
            {
                try
                {
                    Stop();

                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        await Task.Delay(500);
                        Start();
                        if (_isRunning)
                            return;
                        Logger.Warn($"Visualizer restart attempt {attempt + 1} failed, retrying...");
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

        public static void ResizeBarList(int newBarCount)
        {
            BarCount = newBarCount;
            _barValues = new float[BarCount];
            _targetValues = new float[BarCount];
        }

        public void Start()
        {
            if (_isRunning)
                return;

            float barCount = BarCount >= 0 ? BarCount : 8;
            _barValues = new float[(int)barCount];
            _targetValues = new float[(int)barCount];
            _lastHasContent = false;
            _fftHop = SettingsManager.Current.TaskbarVisualizerHighRefreshRate ? FftHop : _fftLength;

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

                _capture = new WasapiLoopbackCapture(_renderDevice);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                _isRunning = true;
                _lastDataAvailableUtc = DateTime.UtcNow;

                // automatic update timer in case audio data is not updated
                _captureWatchdog = new(500)
                {
                    AutoReset = false
                };
                _captureWatchdog.Elapsed += (_, _) =>
                {
                    if (_isRunning)
                    {
                        // Zero the targets; the render loop (if active) draws the bars falling to zero.
                        if (_targetValues != null) Array.Clear(_targetValues, 0, _targetValues.Length);
                        if (_barValues != null) Array.Clear(_barValues, 0, _barValues.Length);

                        if (!SettingsManager.Current.TaskbarVisualizerBaseline || SettingsManager.Current.TaskbarVisualizerBaselineAutoHide) // if baseline is enabled and autohide is off, condition is false
                            SettingsManager.Current.TaskbarVisualizerHasContent = false;

                        // If we stop receiving loopback callbacks entirely (common after lock/unlock + device changes),
                        // the timer fires once and then never again. Use it as a recovery trigger.
                        var silenceFor = DateTime.UtcNow - _lastDataAvailableUtc;
                        if (silenceFor > TimeSpan.FromSeconds(2))
                        {
                            RequestRestart($"no audio callbacks for {silenceFor.TotalSeconds:0.0}s");
                        }
                    }
                };
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

            _isRunning = false;

            StopRenderLoop();

            _capture?.DataAvailable -= OnDataAvailable;
            _capture?.RecordingStopped -= OnRecordingStopped;
            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;

            _renderDevice?.Dispose();
            _renderDevice = null;

            _captureWatchdog?.Stop();
            _captureWatchdog?.Dispose();
            _captureWatchdog = null;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!_isRunning || e.BytesRecorded == 0)
                return;

            _lastDataAvailableUtc = DateTime.UtcNow;

            _captureWatchdog.Stop();
            _captureWatchdog.Start();

            int bytesPerSample = _capture!.WaveFormat.BitsPerSample / 8;
            int samplesRecorded = e.BytesRecorded / bytesPerSample;

            // In high-refresh mode use a small overlapping hop for frequent target updates.
            // In 30 FPS mode use a full-length hop to reproduce the original behavior.
            // Cached field: the audio callback must not read settings per callback.
            int hop = _fftHop;

            for (int i = 0; i < samplesRecorded; i++)
            {
                float sampleValue = 0;
                if (bytesPerSample == 4)
                {
                    sampleValue = BitConverter.ToSingle(e.Buffer, i * 4);
                }
                else if (bytesPerSample == 2)
                {
                    sampleValue = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                }

                _fftBuffer[_fftPos].X = sampleValue;
                _fftBuffer[_fftPos].Y = 0;
                _fftPos++;

                // Wrap around to keep a sliding window of the last _fftLength samples.
                if (_fftPos >= _fftLength)
                    _fftPos = 0;

                _samplesSinceFft++;
                if (_samplesSinceFft < hop)
                    continue;
                _samplesSinceFft = 0;

                // Copy the sliding window into the work buffer (in chronological order),
                // applying the precomputed Hamming window (no per-sample trig).
                for (int j = 0; j < _fftLength; j++)
                {
                    int src = _fftPos + j;
                    if (src >= _fftLength)
                        src -= _fftLength;
                    _fftWork[j].X = _fftBuffer[src].X * _windowTable[j];
                    _fftWork[j].Y = 0;
                }

                // perform FFT
                ProcessFftData();
            }

            // Wake up the render loop when there is content to display.
            bool hasContent = false;
            int targetCount = _targetValues?.Length ?? 0;
            for (int j = 0; j < Math.Min(BarCount, targetCount); j++)
            {
                if (_targetValues[j] > 0.01f)
                {
                    hasContent = true;
                    break;
                }
            }

            if (hasContent || (SettingsManager.Current.TaskbarVisualizerBaseline && !SettingsManager.Current.TaskbarVisualizerBaselineAutoHide))
            {
                EnsureRenderLoop();
                SettingsManager.Current.TaskbarVisualizerHasContent = true;
            }
        }

        private void ProcessFftData()
        {
            FastFourierTransform.FFT(true, FftOrder, _fftWork);

            int sampleRate = _capture.WaveFormat.SampleRate;
            EnsureBandTable(sampleRate);

            int count = Math.Min(Math.Min(BarCount, _bandTable.Length), _targetValues?.Length ?? 0);

            for (int i = 0; i < count; i++)
            {
                int startBin = _bandTable[i].StartBin;
                int endBin = _bandTable[i].EndBin;

                float maxAmplitude = 0;

                // Find max amplitude
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

                // Target-side EMA (audio thread): kills single-FFT spikes before they ever
                // reach the render thread. One multiply-add per bar per FFT — negligible
                // next to the FFT itself. Alpha comes from the smoothing setting.
                float prev = _targetValues[i];
                _targetValues[i] = prev + (intensity - prev) * _targetAlpha;
            }
        }

        /// <summary>
        /// Resolves the smoothing setting (0-100) into time constants once per frame.
        /// Slider feel: attack 12ms (instant punch) .. 60ms, release 80ms (lively) ..
        /// 900ms (slow melt). Target EMA alpha 0.9 (raw) .. 0.25 (heavy). Defaults
        /// (50) reproduce roughly the previous hardcoded 30ms / 350ms behaviour.
        /// </summary>
        private void EnsureSmoothing()
        {
            int s = SettingsManager.Current.TaskbarVisualizerSmoothing;
            if (s < 0) s = 0;
            else if (s > 100) s = 100;
            if (s == _smoothingKey)
                return;
            _smoothingKey = s;

            float t = s / 100f;
            _attackSeconds = 0.012 + t * 0.048;
            _releaseSeconds = 0.08 + t * 0.82;
            _targetAlpha = 0.9f - t * 0.65f;
        }

        /// <summary>
        /// Rebuilds the per-bar FFT bin ranges, boosts and dB range only when the inputs
        /// change. The per-FFT path above then performs zero Math.Pow / Math.Log calls.
        /// </summary>
        private void EnsureBandTable(int sampleRate)
        {
            int bars = BarCount;
            int sens = SettingsManager.Current.TaskbarVisualizerAudioSensitivity;
            int peak = SettingsManager.Current.TaskbarVisualizerAudioPeakLevel;

            if (_bandTable.Length == bars
                && _bandKeyBars == bars
                && _bandKeyRate == sampleRate
                && _bandKeySens == sens
                && _bandKeyPeak == peak)
                return;

            const double minFreq = 40;   // Hz
            const double maxFreq = 8000; // Hz
            double frequencyPerBin = (double)sampleRate / _fftLength;
            double ratio = maxFreq / minFreq;

            var table = new BandRange[Math.Max(bars, 0)];
            for (int i = 0; i < table.Length; i++)
            {
                double startFreq = minFreq * Math.Pow(ratio, (double)i / bars);
                double endFreq = minFreq * Math.Pow(ratio, (double)(i + 1) / bars);

                int startBin = (int)(startFreq / frequencyPerBin);
                int endBin = (int)(endFreq / frequencyPerBin);

                if (endBin <= startBin) endBin = startBin + 1;
                if (endBin >= _fftLength / 2) endBin = _fftLength / 2 - 1;
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

            if (SettingsManager.Current.TaskbarVisualizerHighRefreshRate)
            {
                if (_monitorRefreshRate <= 0)
                {
                    var taskbarWindow = Application.Current.Windows.OfType<TaskbarWindow>().FirstOrDefault();
                    if (taskbarWindow != null)
                    {
                        IntPtr hwnd = new WindowInteropHelper(taskbarWindow).Handle;
                        if (hwnd != IntPtr.Zero)
                            _monitorRefreshRate = MonitorUtil.GetRefreshRate(hwnd);
                    }
                    if (_monitorRefreshRate <= 0)
                        _monitorRefreshRate = MonitorUtil.GetRefreshRate();
                    if (_monitorRefreshRate <= 0)
                        _monitorRefreshRate = 60;
                }

                // CompositionTarget.Rendering fires once per composited frame, i.e. at the monitor's refresh rate.
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
                // The high-refresh toggle changes the audio-thread hop: refresh the cached
                // value together with the render loop so both switch atomically.
                _fftHop = SettingsManager.Current.TaskbarVisualizerHighRefreshRate ? FftHop : _fftLength;
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
            if (dt <= 0 || dt > 1.0)
                dt = 1.0 / 60.0;

            EnsureSmoothing();
            SmoothBars(dt);

            // check if bars are all zero
            bool allZero = true;
            for (int j = 0; j < Math.Min(BarCount, _barValues?.Length ?? 0); j++)
            {
                if (_barValues[j] > 0.01f)
                {
                    allZero = false;
                    break;
                }
            }

            bool forcedBaseline = SettingsManager.Current.TaskbarVisualizerBaseline && !SettingsManager.Current.TaskbarVisualizerBaselineAutoHide;

            if (allZero && !forcedBaseline)
            {
                // update bars if they have content
                if (_lastHasContent)
                {
                    _lastHasContent = false;
                    SettingsManager.Current.TaskbarVisualizerHasContent = false;
                }

                // draw one final empty frame, then stop the render loop to save CPU
                UpdateBitmap();
                StopRenderLoop();
                return;
            }

            if (!_lastHasContent)
            {
                _lastHasContent = true;
                SettingsManager.Current.TaskbarVisualizerHasContent = true;
            }

            UpdateBitmap();
        }

        // Frame-rate independent attack/release interpolation toward the audio thread's targets.
        private void SmoothBars(double dt)
        {
            if (_barValues == null || _targetValues == null)
                return;

            int count = Math.Min(BarCount, Math.Min(_barValues.Length, _targetValues.Length));

            float attackFactor = 1f - (float)Math.Exp(-dt / _attackSeconds);
            float releaseFactor = 1f - (float)Math.Exp(-dt / _releaseSeconds);

            for (int i = 0; i < count; i++)
            {
                float target = _targetValues[i];
                float current = _barValues[i];

                if (target > current)
                {
                    // Jump up quickly
                    _barValues[i] = current + (target - current) * attackFactor;
                }
                else
                {
                    // Fall down slowly
                    _barValues[i] = current + (target - current) * releaseFactor;
                }
            }
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
        /// Draws bars whose rect or color changed since the last frame, clearing only
        /// their old+new area. Returns whether anything changed plus the bounding box
        /// of all touched pixels for a single dirty rect.
        /// </summary>
        private unsafe bool DrawBars(int stride, Span<byte> buffer, out int dirtyX, out int dirtyY, out int dirtyW, out int dirtyH)
        {
            dirtyX = dirtyY = dirtyW = dirtyH = 0;

            // Resolve brush once 
            SolidColorBrush brush = AlbumAccent.Brush;

            byte b = brush.Color.B;
            byte g = brush.Color.G;
            byte r = brush.Color.R;
            int argb = (r << 16) | (g << 8) | b;

            bool centeredBars = SettingsManager.Current.TaskbarVisualizerCenteredBars;
            int barBaseline = SettingsManager.Current.TaskbarVisualizerBaseline ? 4 : 0;

            int centerY = ImageHeight / 2;

            // Horizontal layout 
            ComputeLayout(ImageWidth, BarCount, BarSpacing,
                out int barWidth,
                out int offsetX);

            // Radius 
            float baseRadius = GetCornerRadius();

            // AA constants 
            const float aa = 1.25f;
            float invAA = 1f / aa;

            int count = Math.Min(BarCount, _barValues?.Length ?? 0);

            // Bar count (thus x positions and widths) changed: old pixels sit at stale
            // spots, so clear everything once and redraw all.
            if (_prevBarY.Length != count || _prevBarEndY.Length != count)
            {
                buffer.Clear();
                _prevBarY = new int[count];
                _prevBarEndY = new int[count];
                _prevBarsArgb = argb;
            }

            bool colorChanged = argb != _prevBarsArgb;
            _prevBarsArgb = argb;

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
        private static float GetCornerRadius()
        {
            return 6f / MathF.Max(1f, SettingsManager.Current.TaskbarVisualizerBarCount / 10f);
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

            for (int y = barY; y < barEndY && y < ImageHeight && y >= 0; y++)
            {
                int row = y * stride;

                for (int x = barX; x < barX + barWidth && x < ImageWidth; x++)
                {
                    int index = row + (x << 2); // x * 4 (bitshift faster)
                    if (index + 3 >= buffer.Length)
                        continue;

                    // CENTER
                    if (x >= innerLeft && x <= innerRight)
                    {
                        WritePixel(buffer, index, b, g, r, 255);
                        continue;
                    }

                    // SIDES
                    if (y >= innerTop && y <= innerBottom)
                    {
                        WritePixel(buffer, index, b, g, r, 255);
                        continue;
                    }

                    // FLAT BOTTOM
                    if (!centeredBars && y >= innerBottom)
                    {
                        WritePixel(buffer, index, b, g, r, 255);
                        continue;
                    }

                    // CORNERS
                    float cx = x < innerLeft ? innerLeft : (x > innerRight ? innerRight : x);
                    float cy = y < innerTop ? innerTop : (y > innerBottom ? innerBottom : y);

                    float dx = x - cx;
                    float dy = y - cy;

                    float distSq = dx * dx + dy * dy;
                    float sdf = (distSq - radiusSq) / (2f * radius);

                    float alpha = 0.5f - sdf * invAA;

                    if (alpha <= 0f)
                        continue;

                    if (alpha > 1f) alpha = 1f;

                    WritePixel(buffer, index, b, g, r, (byte)(255 * alpha));
                }
            }
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
            }
        }

        public void Dispose()
        {
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
