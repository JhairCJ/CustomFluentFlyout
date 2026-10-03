// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using NAudio.Wave;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>Dictation state: what the Island draws (spec 006 RF-1/RF-6).</summary>
public enum DictationPhase
{
    /// <summary>Idle: neither microphone nor transcription.</summary>
    Idle,

    /// <summary>Recording: the Island shows the mic and the waves.</summary>
    Listening,

    /// <summary>Recording closed, transcribing.</summary>
    Transcribing,

    /// <summary>Something failed (no microphone, no model): the Island says so for a few seconds.</summary>
    Error,
}

/// <summary>
/// LOCAL voice dictation (spec 006): hold the hotkey, speak, release, and the text
/// appears wherever the cursor is.
/// <para>The engine is whisper.cpp (Whisper.net) with a ggml model from disk: inference
/// never leaves the machine - the network is only used to download the models. Capture
/// goes through <see cref="WaveIn"/> at 16 kHz mono, Whisper's native format, and the
/// text is injected with Unicode <c>SendInput</c> (without touching the clipboard).</para>
///
/// <para>This object knows nothing about the Island: it exposes phase, level and
/// message, and whoever draws it (IslandWindow.Dictation.cs) subscribes to
/// <see cref="Changed"/>.</para>
/// </summary>
public sealed class DictationService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly MicCapture _capture = new();
    private readonly ExternalAsrTranscriber _externalTranscriber = new();
    private readonly HashSet<int> _pressed = [];
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly SemaphoreSlim _vadLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Lock _transcriptionGate = new();
    private readonly Lock _resourceStateGate = new();
    private readonly SemaphoreSlim _resourcePolicyLock = new(1, 1);

    private WhisperFactory? _factory;
    private string? _factoryPath;
    private bool _factoryUseGpu;
    private WhisperVadFactory? _vadFactory;
    private string? _vadFactoryPath;
    private CancellationTokenSource? _transcriptionCts;
    private bool _cancelRequested;
    private bool _runtimeConfigured;
    private bool _runtimeUseGpu;
    private bool _runtimeLoaded;
    private string _runtimeInfo = "";
    private CancellationTokenSource? _resourceReleaseCts;
    private CancellationTokenSource? _resourcePolicyCts;
    private bool _resourcePolicyPending;
    private bool _resourceSessionStarting;
    private volatile bool _disposed;
    private volatile bool _runtimeMaintenance;

    public DictationPhase Phase { get; private set; } = DictationPhase.Idle;

    /// <summary>Is dictation running (recording or transcribing)?</summary>
    public bool Active => Phase is DictationPhase.Listening or DictationPhase.Transcribing;

    /// <summary>
    /// The user is defining the hotkey in Settings: the keys pressed in that box are not
    /// dictation, so they open no microphone and cancel nothing. The dictation page sets
    /// this while its capture box has focus.
    /// </summary>
    public bool HotkeyCaptureActive { get; set; }

    /// <summary>Microphone level right now (0..1), for the wave visualizer.</summary>
    public float Level => _capture.Level;

    /// <summary>Localization key of the current message (error or notice); null if none.</summary>
    public string? MessageKey { get; private set; }

    /// <summary>English fallback text of the current message.</summary>
    public string? MessageFallback { get; private set; }

    /// <summary>Whether Whisper has already loaded a native runtime.</summary>
    public bool RuntimeLoaded => _runtimeLoaded;

    /// <summary>Whether the loaded native runtime is CUDA.</summary>
    public bool UsingGpuRuntime => RuntimeOptions.LoadedLibrary is RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12;

    /// <summary>Information about the native runtime Whisper.NET is using.</summary>
    public string RuntimeInfo => _runtimeInfo;

    /// <summary>CrispASR's model is kept by its worker, independently of Whisper CUDA.</summary>
    public bool CrispModelLoaded => _externalTranscriber.CrispRuntimeLoaded;

    public bool CrispUsingGpu => CrispModelLoaded && _externalTranscriber.UsingGpu;

    public DictationDevice RequestedDevice =>
        SettingsManager.Current.GetDictationDevice(SettingsManager.Current.DictationModel);

    public DictationCudaVersion RequestedCudaVersion =>
        SettingsManager.Current.GetDictationCudaVersion(SettingsManager.Current.DictationModel);

    public DictationCudaVersion LoadedCudaVersion => _externalTranscriber.LoadedCudaVersion;

    private bool EffectiveWhisperUseGpu => RequestedDevice == DictationDevice.DedicatedGpu
        && (!_runtimeLoaded || UsingGpuRuntime);

    public bool ModelLoaded
    {
        get
        {
            string? path = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel);
            return path != null && ((_factory != null && string.Equals(path, _factoryPath, StringComparison.OrdinalIgnoreCase))
                || string.Equals(path, _externalTranscriber.LoadedModelPath, StringComparison.OrdinalIgnoreCase));
        }
    }
    public bool ModelUsingGpu => ModelLoaded && (_factory != null ? _factoryUseGpu : _externalTranscriber.UsingGpu);
    public DictationDevice LoadedDevice => _factory != null
        ? (_factoryUseGpu ? DictationDevice.DedicatedGpu : DictationDevice.Cpu)
        : _externalTranscriber.LoadedDevice;
    public string? LoadedDeviceName => ModelLoaded ? _externalTranscriber.LoadedDeviceName : null;

    /// <summary>
    /// The native library is global to the process. A loaded CPU-only library needs
    /// a restart to enable CUDA; a CUDA library can create either CPU or GPU contexts.
    /// </summary>
    public bool AccelerationRestartRequired =>
        _runtimeLoaded && !UsingGpuRuntime && RequestedDevice == DictationDevice.DedicatedGpu
        && (DictationModelStore.Find(DictationDevices.ModelKey(SettingsManager.Current.DictationModel))?.Backend
            ?? DictationModelBackend.Whisper) == DictationModelBackend.Whisper;

    /// <summary>Raised on any phase change, message level change, or end of dictation.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------
    // Hotkey (driven by MainWindow's keyboard hook)
    // ------------------------------------------------------------------

    /// <summary>
    /// A key from the global hook. It keeps the set of held keys - the hook sees ALL of
    /// them, whether the app has focus or not - and decides with the pure hotkey logic:
    /// completing it starts, releasing any of its keys ends it, and only Escape cancels
    /// (RF-1/RF-3/RF-4). Any other foreign key is ignored, so brushing the desk with
    /// your hand never discards the phrase being dictated.
    /// </summary>
    public void HandleKey(int virtualKey, bool down, bool injected = false)
    {
        // An injected event (the text we type ourselves, an on-screen keyboard, a macro) is
        // not a key from the user and dictation must not see it. Without this an injected
        // Enter - the line break of a transcription - cancelled the very session writing
        // it. Unicode keys also carry no virtual code, so they never count for the hotkey.
        if (_disposed || injected || virtualKey == 0) return;
        // Defining the hotkey in Settings is not dictating: those keys start and cut nothing.
        if (HotkeyCaptureActive) return;
        // The hook delivers the physical modifier (0xA2 for left Ctrl) while the saved
        // hotkey says "Ctrl": they are unified before anything is compared.
        virtualKey = DictationHotkey.Normalize(virtualKey);

        var hotkey = DictationHotkey.Parse(SettingsManager.Current.DictationHotkey);

        if (down)
        {
            _pressed.Add(virtualKey);
            if (Active)
            {
                if (DictationHotkey.Cancels(virtualKey)) Cancel();
                return;
            }
            if (DictationHotkey.Triggers(hotkey, virtualKey, _pressed)) Start();
            return;
        }

        _pressed.Remove(virtualKey);
        if (Phase == DictationPhase.Listening && DictationHotkey.Ends(hotkey, virtualKey)) Stop();
    }

    // ------------------------------------------------------------------
    // Dictation lifecycle
    // ------------------------------------------------------------------

    /// <summary>Opens the microphone and presents the Island card (RF-1).</summary>
    public void Start()
    {
        if (_disposed || Active) return;

        string? modelPath = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel);
        if (modelPath == null)
        {
            Fail("IslandDictationNoModel", "Download a speech model in Settings → Dictation");
            return;
        }

        lock (_resourceStateGate)
        {
            if (_runtimeMaintenance) return;
            CancelResourceReleaseTimerUnsafe();
            _resourceSessionStarting = true;
        }

        // The microphone is opened HERE and not on a separate thread on purpose: the
        // keyboard hook has a ~300 ms deadline and opening the device takes a few tens of
        // ms, while deferring it would open the door to the key being released before the
        // recording exists. Transcription, which is the slow part, runs on its own thread.
        try
        {
            _capture.Start();
        }
        catch (Exception ex)
        {
            lock (_resourceStateGate) _resourceSessionStarting = false;
            Logger.Warn(ex, "No se pudo abrir el micrófono");
            Fail("IslandDictationNoMic", "No microphone available");
            ScheduleResourceRelease();
            return;
        }

        _cancelRequested = false;
        SetPhase(DictationPhase.Listening);
        Logger.Info("Dictado: grabación iniciada; precarga en segundo plano");
        _ = Task.Run(PrefetchEngineAsync);
        lock (_resourceStateGate) _resourceSessionStarting = false;
    }

    /// <summary>
    /// Budget for the background warm-up started when dictation begins. It only has to
    /// be larger than the slowest thing it does — a CUDA model load, or an external
    /// worker booting its HTTP server — otherwise the prefetch would be cancelled while
    /// it is still worth waiting for and the next dictation would pay for it again.
    /// </summary>
    private static readonly TimeSpan PrefetchBudget = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Loads the engine while the user is still speaking: with CUDA that is seconds, so
    /// by the time the hotkey comes back everything is warm and the icon leaves early.
    /// Touches neither the phase nor the mic. It also warms the engine with one silent
    /// inference (some backends only allocate their contexts and buffers on the first
    /// real pass) and makes sure the Silero VAD model is on disk, so neither the network
    /// nor that first-pass allocation ever lands on the hot path. Failures and
    /// cancellations are not fatal: the transcription loads whatever is missing itself.
    /// </summary>
    private async Task PrefetchEngineAsync()
    {
        try
        {
            if (_disposed || !SettingsManager.Current.DictationEnabled) return;
            string? path = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel);
            if (path == null) return;
            DictationModelInfo? model = DictationModelStore.Find(Path.GetFileName(path));
            var preloadClock = Stopwatch.StartNew();
            Logger.Info($"Dictado: precarga iniciada para {Path.GetFileName(path)}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            cts.CancelAfter(PrefetchBudget);
            if (model is { Backend: not DictationModelBackend.Whisper })
            {
                await DictationModelStore.ValidateIntegrityAsync(path, cts.Token);
                await _externalTranscriber.EnsureLoadedAsync(
                    model, path, RequestedDevice, cts.Token, RequestedCudaVersion);
            }
            else
            {
                WhisperFactory factory = await EnsureFactoryAsync(cts.Token);
                await WarmUpAsync(factory, cts.Token);
            }

            Logger.Info($"Dictado: modelo listo tras {preloadClock.ElapsedMilliseconds} ms de precarga "
                + $"({Path.GetFileName(path)}, fase {Phase})");
            Changed?.Invoke();

            // The gate skips the neural VAD unless the level is ambiguous, but when it
            // does need it the model has to be there: fetching it here keeps the network
            // off the hot path instead of stalling the first dictation of the session.
            await EnsureVadFactoryAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The session ended before the engine was warm: the transcription loads it.
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Dictation engine prefetch failed; it will be loaded on transcription");
        }
    }

    /// <summary>Closes the microphone, drops the audio and writes nothing (RF-4).</summary>
    public void Cancel()
    {
        if (Phase == DictationPhase.Listening)
        {
            _cancelRequested = true;
            Stop();
            return;
        }

        if (Phase == DictationPhase.Transcribing)
        {
            CancelTranscription();
            SetPhase(DictationPhase.Idle);
        }
    }

    /// <summary>Closes the microphone and transcribes what was captured, if there is voice (RF-3/RF-5).</summary>
    public void Stop()
    {
        if (Phase != DictationPhase.Listening) return;

        float[] samples = _capture.Stop();
        bool cancelled = _cancelRequested;
        _cancelRequested = false;

        if (cancelled || samples.Length == 0)
        {
            SetPhase(DictationPhase.Idle);
            FinishSessionWithoutTranscription();
            return;
        }

        SetPhase(DictationPhase.Transcribing);
        Logger.Info("Dictado: grabación finalizada; comienza la transcripción");
        string language = SettingsManager.Current.DictationLanguage;
        var sessionCts = new CancellationTokenSource();
        lock (_transcriptionGate)
        {
            CancellationTokenSource? previousCts = Interlocked.Exchange(ref _transcriptionCts, sessionCts);
            previousCts?.Cancel();
        }
        _ = Task.Run(() => TranscribeAndSendAsync(samples, language, sessionCts));
    }

    /// <summary>
    /// Applies the current loading policy. Kept as a compatibility point for startup
    /// and for configuration changes.
    /// </summary>
    public void Preload()
    {
        RefreshResourcePolicy();
    }

    /// <summary>Releases locked runtime files and suspends preloading/new recordings during removal.</summary>
    public async Task RunRuntimeMaintenanceAsync(Func<Task> action)
    {
        lock (_resourceStateGate)
        {
            if (_disposed || IsResourceBusyUnsafe())
                throw new InvalidOperationException("Finish the current dictation before managing runtimes");
            _runtimeMaintenance = true;
            CancelResourceReleaseTimerUnsafe();
            CancelResourcePolicyUnsafe();
        }
        bool lockTaken = false;
        try
        {
            await _resourcePolicyLock.WaitAsync();
            lockTaken = true;
            await ReleaseLoadedResourcesCoreAsync();
            await action();
        }
        finally
        {
            if (lockTaken) _resourcePolicyLock.Release();
            lock (_resourceStateGate) _runtimeMaintenance = false;
            RefreshResourcePolicy();
        }
    }

    /// <summary>
    /// Reapplies the loading mode and selected model. A matching model is retained;
    /// automatic mode starts its inactivity timer, and keep-loaded mode cancels it.
    /// </summary>
    public void RefreshResourcePolicy()
    {
        if (_disposed) return;

        CancellationTokenSource? work;
        lock (_resourceStateGate)
        {
            CancelResourceReleaseTimerUnsafe();
            CancelResourcePolicyUnsafe();
            if (IsResourceBusyUnsafe())
            {
                _resourcePolicyPending = true;
                return;
            }

            _resourcePolicyPending = false;
            work = new CancellationTokenSource();
            _resourcePolicyCts = work;
        }

        _ = Task.Run(() => ApplyResourcePolicyAsync(work));
    }

    /// <summary>Restarts the release timer with the new slider value.</summary>
    public void RefreshResourceTimeout()
    {
        if (_disposed) return;

        bool schedule;
        lock (_resourceStateGate)
        {
            CancelResourceReleaseTimerUnsafe();
            schedule = !SettingsManager.Current.DictationKeepModelLoaded
                && !IsResourceBusyUnsafe()
                && HasLoadedResourcesUnsafe();
        }

        if (schedule) ScheduleResourceRelease();
    }

    /// <summary>Hot setting change: if dictation is no longer enabled, it is cut on the spot.</summary>
    public void RefreshSettings()
    {
        if (!SettingsManager.Current.DictationEnabled && Active)
        {
            _cancelRequested = true;
            if (Phase == DictationPhase.Listening) Stop();
            else
            {
                CancelTranscription();
                SetPhase(DictationPhase.Idle);
            }
        }

        RefreshResourcePolicy();
    }

    /// <summary>
    /// Applies the selected model's device and refreshes the UI's effective runtime status.
    /// </summary>
    public void RefreshAccelerationSettings()
    {
        if (_disposed) return;
        if (_runtimeConfigured && !_runtimeLoaded)
        {
            _runtimeUseGpu = RequestedDevice == DictationDevice.DedicatedGpu;
            ApplyRuntimeLibraryOrder(_runtimeUseGpu);
        }
        // Reload the model context, not the native DLL. CUDA can also run CPU contexts;
        // a CPU-only library needs a restart before it can offer CUDA.
        RefreshResourcePolicy();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_resourceStateGate)
        {
            CancelResourceReleaseTimerUnsafe();
            CancelResourcePolicyUnsafe();
            _resourcePolicyPending = false;
            _resourceSessionStarting = false;
        }
        _lifetimeCts.Cancel();
        CancelTranscription();
        _capture.Dispose();
        _externalTranscriber.Dispose();
        _vadFactory?.Dispose();
        _vadFactory = null;
        _engineLock.Dispose();
        _vadLock.Dispose();
        _factory?.Dispose();
        _factory = null;
        _lifetimeCts.Dispose();
        // Clean shutdown with CUDA loaded: that is not a failure, the notice is withdrawn.
        DictationGpuSafety.Disarm();
    }

    private bool IsResourceBusyUnsafe() =>
        _runtimeMaintenance
        || _resourceSessionStarting
        || Active
        || Volatile.Read(ref _transcriptionCts) != null;

    private bool HasLoadedResourcesUnsafe() =>
        _factory != null
        || _vadFactory != null
        || _externalTranscriber.RuntimeLoaded;

    private void CancelResourceReleaseTimerUnsafe()
    {
        CancellationTokenSource? cts = _resourceReleaseCts;
        _resourceReleaseCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The timer finished right before it could be cancelled.
        }
    }

    private void CancelResourcePolicyUnsafe()
    {
        CancellationTokenSource? cts = _resourcePolicyCts;
        _resourcePolicyCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The transition finished right before it could be cancelled.
        }
    }

    private void FinishSessionWithoutTranscription()
    {
        bool policyPending;
        lock (_resourceStateGate)
        {
            policyPending = _resourcePolicyPending;
            _resourcePolicyPending = false;
        }

        if (policyPending) RefreshResourcePolicy();
        else if (!SettingsManager.Current.DictationKeepModelLoaded) ScheduleResourceRelease();
    }

    private async Task ApplyResourcePolicyAsync(CancellationTokenSource work)
    {
        bool lockTaken = false;
        try
        {
            await _resourcePolicyLock.WaitAsync(work.Token);
            lockTaken = true;
            if (_disposed || work.IsCancellationRequested) return;

            lock (_resourceStateGate)
            {
                if (IsResourceBusyUnsafe())
                {
                    _resourcePolicyPending = true;
                    return;
                }
            }

            string? activePath = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel);
            if (SettingsManager.Current.DictationEnabled && activePath != null
                && ((string.Equals(_factoryPath, activePath, StringComparison.OrdinalIgnoreCase)
                        && _factoryUseGpu == EffectiveWhisperUseGpu)
                    || _externalTranscriber.MatchesLoadedModel(activePath, RequestedDevice, RequestedCudaVersion)))
            {
                // Toggling Keep Model Loaded must not unload/reload the same weights.
                if (!SettingsManager.Current.DictationKeepModelLoaded) ScheduleResourceRelease();
                return;
            }

            // A cancelled policy still releases what it already owned. The next policy
            // transition will then load the newly selected backend if necessary.
            await ReleaseLoadedResourcesCoreAsync();
            if (_disposed || work.IsCancellationRequested) return;

            if (!SettingsManager.Current.DictationEnabled
                || !SettingsManager.Current.DictationKeepModelLoaded)
                return;

            lock (_resourceStateGate)
            {
                if (IsResourceBusyUnsafe())
                {
                    _resourcePolicyPending = true;
                    return;
                }
            }

            await LoadResourcesCoreAsync(work.Token);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested || _disposed)
        {
            // A model or policy change invalidates this transition.
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo aplicar la política de carga del dictado");
        }
        finally
        {
            if (lockTaken) _resourcePolicyLock.Release();
            lock (_resourceStateGate)
            {
                if (ReferenceEquals(_resourcePolicyCts, work)) _resourcePolicyCts = null;
            }
            work.Dispose();
        }
    }

    private async Task LoadResourcesCoreAsync(CancellationToken cancellationToken)
    {
        string? modelPath = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel);
        if (modelPath == null) return;

        await EnsureVadFactoryCoreAsync(cancellationToken);
        DictationModelInfo? model = DictationModelStore.Find(Path.GetFileName(modelPath));
        if (model is { Backend: not DictationModelBackend.Whisper })
        {
            await DictationModelStore.ValidateIntegrityAsync(modelPath, cancellationToken);
            await _externalTranscriber.EnsureLoadedAsync(
                model,
                modelPath,
                RequestedDevice,
                cancellationToken, RequestedCudaVersion);
        }
        else
        {
            WhisperFactory factory = await EnsureFactoryCoreAsync(cancellationToken);
            await WarmUpAsync(factory, cancellationToken);
        }

        Logger.Info($"Dictado: recursos preparados para {Path.GetFileName(modelPath)}");
        Changed?.Invoke();
    }

    private async Task ReleaseLoadedResourcesCoreAsync()
    {
        try
        {
            await _externalTranscriber.ReleaseLoadedResourcesAsync();

            await _engineLock.WaitAsync();
            try
            {
                _factory?.Dispose();
                _factory = null;
                _factoryPath = null;
            }
            finally
            {
                _engineLock.Release();
            }

            await _vadLock.WaitAsync();
            try
            {
                _vadFactory?.Dispose();
                _vadFactory = null;
                _vadFactoryPath = null;
            }
            finally
            {
                _vadLock.Release();
            }

            Logger.Info("Dictado: pesos, contextos, VAD y workers liberados");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudieron liberar todos los recursos del dictado");
        }
    }

    private void ScheduleResourceRelease()
    {
        if (_disposed || SettingsManager.Current.DictationKeepModelLoaded) return;

        CancellationTokenSource? timer;
        int delaySeconds = Math.Clamp(
            SettingsManager.Current.DictationUnloadDelaySeconds,
            15,
            600);
        lock (_resourceStateGate)
        {
            CancelResourceReleaseTimerUnsafe();
            if (IsResourceBusyUnsafe() || !HasLoadedResourcesUnsafe()) return;
            timer = new CancellationTokenSource();
            _resourceReleaseCts = timer;
        }

        _ = Task.Run(() => ReleaseWhenIdleAsync(timer, TimeSpan.FromSeconds(delaySeconds)));
    }

    private async Task ReleaseWhenIdleAsync(
        CancellationTokenSource timer,
        TimeSpan delay)
    {
        bool lockTaken = false;
        try
        {
            await Task.Delay(delay, timer.Token);
            if (_disposed || timer.IsCancellationRequested) return;

            await _resourcePolicyLock.WaitAsync(timer.Token);
            lockTaken = true;
            lock (_resourceStateGate)
            {
                if (_disposed
                    || timer.IsCancellationRequested
                    || SettingsManager.Current.DictationKeepModelLoaded
                    || IsResourceBusyUnsafe())
                    return;
            }

            await ReleaseLoadedResourcesCoreAsync();
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested || _disposed)
        {
            // Cancelled because another dictation started or the policy changed.
        }
        finally
        {
            if (lockTaken) _resourcePolicyLock.Release();
            lock (_resourceStateGate)
            {
                if (ReferenceEquals(_resourceReleaseCts, timer)) _resourceReleaseCts = null;
            }
            timer.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Local engine (whisper.cpp)
    // ------------------------------------------------------------------

    /// <summary>
    /// Configures the loader order before Whisper creates any factory. CUDA is only tried
    /// when the user enables it; CPU always stays available as a fallback.
    /// </summary>
    private void ConfigureRuntimeIfNeeded()
    {
        if (_runtimeConfigured) return;

        bool requested = RequestedDevice == DictationDevice.DedicatedGpu;
        // The notice is ALWAYS read (and consumed), even with the GPU off: otherwise an old
        // notice would stay on disk and fire months later when CUDA is turned on.
        bool previousCrash = DictationGpuSafety.PreviousLoadCrashed;

        if (requested && previousCrash)
        {
            // There is nothing to contain here: the failure was native. Dictation continues on
            // CPU and the setting is turned off so the next start does not try again.
            Logger.Warn("Dictado: la sesión anterior murió cargando CUDA; se arranca en CPU");
            requested = false;
            DisableGpuPreference();
        }

        _runtimeUseGpu = requested;
        ApplyRuntimeLibraryOrder(_runtimeUseGpu);
        _runtimeConfigured = true;
        Logger.Info(_runtimeUseGpu
            ? "Dictado: se intentará usar CUDA y se conservará CPU como respaldo"
            : "Dictado: se usará CPU");
    }

    /// <summary>
    /// Pins the active model to CPU after a CUDA failure and saves that preference.
    /// </summary>
    private static void DisableGpuPreference()
    {
        try
        {
            var settings = SettingsManager.Current;
            if (settings.GetDictationDevice(settings.DictationModel) == DictationDevice.Cpu) return;
            settings.SetDictationDevice(settings.DictationModel, DictationDevice.Cpu);
            SettingsManager.SaveSettings();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo guardar el ajuste de aceleración del dictado");
        }
    }

    private static void ApplyRuntimeLibraryOrder(bool useGpu)
    {
        WhisperCudaRuntime.CompletePendingRemoval();
        bool whisperModel = (DictationModelStore.Find(DictationDevices.ModelKey(SettingsManager.Current.DictationModel))?.Backend
            ?? DictationModelBackend.Whisper) == DictationModelBackend.Whisper;
        RuntimeOptions.RuntimeLibraryOrder = useGpu && whisperModel && WhisperCudaRuntime.TryPrepare()
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
    }

    private void UpdateRuntimeState()
    {
        _runtimeLoaded = RuntimeOptions.LoadedLibrary.HasValue;
        _runtimeInfo = _runtimeLoaded ? WhisperFactory.GetRuntimeInfo() : "";
        Logger.Info($"Runtime nativo de dictado: {RuntimeOptions.LoadedLibrary?.ToString() ?? "desconocido"} ({_runtimeInfo})");
        Changed?.Invoke();
    }

    private async Task<WhisperFactory> EnsureFactoryAsync(CancellationToken cancellationToken)
    {
        await _resourcePolicyLock.WaitAsync(cancellationToken);
        try
        {
            return await EnsureFactoryCoreAsync(cancellationToken);
        }
        finally
        {
            _resourcePolicyLock.Release();
        }
    }

    private async Task<WhisperFactory> EnsureFactoryCoreAsync(CancellationToken cancellationToken)
    {
        string path = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel)
            ?? throw new FileNotFoundException("No hay modelo de dictado activo");

        ConfigureRuntimeIfNeeded();

        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            if (_factory != null && _factoryPath == path && _factoryUseGpu == EffectiveWhisperUseGpu)
                return _factory;
            await DictationModelStore.ValidateIntegrityAsync(path, cancellationToken);
            _factory?.Dispose();
            _factory = LoadFactory(path);
            _factoryPath = path;
            UpdateRuntimeState();
            _factoryUseGpu &= UsingGpuRuntime;
            Logger.Info($"Modelo de dictado cargado: {Path.GetFileName(path)} ({_runtimeInfo})");
            return _factory;
        }
        finally
        {
            _engineLock.Release();
        }
    }

    /// <summary>
    /// Creates the engine with the runtime already chosen. With CUDA first, a broken
    /// NVIDIA driver makes the load crash or hang without a managed exception, so this
    /// is the only safety net available: leave the notice on disk before touching the
    /// native DLL and, if it still fails in a managed way, continue the session on CPU.
    /// </summary>
    private WhisperFactory LoadFactory(string path)
    {
        _factoryUseGpu = EffectiveWhisperUseGpu;
        if (!_factoryUseGpu)
            return WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false, GpuDevice = 0 });

        DictationGpuSafety.Arm();
        try
        {
            return WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = true, GpuDevice = 0 });
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "El motor CUDA del dictado no cargó; se continúa en CPU");
            FallBackToCpu();
        }

        _factoryUseGpu = false;
        return WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false, GpuDevice = 0 });
    }

    /// <summary>
    /// Leaves the session on CPU: the loader order is corrected (CUDA is not requested
    /// again), the setting is turned off, and the disk notice is removed so the next
    /// start does not read it as a dirty crash.
    /// </summary>
    private void FallBackToCpu()
    {
        _runtimeUseGpu = false;
        ApplyRuntimeLibraryOrder(false);
        DictationGpuSafety.Disarm();
        DisableGpuPreference();
        Changed?.Invoke();
    }

    /// <summary>Dictation sample rate (also Whisper's native format): 16 kHz.</summary>
    private const int SampleRateHz = 16_000;

    /// <summary>Engine threads: available cores minus one, clamped so the UI does not starve.</summary>
    private static int TranscriptionThreads => Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

    /// <summary>Length of what was captured, as text, for the log notices.</summary>
    private static string AudioSeconds(float[] samples) => $"{samples.Length / (double)SampleRateHz:F1} s";

    /// <summary>Microphone note for the log: only shown when it had to be reopened.</summary>
    private string MicNote() => _capture.Revives > 0 ? $" | micrófono reabierto {_capture.Revives}×" : "";

    /// <summary>
    /// Processor for one transcription: threads and language, with the engine defaults
    /// (full context, conditioning between segments and temperature fallback with its
    /// thresholds: that is what keeps poor audio from looping).
    /// </summary>
    private static WhisperProcessor BuildProcessor(WhisperFactory factory, string language) =>
        factory.CreateBuilder()
            .WithThreads(TranscriptionThreads)
            .WithLanguage(language)
            .Build();

    /// <summary>
    /// Reserves the context and the buffers that some backends only create on the first
    /// inference. It runs twice: once when the resources are preloaded, and once in the
    /// background while the user is speaking, so the first phrase never pays for it.
    /// </summary>
    private async Task WarmUpAsync(WhisperFactory factory, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            float[] silence = new float[SampleRateHz];
            using var processor = factory.CreateBuilder()
                .WithThreads(TranscriptionThreads)
                .WithLanguage("en")
                .WithNoContext()
                .WithSingleSegment()
                .Build();
            await foreach (var segment in processor.ProcessAsync(silence, cancellationToken))
            {
                _ = segment;
            }
            // The first CUDA inference can crash in the native DLL too: the notice stays until
            // one really goes through.
            DictationGpuSafety.Disarm();
            Logger.Info($"Dictado: motor templado en {clock.ElapsedMilliseconds} ms ({_runtimeInfo})");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposed)
        {
            // Switching model, turning dictation off or closing the app cancels the warm-up.
        }
        catch (Exception ex)
        {
            // A warm-up that fails breaks nothing: the real failure shows up while dictating.
            Logger.Warn(ex, "No se pudo templar el motor de dictado");
        }
    }

    /// <summary>Effective language: a ".en" model only understands English (RF-7).</summary>
    private static string EffectiveLanguage(string language, string? activePath)
    {
        if (activePath != null && DictationModelStore.IsEnglishOnly(activePath)) return "en";
        return string.IsNullOrWhiteSpace(language) ? "auto" : language;
    }

    /// <summary>
    /// Writes the transcription at the cursor, if the session is still current and has
    /// not been cancelled (RF-3/RF-4). It is the same lock cancellation uses: text and
    /// cancellation never cross halfway.
    /// </summary>
    private void WriteDictatedText(string text, CancellationToken token, CancellationTokenSource sessionCts)
    {
        if (token.IsCancellationRequested) return;
        lock (_transcriptionGate)
        {
            if (_disposed || !SettingsManager.Current.DictationEnabled || !IsCurrentTranscription(sessionCts))
                return;
            SendText(text);
        }
    }

    private async Task TranscribeAndSendAsync(
        float[] samples,
        string language,
        CancellationTokenSource sessionCts)
    {
        var clock = Stopwatch.StartNew();
        long vadMs = 0, engineMs = 0, decodeMs = 0;
        int segments = 0, characters = 0;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                sessionCts.Token,
                _lifetimeCts.Token);
            CancellationToken token = linkedCts.Token;

            long mark = clock.ElapsedMilliseconds;
            VoiceWindow window = LocateVoice(samples);
            if (window.Length == 0)
            {
                Logger.Info($"Dictation: {AudioSeconds(samples)} of audio with no voice"
                    + $" (gate {clock.ElapsedMilliseconds - mark} ms)"
                    + MicNote());
                return;
            }

            // Ambiguous level: background noise, or a voice too quiet to separate from
            // the floor. The neural VAD gets the last word there so a stray trigger does
            // not invent text. A VAD that is missing or failing only downgrades to
            // "assume voice": transcribing is always better than staying mute.
            if (!window.Confident)
            {
                bool? hasVoice = await DetectVoiceAsync(window.Slice(samples), token);
                vadMs = clock.ElapsedMilliseconds - mark;
                if (hasVoice == false)
                {
                    Logger.Info($"Dictation: {AudioSeconds(samples)} of audio with no voice"
                        + $" (gate {vadMs} ms)"
                        + MicNote());
                    return;
                }
            }
            else
            {
                vadMs = clock.ElapsedMilliseconds - mark;
            }

            float[] useful = window.Slice(samples);
            string vadNote = window.Confident ? $"{vadMs} ms (gate)" : $"{vadMs} ms (gate+VAD)";
            string activePath = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel)
                ?? throw new FileNotFoundException("There is no active dictation model");
            DictationModelInfo? activeModel = DictationModelStore.Find(Path.GetFileName(activePath));
            if (activeModel is { Backend: not DictationModelBackend.Whisper })
            {
                mark = clock.ElapsedMilliseconds;
                await DictationModelStore.ValidateIntegrityAsync(activePath, token);
                string text = await TranscribeExternalAsync(
                    activeModel,
                    activePath,
                    useful,
                    EffectiveLanguage(language, activePath),
                    RequestedDevice,
                    token, RequestedCudaVersion);
                engineMs = clock.ElapsedMilliseconds - mark;
                text = text.Trim();
                if (text.Length > 0)
                {
                    segments = 1;
                    characters = text.Length;
                    WriteDictatedText(text, token, sessionCts);
                }
                decodeMs = clock.ElapsedMilliseconds - mark;
                Logger.Info($"Dictation: {AudioSeconds(samples)}→{AudioSeconds(useful)} of audio | VAD {vadNote} | engine {engineMs} ms | "
                    + $"decode {decodeMs} ms | {segments} segment(s), "
                    + $"{characters} characters | model {activeModel.Name}{MicNote()}");
                return;
            }

            mark = clock.ElapsedMilliseconds;
            WhisperFactory factory = await EnsureFactoryAsync(token);
            engineMs = clock.ElapsedMilliseconds - mark;

            using var processor = BuildProcessor(factory, EffectiveLanguage(language, activePath));

            // Whisper can return several segments for a single session. They are accumulated
            // and injected together: one dictation session is one write at the target.
            var transcript = new StringBuilder();
            mark = clock.ElapsedMilliseconds;
            await foreach (var segment in processor.ProcessAsync(useful, token))
            {
                segments++;
                transcript.Append(segment.Text);
            }
            decodeMs = clock.ElapsedMilliseconds - mark;
            string whisperText = transcript.ToString().Trim();
            characters = whisperText.Length;
            if (whisperText.Length > 0)
                WriteDictatedText(whisperText, token, sessionCts);
            // CUDA has loaded and has inferred: the safety notice is redundant until the next load.
            DictationGpuSafety.Disarm();

            Logger.Info($"Dictation: {AudioSeconds(samples)}→{AudioSeconds(useful)} of audio | VAD {vadNote} | engine {engineMs} ms | "
                + $"decode {decodeMs} ms | {segments} segment(s), "
                + $"{characters} characters "
                + $"| {(_factoryPath == null ? "?" : Path.GetFileName(_factoryPath))}"
                + MicNote());
        }
        catch (OperationCanceledException) when (
            sessionCts.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
        {
            Logger.Info("Dictation transcription cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Dictation transcription failed");
            if (!_disposed && IsCurrentTranscription(sessionCts))
                Fail("IslandDictationFailed", "Could not transcribe the audio");
        }
        finally
        {
            bool current;
            lock (_transcriptionGate)
            {
                current = ReferenceEquals(
                    Interlocked.CompareExchange(ref _transcriptionCts, null, sessionCts),
                    sessionCts);
                sessionCts.Dispose();
            }
            if (current)
            {
                if (!_disposed && Phase == DictationPhase.Transcribing)
                    SetPhase(DictationPhase.Idle);

                bool policyPending;
                lock (_resourceStateGate)
                {
                    policyPending = _resourcePolicyPending;
                    _resourcePolicyPending = false;
                }

                if (!_disposed)
                {
                    if (policyPending) RefreshResourcePolicy();
                    else if (SettingsManager.Current.DictationKeepModelLoaded)
                    {
                        lock (_resourceStateGate) CancelResourceReleaseTimerUnsafe();
                    }
                    else ScheduleResourceRelease();
                }
            }
        }
    }

    private async Task<string> TranscribeExternalAsync(
        DictationModelInfo model,
        string modelPath,
        float[] samples,
        string language,
        DictationDevice device,
        CancellationToken cancellationToken,
        DictationCudaVersion cudaVersion)
    {
        await _resourcePolicyLock.WaitAsync(cancellationToken);
        try
        {
            return await _externalTranscriber.TranscribeAsync(
                model,
                modelPath,
                samples,
                language,
                device,
                cancellationToken, cudaVersion);
        }
        finally
        {
            _resourcePolicyLock.Release();
        }
    }

    /// <summary>
    /// Silero verdict on a window that the energy gate could not classify. Returns null
    /// when the VAD is unavailable or fails: the caller then assumes voice, because a
    /// failed check must not turn a working microphone into a mute one.
    /// </summary>
    private async Task<bool?> DetectVoiceAsync(float[] samples, CancellationToken cancellationToken)
    {
        try
        {
            WhisperVadFactory vadFactory = await EnsureVadFactoryAsync(cancellationToken);
            using var vad = vadFactory.CreateBuilder()
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 4))
                .WithThreshold(0.3f)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(100))
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(250))
                .WithSpeechPadding(TimeSpan.FromMilliseconds(150))
                .Build();

            var voiced = await vad.DetectSpeechAsync(samples, cancellationToken);
            return voiced.Count > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "The Silero VAD could not classify the dictation audio; assuming voice");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Voice gate: the cheap pass that replaces a neural one on the hot path
    // ------------------------------------------------------------------

    /// <summary>Frame hop of the voice gate: 20 ms at 16 kHz.</summary>
    private const int GateHopSamples = 320;

    /// <summary>
    /// Peak frame RMS below which a buffer is silence without asking anybody
    /// (about -70 dBFS). True silence is an exact zero stream, so this cut is
    /// unambiguous and never fires on speech.
    /// </summary>
    private const float GateAbsoluteSilence = 3e-4f;

    /// <summary>
    /// How far a frame has to stand above the room's own noise floor to count as
    /// voice. A ratio, not an absolute level, so it holds up in a quiet office and
    /// next to a fan: a fixed cut either mutes soft speech or lets the noise in.
    /// </summary>
    private const float GateSpeechRatio = 3.0f;

    /// <summary>Margin added to the floor for when the room really is silent.</summary>
    private const float GateSpeechMargin = 1.5e-3f;

    /// <summary>
    /// Samples kept before the first voiced frame and after the last one. Generous on
    /// purpose: a syllable's attack and a word's tail are quieter than its middle, and
    /// cutting them is worse than decoding a few hundred extra milliseconds.
    /// </summary>
    private const int GateHeadPadSamples = 6_400;
    private const int GateTailPadSamples = 9_600;

    /// <summary>
    /// The window of the captured buffer worth decoding.
    /// </summary>
    /// <param name="Start">First sample of the window.</param>
    /// <param name="Length">Window length; 0 means "no voice at all".</param>
    /// <param name="Confident">
    /// True when the level alone settled it, so the neural VAD can be skipped.
    /// </param>
    private readonly record struct VoiceWindow(int Start, int Length, bool Confident)
    {
        /// <summary>The window as its own array, or the original when it covers all of it.</summary>
        public float[] Slice(float[] samples) =>
            Start == 0 && Length == samples.Length ? samples : samples[Start..(Start + Length)];
    }

    /// <summary>
    /// Finds the voice in the captured buffer with one arithmetic pass: per-frame RMS,
    /// the room's noise floor as the 20th percentile, and the frames that clearly stand
    /// above it.
    ///
    /// <para>It answers in microseconds where the neural VAD needed a model load, a
    /// context and a full extra pass, and it trims the leading and trailing silence the
    /// recognizer would otherwise decode. For a user who holds the hotkey while thinking
    /// that trim is most of the work; for everybody it is the difference between a
    /// transcript that starts instantly and one that waits on the VAD first.</para>
    ///
    /// <para>The result is marked <see cref="VoiceWindow.Confident"/> only when speech
    /// actually cleared the floor. When nothing does, the caller asks the neural VAD:
    /// that is the ambiguous band — a stray trigger on a noisy desk — where guessing
    /// "no voice" would swallow a real phrase and guessing "voice" would invent one.</para>
    /// </summary>
    private static VoiceWindow LocateVoice(float[] samples)
    {
        int hop = GateHopSamples;
        int frames = samples.Length / hop;
        if (frames <= 0)
            return new VoiceWindow(0, 0, false);

        var rms = new float[frames];
        float peak = 0;
        for (int f = 0; f < frames; f++)
        {
            int offset = f * hop;
            float sum = 0;
            // Vectorized: the gate is the first thing every dictation pays for, and a
            // two-minute buffer is almost two million multiply-accumulates.
            var window = samples.AsSpan(offset, hop);
            int i = 0;
            for (; i <= window.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                sum += Vector.Dot(new Vector<float>(window.Slice(i, Vector<float>.Count)),
                    new Vector<float>(window.Slice(i, Vector<float>.Count)));
            }

            for (; i < window.Length; i++)
            {
                float sample = window[i];
                sum += sample * sample;
            }

            float value = MathF.Sqrt(sum * (1f / hop));
            rms[f] = value;
            if (value > peak) peak = value;
        }

        if (peak < GateAbsoluteSilence) return new VoiceWindow(0, 0, true);

        // The 20th percentile is background hiss, not voice: speech occupies far less
        // than 80% of a dictated phrase even when the user talks continuously.
        float[] sorted = (float[])rms.Clone();
        Array.Sort(sorted);
        float floor = sorted[sorted.Length / 5];
        float threshold = MathF.Max(floor * GateSpeechRatio, floor + GateSpeechMargin);
        if (threshold < GateAbsoluteSilence) threshold = GateAbsoluteSilence;

        int first = -1;
        int last = -1;
        for (int f = 0; f < frames; f++)
        {
            if (rms[f] < threshold) continue;
            if (first < 0) first = f;
            last = f;
        }

        if (first < 0)
        {
            // Nothing cleared the floor. Either the room is loud enough to hide a soft
            // voice, or the hotkey fired over silence: only the neural VAD can tell, so
            // the whole buffer goes to it rather than to the recognizer.
            return new VoiceWindow(0, samples.Length, false);
        }

        // Everything between the first and the last voiced frame is kept, pauses
        // included: a breath between words is not a place to cut.
        int start = Math.Max(0, first * hop - GateHeadPadSamples);
        int end = Math.Min(samples.Length, (last + 1) * hop + GateTailPadSamples);
        if (end <= start) return new VoiceWindow(0, 0, true);
        return new VoiceWindow(start, end - start, true);
    }

    private async Task<WhisperVadFactory> EnsureVadFactoryAsync(CancellationToken cancellationToken)
    {
        await _resourcePolicyLock.WaitAsync(cancellationToken);
        try
        {
            return await EnsureVadFactoryCoreAsync(cancellationToken);
        }
        finally
        {
            _resourcePolicyLock.Release();
        }
    }

    private async Task<WhisperVadFactory> EnsureVadFactoryCoreAsync(CancellationToken cancellationToken)
    {
        ConfigureRuntimeIfNeeded();

        WhisperVadFactory? cached = _vadFactory;
        if (cached != null) return cached;

        string path = await DictationModelStore.EnsureVadModelAsync(cancellationToken);
        await _vadLock.WaitAsync(cancellationToken);
        try
        {
            if (_vadFactory != null && _vadFactoryPath == path) return _vadFactory;
            _vadFactory?.Dispose();
            _vadFactory = WhisperVadFactory.FromPath(path, new WhisperFactoryOptions
            {
                // VAD is deliberately kept on CPU: it is tiny and this avoids reserving
                // GPU memory before the main Whisper model starts transcription.
                UseGpu = false,
                GpuDevice = 0,
            });
            _vadFactoryPath = path;
            UpdateRuntimeState();
            return _vadFactory;
        }
        finally
        {
            _vadLock.Release();
        }
    }

    private bool IsCurrentTranscription(CancellationTokenSource sessionCts) =>
        ReferenceEquals(Volatile.Read(ref _transcriptionCts), sessionCts);

    private void CancelTranscription()
    {
        lock (_transcriptionGate)
        {
            try
            {
                _transcriptionCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The transcription finished right before it could observe the cancellation.
            }
        }
    }

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private void SetPhase(DictationPhase phase)
    {
        Phase = phase;
        if (phase is DictationPhase.Listening or DictationPhase.Transcribing or DictationPhase.Idle)
        {
            MessageKey = null;
            MessageFallback = null;
        }
        Changed?.Invoke();
    }

    private void Fail(string messageKey, string fallback, int hideAfterMs = 2500)
    {
        Phase = DictationPhase.Error;
        MessageKey = messageKey;
        MessageFallback = fallback;
        Changed?.Invoke();
        _ = Task.Delay(hideAfterMs).ContinueWith(_ =>
        {
            if (_disposed || Phase != DictationPhase.Error) return;
            SetPhase(DictationPhase.Idle);
        });
    }

    // ------------------------------------------------------------------
    // Writing at the insertion point
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes the text at the cursor with Unicode <c>SendInput</c>: it works for any
    /// language, it does not touch the clipboard, and it respects the app in front.
    /// System limit: Windows does not allow injecting into elevated windows (UIPI).
    /// </summary>
    private static void SendText(string text)
    {
        // Some engines return accented letters as a base letter plus a combining
        // accent. SendInput delivers individual UTF-16 units and plenty of apps do
        // not recombine that sequence; NFC turns it into "á", "é", etc. before it is
        // injected.
        text = text.Normalize(NormalizationForm.FormC);
        // Two events per character, written straight into a right-sized array: the
        // List plus its final copy allocated the whole thing twice, on a phrase that
        // can be a few thousand inputs long.
        var inputs = new NativeMethods.INPUT[text.Length * 2];
        int count = 0;
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                inputs[count++] = KeyInput(0x0D, false);
                inputs[count++] = KeyInput(0x0D, true);
                continue;
            }
            if (c == '\t')
            {
                inputs[count++] = KeyInput(0x09, false);
                inputs[count++] = KeyInput(0x09, true);
                continue;
            }
            // Unicode: one character (one UTF-16 unit) per press and its release.
            inputs[count++] = UnicodeInput(c, false);
            inputs[count++] = UnicodeInput(c, true);
        }
        if (count == 0) return;
        uint sent = NativeMethods.SendInput((uint)count, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != count) Logger.Warn($"SendInput wrote {sent} of {count} dictation events");
    }

    private static NativeMethods.INPUT KeyInput(ushort virtualKey, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        ki = new NativeMethods.KEYBDINPUT
        {
            wVk = virtualKey,
            dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
        },
    };

    private static NativeMethods.INPUT UnicodeInput(char c, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        ki = new NativeMethods.KEYBDINPUT
        {
            wScan = c,
            dwFlags = NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0),
        },
    };

    // ------------------------------------------------------------------
    // Microphone
    // ------------------------------------------------------------------

    /// <summary>
    /// Capture from the default microphone at 16 kHz mono PCM16 - Whisper's native
    /// format - accumulating the already normalized samples and measuring the level for
    /// the waves.
    ///
    /// <para>It watches that the device keeps delivering audio. A long dictation used to
    /// be lost entirely because capture died halfway - another app took the microphone,
    /// the default device changed, the driver slept - and nobody noticed until the key
    /// came back, with three seconds of audio and no voice in it. The watchdog now spots
    /// that and reopens capture without leaving dictation, keeping what was recorded.</para>
    /// </summary>
    private sealed class MicCapture : IDisposable
    {
        private const int SampleRate = 16_000;
        /// <summary>Top of a dictation: 2 minutes. Caps memory (7.7 MB of samples).</summary>
        private const int MaxSamples = SampleRate * 120;
        /// <summary>Without a single device buffer in this time, capture is dead (the same criterion the visualizer uses).</summary>
        private const int StallMs = 2000;
        /// <summary>
        /// Room reserved on the first block so a typical phrase never has to grow it.
        /// The buffer doubles from here; the cap above is the only hard limit.
        /// </summary>
        private const int InitialCapacity = SampleRate * 15;

        // The device is opened in Start(), not in the constructor: on a machine with no
        // microphone building the object already throws, and this service is born with
        // the main window (dictation being off must not stop the app from starting).
        private WaveIn? _wave;
        /// <summary>
        /// Plain array with an explicit length, not a List: the audio callback appends to
        /// it every 100 ms and a List checks its own bounds on every single sample.
        /// </summary>
        private float[] _samples = new float[InitialCapacity];
        private int _count;
        /// <summary>Samples: the audio thread writes them, the transcription thread reads them.</summary>
        private readonly Lock _lock = new();
        /// <summary>Device and recording: touched by the UI, the watchdog and the teardown.</summary>
        private readonly Lock _deviceLock = new();
        private System.Timers.Timer? _watchdog;
        private volatile float _level;
        private volatile bool _active;
        private long _lastDataTick;
        private int _revives;
        private bool _capped;

        /// <summary>Level of the last buffer (0..1) for the visualizer.</summary>
        public float Level => _level;

        /// <summary>Capture restarts during this dictation (0 = the device behaved).</summary>
        public int Revives => _revives;

        public void Start()
        {
            lock (_lock)
            {
                _count = 0;
                _level = 0;
                _capped = false;
            }
            _revives = 0;
            lock (_deviceLock)
            {
                OpenAndRecord();
                _active = true;
            }
            StartWatchdog();
        }

        /// <summary>Closes the microphone and returns what was captured.</summary>
        public float[] Stop()
        {
            _active = false; // before the watchdog: an in-flight tick must not reopen anything
            StopWatchdog();
            lock (_deviceLock)
            {
                try
                {
                    CloseDevice();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to close the microphone");
                }
            }
            _level = 0;
            lock (_lock)
            {
                float[] copy = _samples.AsSpan(0, _count).ToArray();
                _count = 0; // a second Stop without Start returns empty, it does not duplicate a session
                return copy;
            }
        }

        public void Dispose()
        {
            _active = false;
            StopWatchdog();
            lock (_deviceLock)
            {
                try
                {
                    CloseDevice();
                }
                catch
                {
                    // The device may already have been closed.
                }
            }
        }

        /// <summary>Opens the device if needed and starts recording.</summary>
        private void OpenAndRecord()
        {
            if (_wave == null)
            {
                var wave = new WaveIn
                {
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = 100,
                    NumberOfBuffers = 4,
                };
                wave.DataAvailable += OnData;
                _wave = wave;
            }
            _wave.StartRecording();
            Volatile.Write(ref _lastDataTick, Environment.TickCount64);
        }

        /// <summary>Releases the device (the next dictation opens a fresh one).</summary>
        private void CloseDevice()
        {
            var wave = _wave;
            _wave = null;
            if (wave == null) return;
            wave.DataAvailable -= OnData;
            wave.StopRecording();
            wave.Dispose();
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            Volatile.Write(ref _lastDataTick, Environment.TickCount64);
            int count = e.BytesRecorded / 2;
            if (count <= 0) return;
            var pcm = e.Buffer.AsSpan(0, count * 2);
            float sum = 0;
            lock (_lock)
            {
                int room = MaxSamples - _count;
                int take = Math.Min(count, room);
                if (take == 0 && !_capped)
                {
                    // Dictation cap reached: whatever comes next does not fit.
                    _capped = true;
                    Logger.Warn($"Dictation: recording hit the {MaxSamples / SampleRate} s cap; "
                        + "anything after it is not captured");
                }

                GrowIfNeeded(take);
                var destination = _samples.AsSpan(_count, take);
                for (int i = 0; i < take; i++)
                {
                    short raw = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                    float sample = raw / 32768f;
                    destination[i] = sample;
                    sum += sample * sample;
                }
                _count += take;
            }

            // Buffer RMS -> visualizer level (the Island does the smoothing).
            float rms = MathF.Sqrt(sum / count);
            _level = Math.Clamp(rms * 9f, 0f, 1f);
        }

        /// <summary>
        /// Doubles the sample buffer until it can hold <paramref name="extra"/> more.
        /// Only the first blocks of a long dictation ever pay for it.
        /// </summary>
        private void GrowIfNeeded(int extra)
        {
            int required = _count + extra;
            if (required <= _samples.Length) return;
            int capacity = Math.Max(_samples.Length, InitialCapacity);
            while (capacity < required) capacity *= 2;
            if (capacity > MaxSamples) capacity = MaxSamples;
            Array.Resize(ref _samples, capacity);
        }

        /// <summary>Samples stored so far, in seconds (for the watchdog notices).</summary>
        private double CapturedSeconds()
        {
            lock (_lock) return _count / (double)SampleRate;
        }

        private void StartWatchdog()
        {
            StopWatchdog();
            var watchdog = new System.Timers.Timer(StallMs / 2.0) { AutoReset = true };
            watchdog.Elapsed += (_, _) =>
            {
                if (!_active) return;
                if (Environment.TickCount64 - Volatile.Read(ref _lastDataTick) < StallMs) return;
                Revive();
            };
            watchdog.Start();
            _watchdog = watchdog;
        }

        private void StopWatchdog()
        {
            var watchdog = _watchdog;
            _watchdog = null;
            if (watchdog == null) return;
            watchdog.Stop();
            watchdog.Dispose();
        }

        /// <summary>
        /// The device stopped delivering audio halfway through the dictation: it is
        /// reopened and recording CONTINUES in the same session, keeping what was already
        /// captured. If stopping and starting the same device fails, it is released and a
        /// new one is opened (which also picks up a change of default microphone).
        /// </summary>
        private void Revive()
        {
            lock (_deviceLock)
            {
                if (!_active) return; // the session already closed: nothing to revive
                Volatile.Write(ref _lastDataTick, Environment.TickCount64); // one attempt at a time
                _revives++;
                bool first = _revives <= 3 || _revives % 10 == 0; // without flooding the log
                double captured = CapturedSeconds();
                var wave = _wave;
                if (wave != null)
                {
                    try
                    {
                        wave.StopRecording();
                        wave.StartRecording();
                        if (first)
                            Logger.Warn($"Dictation: the microphone stopped delivering audio; "
                                + $"capture resumes with {captured:F1} s already stored");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Dictation: could not resume capture on the same device; opening another one");
                        try
                        {
                            CloseDevice();
                        }
                        catch
                        {
                            // The device was already gone.
                        }
                    }
                }

                try
                {
                    OpenAndRecord();
                    Logger.Warn($"Dictado: micrófono reabierto con un dispositivo nuevo ({captured:F1} s guardados)");
                }
                catch (Exception ex)
                {
                    // Without a microphone: the rest of the dictation cannot be captured, but the
                    // watchdog keeps trying and what was captured is not lost.
                    if (first) Logger.Error(ex, "Dictado: el micrófono no volvió a abrirse");
                }
            }
        }
    }
}
