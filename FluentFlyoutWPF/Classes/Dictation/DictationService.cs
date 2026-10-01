// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using NAudio.Wave;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>Estado del dictado: es lo que el Island dibuja (spec 006 RF-1/RF-6).</summary>
public enum DictationPhase
{
    /// <summary>En reposo: ni micrófono ni transcripción.</summary>
    Idle,

    /// <summary>Grabando: el Island enseña el micro y las ondas.</summary>
    Listening,

    /// <summary>Grabación cerrada, transcribiendo.</summary>
    Transcribing,

    /// <summary>Algo falló (sin micrófono, sin modelo): el Island lo dice unos segundos.</summary>
    Error,
}

/// <summary>
/// Dictado por voz LOCAL (spec 006): mantén el atajo, habla, suelta, y el texto aparece
/// donde esté el cursor.
/// <para>El motor es whisper.cpp (Whisper.net) con un modelo ggml del disco: la
/// inferencia no sale del equipo —la red solo se usa para descargar los modelos—. La
/// captura va por <see cref="WaveIn"/> a 16 kHz mono, que es el formato nativo de Whisper,
/// y el texto se inyecta con <c>SendInput</c> Unicode (sin tocar el portapapeles).</para>
///
/// <para>Este objeto no conoce el Island: expone fase, nivel y mensaje, y quien lo pinta
/// (IslandWindow.Dictation.cs) se suscribe a <see cref="Changed"/>.</para>
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

    public DictationPhase Phase { get; private set; } = DictationPhase.Idle;

    /// <summary>¿Está el dictado en marcha (grabando o transcribiendo)?</summary>
    public bool Active => Phase is DictationPhase.Listening or DictationPhase.Transcribing;

    /// <summary>
    /// El usuario está definiendo el atajo en Ajustes: las teclas que pulse en esa caja no
    /// son dictado, así que no abren el micrófono ni cancelan nada. Lo pone la página del
    /// dictado mientras su caja de captura tiene el foco.
    /// </summary>
    public bool HotkeyCaptureActive { get; set; }

    /// <summary>Nivel del micrófono ahora mismo (0..1), para el visualizador de ondas.</summary>
    public float Level => _capture.Level;

    /// <summary>Clave de localización del mensaje vigente (error o aviso); null si no hay.</summary>
    public string? MessageKey { get; private set; }

    /// <summary>Texto de respaldo en inglés del mensaje vigente.</summary>
    public string? MessageFallback { get; private set; }

    /// <summary>Indica si Whisper ya cargó un runtime nativo.</summary>
    public bool RuntimeLoaded => _runtimeLoaded;

    /// <summary>Indica si el runtime nativo cargado es CUDA.</summary>
    public bool UsingGpuRuntime => RuntimeOptions.LoadedLibrary is RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12;

    /// <summary>Información del runtime nativo que Whisper.NET está usando.</summary>
    public string RuntimeInfo => _runtimeInfo;

    /// <summary>
    /// El runtime nativo es global para el proceso: cambiar CPU/CUDA después de cargarlo
    /// no es seguro y requiere reiniciar la aplicación.
    /// </summary>
    public bool AccelerationRestartRequired =>
        _runtimeLoaded && _runtimeUseGpu != SettingsManager.Current.DictationUseGpu;

    /// <summary>Se dispara con cualquier cambio de fase, nivel de mensaje o fin del dictado.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------
    // Atajo (lo llama el gancho de teclado de MainWindow)
    // ------------------------------------------------------------------

    /// <summary>
    /// Una tecla del gancho global. Mantiene el conjunto de teclas pulsadas —el gancho ve
    /// TODAS, la aplicación tenga el foco o no— y decide con la lógica pura del atajo:
    /// completarlo arranca, soltar cualquiera de sus teclas cierra y solo Escape cancela
    /// (RF-1/RF-3/RF-4); cualquier otra tecla ajena se ignora, así un roce con la mano no
    /// descarta la frase que se está dictando.
    /// </summary>
    public void HandleKey(int virtualKey, bool down, bool injected = false)
    {
        // Un evento inyectado (el texto que escribimos nosotros mismos, un teclado en
        // pantalla, una macro) no es una tecla del usuario: el dictado no debe verlo. Sin
        // esto, un Enter inyectado —el salto de línea de una transcripción— cancelaba la
        // sesión que lo estaba escribiendo. Además, las teclas Unicode no traen código
        // virtual, así que tampoco cuentan para el atajo.
        if (_disposed || injected || virtualKey == 0) return;
        // Definir el atajo en Ajustes no es dictar: esas teclas no arrancan ni cortan nada.
        if (HotkeyCaptureActive) return;
        // El gancho entrega el modificador físico (0xA2 para Ctrl izquierdo); el atajo
        // guardado dice «Ctrl»: se unifican antes de comparar nada.
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
    // Ciclo del dictado
    // ------------------------------------------------------------------

    /// <summary>Abre el micrófono y presenta la tarjeta del Island (RF-1).</summary>
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
            CancelResourceReleaseTimerUnsafe();
            _resourceSessionStarting = true;
        }

        // El micrófono se abre AQUÍ y no en un hilo aparte a propósito: el gancho de
        // teclado tiene un plazo de ~300 ms y abrir el dispositivo son unas decenas de
        // ms, mientras que diferirlo abriría la puerta a que la tecla se suelte antes de
        // que exista la grabación. La transcripción, que sí tarda, va a otro hilo.
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
        _ = PrefetchEngineAsync();
        lock (_resourceStateGate) _resourceSessionStarting = false;
    }

    /// <summary>
    /// Adelanta la carga del motor mientras el usuario habla (en CUDA son segundos):
    /// al soltar ya está caliente y el icono se retira antes. No toca fase ni micro;
    /// si falla o se cancela, la transcripción lo carga igual que antes.
    /// </summary>
    private async Task PrefetchEngineAsync()
    {
        try
        {
            if (_disposed || !SettingsManager.Current.DictationEnabled) return;
            string? path = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel);
            if (path == null) return;
            DictationModelInfo? model = DictationModelStore.Find(Path.GetFileName(path));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            if (model is { Backend: not DictationModelBackend.Whisper })
            {
                await DictationModelStore.ValidateIntegrityAsync(path, cts.Token);
                await _externalTranscriber.EnsureLoadedAsync(
                    model, path, SettingsManager.Current.DictationUseGpu, cts.Token);
            }
            else
            {
                await EnsureFactoryAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // La sesión terminó antes de que el motor se templara: la transcripción lo carga.
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Precarga del motor de dictado fallida; se cargará al transcribir");
        }
    }

    /// <summary>Cierra el micrófono, descarta el audio y no escribe nada (RF-4).</summary>
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

    /// <summary>Cierra el micrófono y transcribe lo grabado, si hay voz (RF-3/RF-5).</summary>
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
    /// Aplica la política actual de carga. Se conserva como punto de compatibilidad para el
    /// arranque y para los cambios de configuración.
    /// </summary>
    public void Preload()
    {
        RefreshResourcePolicy();
    }

    /// <summary>
    /// Reaplica el modo de carga y el modelo seleccionado. En automático libera primero lo
    /// que hubiera quedado cargado; en permanente deja la carga preparada en segundo plano.
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

    /// <summary>Reinicia el temporizador con el nuevo valor del deslizador.</summary>
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

    /// <summary>Ajuste en caliente: si el dictado dejó de estar activado, se corta en seco.</summary>
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
    /// Notifica a la interfaz si el toggle de aceleración ya no coincide con el runtime
    /// cargado. La selección efectiva se hace antes de crear el primer factory.
    /// </summary>
    public void RefreshAccelerationSettings()
    {
        if (_disposed || !_runtimeConfigured || _runtimeUseGpu == SettingsManager.Current.DictationUseGpu)
            return;

        if (!_runtimeLoaded)
        {
            // Todavía no se ha creado el runtime nativo; se puede cambiar el orden antes
            // de la primera transcripción.
            _runtimeUseGpu = SettingsManager.Current.DictationUseGpu;
            ApplyRuntimeLibraryOrder(_runtimeUseGpu);
            return;
        }

        Logger.Info("El cambio de aceleración del dictado requiere reiniciar la aplicación");
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
        // Cierre ordenado con CUDA cargada: no es un fallo, el aviso se retira.
        DictationGpuSafety.Disarm();
    }

    private bool IsResourceBusyUnsafe() =>
        _resourceSessionStarting
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
            // El temporizador terminó justo antes de que se cancelara.
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
            // La transición terminó justo antes de que se cancelara.
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
            // Un cambio de modelo o de política invalida esta transición.
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
                SettingsManager.Current.DictationUseGpu,
                cancellationToken);
        }
        else
        {
            WhisperFactory factory = await EnsureFactoryCoreAsync(cancellationToken);
            await WarmUpAsync(factory, cancellationToken);
        }

        Logger.Info($"Dictado: recursos preparados para {Path.GetFileName(modelPath)}");
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
            // Se canceló al empezar otro dictado o al cambiar la política.
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
    // Motor local (whisper.cpp)
    // ------------------------------------------------------------------

    /// <summary>
    /// Configura el orden del cargador antes de que Whisper cree cualquier factory.
    /// CUDA se intenta solo cuando el usuario lo activa; siempre queda CPU como respaldo.
    /// </summary>
    private void ConfigureRuntimeIfNeeded()
    {
        if (_runtimeConfigured) return;

        bool requested = SettingsManager.Current.DictationUseGpu;
        // El aviso se lee SIEMPRE (y se consume), aunque la GPU esté apagada: si no,
        // un aviso viejo se quedaría en disco y saltaría al activar CUDA meses después.
        bool previousCrash = DictationGpuSafety.PreviousLoadCrashed;

        if (requested && previousCrash)
        {
            // Aquí no hay nada que contener: el fallo fue nativo. Se dicta en CPU y se
            // apaga el ajuste para que el próximo arranque no lo vuelva a intentar.
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
    /// Apaga el ajuste de aceleración y lo guarda: la GPU que ha tumbado el proceso no
    /// vuelve a intentarse sola, ni en esta sesión ni en la siguiente.
    /// </summary>
    private static void DisableGpuPreference()
    {
        try
        {
            if (!SettingsManager.Current.DictationUseGpu) return;
            SettingsManager.Current.DictationUseGpu = false;
            SettingsManager.SaveSettings();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "No se pudo guardar el ajuste de aceleración del dictado");
        }
    }

    private static void ApplyRuntimeLibraryOrder(bool useGpu)
    {
        RuntimeOptions.RuntimeLibraryOrder = useGpu
            ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
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
            if (_factory != null && _factoryPath == path) return _factory;
            await DictationModelStore.ValidateIntegrityAsync(path, cancellationToken);
            _factory?.Dispose();
            _factory = LoadFactory(path);
            _factoryPath = path;
            UpdateRuntimeState();
            Logger.Info($"Modelo de dictado cargado: {Path.GetFileName(path)} ({_runtimeInfo})");
            return _factory;
        }
        finally
        {
            _engineLock.Release();
        }
    }

    /// <summary>
    /// Crea el motor con el runtime ya elegido. Con CUDA delante, un controlador NVIDIA
    /// roto hace que la carga reviente o se cuelgue sin excepción gestionada, así que
    /// aquí está la única red posible: dejar el aviso en disco antes de tocar la DLL
    /// nativa y, si aun así falla de forma gestionada, seguir la sesión en CPU.
    /// </summary>
    private WhisperFactory LoadFactory(string path)
    {
        if (!_runtimeUseGpu)
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

        return WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = false, GpuDevice = 0 });
    }

    /// <summary>
    /// Deja la sesión en CPU: se corrige el orden del cargador (no se vuelve a pedir
    /// CUDA), se apaga el ajuste y se retira el aviso de disco para que el próximo
    /// arranque no lo interprete como una muerte sucia.
    /// </summary>
    private void FallBackToCpu()
    {
        _runtimeUseGpu = false;
        ApplyRuntimeLibraryOrder(false);
        DictationGpuSafety.Disarm();
        DisableGpuPreference();
        Changed?.Invoke();
    }

    /// <summary>Frecuencia de muestreo del dictado (y del formato nativo de Whisper): 16 kHz.</summary>
    private const int SampleRateHz = 16_000;

    /// <summary>Hilos del motor: los núcleos disponibles menos uno, acotado para no ahogar la interfaz.</summary>
    private static int TranscriptionThreads => Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

    /// <summary>Duración de lo capturado, en texto, para los avisos del registro.</summary>
    private static string AudioSeconds(float[] samples) => $"{samples.Length / (double)SampleRateHz:F1} s";

    /// <summary>Aviso de micrófono para el registro: solo aparece si hubo que reabrirlo.</summary>
    private string MicNote() => _capture.Revives > 0 ? $" | micrófono reabierto {_capture.Revives}×" : "";

    /// <summary>
    /// Procesador de una transcripción: hilos e idioma, con los defaults del motor
    /// (contexto completo, condicionamiento entre segmentos y fallback de temperatura
    /// con sus thresholds: es lo que evita los bucles con audio pobre).
    /// </summary>
    private static WhisperProcessor BuildProcessor(WhisperFactory factory, string language) =>
        factory.CreateBuilder()
            .WithThreads(TranscriptionThreads)
            .WithLanguage(language)
            .Build();

    /// <summary>
    /// La primera pasada reserva el contexto y los buffers que algunos backends solo crean
    /// al inferir. Solo se usa en modo permanente; en automático la primera frase conserva
    /// el comportamiento bajo demanda y evita trabajo extra si el dictado no se utiliza.
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
            // La primera inferencia con CUDA también puede reventar en la DLL nativa: el
            // aviso sigue puesto hasta que una pasa de verdad.
            DictationGpuSafety.Disarm();
            Logger.Info($"Dictado: motor templado en {clock.ElapsedMilliseconds} ms ({_runtimeInfo})");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposed)
        {
            // Cambiar de modelo, apagar el dictado o cerrar la aplicación cancela el templado.
        }
        catch (Exception ex)
        {
            // Un templado que falla no rompe nada: el fallo real se verá al dictar.
            Logger.Warn(ex, "No se pudo templar el motor de dictado");
        }
    }

    /// <summary>Idioma efectivo: un modelo «.en» solo entiende inglés (RF-7).</summary>
    private static string EffectiveLanguage(string language, string? activePath)
    {
        if (activePath != null && DictationModelStore.IsEnglishOnly(activePath)) return "en";
        return string.IsNullOrWhiteSpace(language) ? "auto" : language;
    }

    /// <summary>
    /// Escribe la transcripción donde esté el cursor, si la sesión sigue vigente y no
    /// se ha cancelado (RF-3/RF-4). Es el mismo candado que usa la cancelación: texto y
    /// cancelación no se cruzan a mitad.
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
            var voiced = await DetectVoiceAsync(samples, token);
            float[] useful = TrimSilence(samples, voiced);
            if (useful.Length == 0)
            {
                // RF-5: silencio = ni transcripción ni texto (y ninguna alucinación).
                Logger.Info($"Dictado: {AudioSeconds(samples)} de audio sin voz (VAD {clock.ElapsedMilliseconds - mark} ms)"
                    + MicNote());
                return;
            }
            vadMs = clock.ElapsedMilliseconds - mark;

            string activePath = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel)
                ?? throw new FileNotFoundException("No hay modelo de dictado activo");
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
                    SettingsManager.Current.DictationUseGpu,
                    token);
                engineMs = clock.ElapsedMilliseconds - mark;
                text = text.Trim();
                if (text.Length > 0)
                {
                    segments = 1;
                    characters = text.Length;
                    WriteDictatedText(text, token, sessionCts);
                }
                decodeMs = clock.ElapsedMilliseconds - mark;
                Logger.Info($"Dictado: {AudioSeconds(samples)}→{AudioSeconds(useful)} de audio | VAD {vadMs} ms ({voiced.Count} tramo(s)) | motor {engineMs} ms | "
                    + $"decodificación {decodeMs} ms | {segments} segmento(s), "
                    + $"{characters} caracteres | modelo {activeModel.Name}{MicNote()}");
                return;
            }

            mark = clock.ElapsedMilliseconds;
            WhisperFactory factory = await EnsureFactoryAsync(token);
            engineMs = clock.ElapsedMilliseconds - mark;

            using var processor = BuildProcessor(factory, EffectiveLanguage(language, activePath));

            // Whisper puede devolver varios segmentos para una sola sesión. Se acumulan y se
            // inyectan juntos: una sesión de dictado produce una sola escritura en destino.
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
            // CUDA ya cargó y ya infirió: el aviso de seguridad sobra hasta la próxima carga.
            DictationGpuSafety.Disarm();

            Logger.Info($"Dictado: {AudioSeconds(samples)}→{AudioSeconds(useful)} de audio | VAD {vadMs} ms ({voiced.Count} tramo(s)) | motor {engineMs} ms | "
                + $"decodificación {decodeMs} ms | {segments} segmento(s), "
                + $"{characters} caracteres "
                + $"| {(_factoryPath == null ? "?" : Path.GetFileName(_factoryPath))}"
                + MicNote());
        }
        catch (OperationCanceledException) when (
            sessionCts.IsCancellationRequested || _lifetimeCts.IsCancellationRequested)
        {
            Logger.Info("Transcripción de dictado cancelada");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Falló la transcripción del dictado");
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
        bool useGpu,
        CancellationToken cancellationToken)
    {
        await _resourcePolicyLock.WaitAsync(cancellationToken);
        try
        {
            return await _externalTranscriber.TranscribeAsync(
                model,
                modelPath,
                samples,
                language,
                useGpu,
                cancellationToken);
        }
        finally
        {
            _resourcePolicyLock.Release();
        }
    }

    /// <summary>Padding extra al recortar por VAD: no cortar el ataque ni la cola de palabra.</summary>
    private static readonly TimeSpan VadTrimPadding = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Tramos con voz según Silero (RF-5). El builder ya añade 150 ms de padding por
    /// tramo; aquí solo se usan sus marcas para recortar el audio antes de inferir.
    /// </summary>
    private async Task<IReadOnlyList<VadSegmentData>> DetectVoiceAsync(float[] samples, CancellationToken cancellationToken)
    {
        WhisperVadFactory vadFactory = await EnsureVadFactoryAsync(cancellationToken);
        using var vad = vadFactory.CreateBuilder()
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 4))
            .WithThreshold(0.5f)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(100))
            .WithMinSilenceDuration(TimeSpan.FromMilliseconds(250))
            .WithSpeechPadding(TimeSpan.FromMilliseconds(150))
            .Build();

        return await vad.DetectSpeechAsync(samples, cancellationToken);
    }

    /// <summary>
    /// Recorta silencio inicial/final entre el primer y el último tramo con voz. Al motor
    /// le llega audio útil en vez del buffer crudo con silencios: eso evita bucles,
    /// parciales e inventos con habla lenta. Sin voz → vacío (RF-5).
    /// </summary>
    private static float[] TrimSilence(float[] samples, IReadOnlyList<VadSegmentData> voiced)
    {
        if (voiced.Count == 0) return [];
        int start = Math.Max(0, (int)((voiced[0].Start - VadTrimPadding).TotalSeconds * SampleRateHz));
        int end = Math.Min(
            samples.Length,
            (int)Math.Ceiling((voiced[voiced.Count - 1].End + VadTrimPadding).TotalSeconds * SampleRateHz));
        if (end <= start) return [];
        return samples[start..end];
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
                // La transcripción terminó justo antes de observar la cancelación.
            }
        }
    }

    // ------------------------------------------------------------------
    // Estado
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
    // Escritura en el punto de inserción
    // ------------------------------------------------------------------

    /// <summary>
    /// Escribe el texto donde esté el cursor con <c>SendInput</c> Unicode: vale para
    /// cualquier idioma, no toca el portapapeles y respeta la aplicación de delante.
    /// Límite del sistema: Windows no deja inyectar en ventanas elevadas (UIPI).
    /// </summary>
    private static void SendText(string text)
    {
        // Algunos motores devuelven letras con tilde como letra + acento combinante.
        // SendInput entrega unidades UTF-16 individuales y varias aplicaciones no
        // recomponen esa secuencia; NFC la convierte en «á», «é», etc. antes de inyectarla.
        text = text.Normalize(NormalizationForm.FormC);
        var inputs = new List<NativeMethods.INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                inputs.Add(KeyInput(0x0D, false));
                inputs.Add(KeyInput(0x0D, true));
                continue;
            }
            if (c == '\t')
            {
                inputs.Add(KeyInput(0x09, false));
                inputs.Add(KeyInput(0x09, true));
                continue;
            }
            // Unicode: un carácter (una unidad UTF-16) por pulsación y su suelta.
            inputs.Add(UnicodeInput(c, false));
            inputs.Add(UnicodeInput(c, true));
        }
        if (inputs.Count == 0) return;
        uint sent = NativeMethods.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Count) Logger.Warn($"SendInput escribió {sent} de {inputs.Count} eventos de dictado");
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
    // Micrófono
    // ------------------------------------------------------------------

    /// <summary>
    /// Captura del micrófono predeterminado a 16 kHz mono PCM16 —el formato nativo de
    /// Whisper— acumulando las muestras ya normalizadas y midiendo el nivel para las ondas.
    ///
    /// <para>Vigila que el dispositivo siga entregando audio. Un dictado largo se perdía
    /// entero porque la captura se moría a mitad —otra aplicación se quedó con el micro, el
    /// dispositivo predeterminado cambió, el controlador se durmió— y nadie lo notaba hasta
    /// soltar la tecla, con un audio de tres segundos y sin voz. Ahora el vigía lo detecta y
    /// reabre la captura sin salir del dictado, conservando lo ya grabado.</para>
    /// </summary>
    private sealed class MicCapture : IDisposable
    {
        private const int SampleRate = 16_000;
        /// <summary>Tope de un dictado: 2 minutos. Acota la memoria (7,7 MB de muestras).</summary>
        private const int MaxSamples = SampleRate * 120;
        /// <summary>Sin un solo bloque del dispositivo en este tiempo, la captura está muerta (el mismo criterio que el visualizador).</summary>
        private const int StallMs = 2000;

        // El dispositivo se abre en Start(), no al construir: en un equipo SIN micrófono
        // crear el objeto ya lanza, y este servicio nace con la ventana principal (el
        // dictado apagado no debe poder impedir que la aplicación arranque).
        private WaveIn? _wave;
        private readonly List<float> _samples = new(SampleRate * 15);
        /// <summary>Muestras: las escribe el hilo de audio, las lee el de la transcripción.</summary>
        private readonly Lock _lock = new();
        /// <summary>Dispositivo y grabación: los tocan la interfaz, el vigía y el cierre.</summary>
        private readonly Lock _deviceLock = new();
        private System.Timers.Timer? _watchdog;
        private volatile float _level;
        private volatile bool _active;
        private long _lastDataTick;
        private int _revives;
        private bool _capped;

        /// <summary>Nivel del último bloque (0..1) para el visualizador.</summary>
        public float Level => _level;

        /// <summary>Reaperturas de la captura durante el dictado (0 = el dispositivo se portó bien).</summary>
        public int Revives => _revives;

        public void Start()
        {
            lock (_lock)
            {
                _samples.Clear();
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

        /// <summary>Cierra el micrófono y devuelve lo capturado.</summary>
        public float[] Stop()
        {
            _active = false; // antes del vigía: un tic en vuelo no debe reabrir nada
            StopWatchdog();
            lock (_deviceLock)
            {
                try
                {
                    CloseDevice();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Error al cerrar el micrófono");
                }
            }
            _level = 0;
            lock (_lock)
            {
                float[] copy = [.. _samples];
                _samples.Clear(); // un segundo Stop sin Start devuelve vacío, no duplica sesión
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
                    // El dispositivo ya podía estar cerrado.
                }
            }
        }

        /// <summary>Abre el dispositivo si hace falta y arranca la grabación.</summary>
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

        /// <summary>Suelta el dispositivo (para el próximo dictado se abre uno nuevo).</summary>
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
            float sum = 0;
            lock (_lock)
            {
                bool room = _samples.Count < MaxSamples;
                if (!room && !_capped)
                {
                    // Tope del dictado alcanzado: lo que venga después ya no cabe.
                    _capped = true;
                    Logger.Warn($"Dictado: la grabación llegó al tope de {MaxSamples / SampleRate} s; lo que siga no se captura");
                }
                for (int i = 0; i < count; i++)
                {
                    short raw = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
                    float sample = raw / 32768f;
                    if (_samples.Count < MaxSamples) _samples.Add(sample);
                    sum += sample * sample;
                }
            }
            // RMS del bloque → nivel del visualizador (el suavizado lo hace el Island).
            float rms = MathF.Sqrt(sum / count);
            _level = Math.Clamp(rms * 9f, 0f, 1f);
        }

        /// <summary>Muestras guardadas hasta ahora, en segundos (para los avisos del vigía).</summary>
        private double CapturedSeconds()
        {
            lock (_lock) return _samples.Count / (double)SampleRate;
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
        /// El dispositivo dejó de entregar audio a mitad del dictado: se reabre y se SIGUE
        /// grabando en la misma sesión, conservando lo capturado. Si parar y arrancar en el
        /// mismo dispositivo falla, se suelta y se abre uno nuevo (así también recoge un
        /// cambio de micrófono predeterminado).
        /// </summary>
        private void Revive()
        {
            lock (_deviceLock)
            {
                if (!_active) return; // la sesión ya cerró: no hay nada que revivir
                Volatile.Write(ref _lastDataTick, Environment.TickCount64); // una intentona por vez
                _revives++;
                bool first = _revives <= 3 || _revives % 10 == 0; // sin llenar el registro
                double captured = CapturedSeconds();
                var wave = _wave;
                if (wave != null)
                {
                    try
                    {
                        wave.StopRecording();
                        wave.StartRecording();
                        if (first)
                            Logger.Warn($"Dictado: el micrófono dejó de entregar audio; se reanuda la captura con {captured:F1} s ya guardados");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Dictado: no se pudo reanudar la captura en el mismo dispositivo; se abre otro");
                        try
                        {
                            CloseDevice();
                        }
                        catch
                        {
                            // El dispositivo ya no estaba.
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
                    // Sin micrófono: lo que queda del dictado no se puede capturar, pero el
                    // vigía sigue intentándolo y lo capturado no se pierde.
                    if (first) Logger.Error(ex, "Dictado: el micrófono no volvió a abrirse");
                }
            }
        }
    }
}
