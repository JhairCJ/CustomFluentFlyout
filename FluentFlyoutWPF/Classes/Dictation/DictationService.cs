// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using NAudio.Wave;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Whisper.net;

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
///    /// <para>El motor es whisper.cpp (Whisper.net) con un modelo ggml del disco: la
    /// inferencia no sale del equipo —la red solo se usa en Ajustes para descargar el
    /// archivo del modelo—. La captura va por <see cref="WaveIn"/> a 16 kHz mono, que
/// es el formato nativo de Whisper, y el texto se inyecta con <c>SendInput</c> Unicode
/// (sin tocar el portapapeles del usuario).</para>
///
/// <para>Este objeto no conoce el Island: expone fase, nivel y mensaje, y quien lo pinta
/// (IslandWindow.Dictation.cs) se suscribe a <see cref="Changed"/>.</para>
/// </summary>
public sealed class DictationService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Umbral de silencio por PICO (0..1). Un micrófono real nunca da cero en una sala
    /// silenciosa, así que el corte es un pico mínimo: por debajo no hay voz y no se
    /// transcribe —así no aparecen las alucinaciones típicas del modelo con silencio
    /// (RF-5)—. ponytail: umbral fijo de micrófono doméstico; si en algún equipo molesta,
    /// el sitio para el ajuste es la página de dictado.
    /// </summary>
    private const float SilencePeak = 0.012f;

    private readonly MicCapture _capture = new();
    private readonly HashSet<int> _pressed = [];
    private readonly SemaphoreSlim _engineLock = new(1, 1);

    private WhisperFactory? _factory;
    private string? _factoryPath;
    private bool _cancelRequested;
    private bool _disposed;

    public DictationPhase Phase { get; private set; } = DictationPhase.Idle;

    /// <summary>¿Está el dictado en marcha (grabando o transcribiendo)?</summary>
    public bool Active => Phase is DictationPhase.Listening or DictationPhase.Transcribing;

    /// <summary>Nivel del micrófono ahora mismo (0..1), para el visualizador de ondas.</summary>
    public float Level => _capture.Level;

    /// <summary>Clave de localización del mensaje vigente (error o aviso); null si no hay.</summary>
    public string? MessageKey { get; private set; }

    /// <summary>Texto de respaldo en inglés del mensaje vigente.</summary>
    public string? MessageFallback { get; private set; }

    /// <summary>Se dispara con cualquier cambio de fase, nivel de mensaje o fin del dictado.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------
    // Atajo (lo llama el gancho de teclado de MainWindow)
    // ------------------------------------------------------------------

    /// <summary>
    /// Una tecla del gancho global. Mantiene el conjunto de teclas pulsadas —el gancho ve
    /// TODAS, la aplicación tenga el foco o no— y decide con la lógica pura del atajo:
    /// completarlo arranca, soltar cualquiera de sus teclas cierra y una tecla ajena
    /// cancela (RF-1/RF-3/RF-4).
    /// </summary>
    public void HandleKey(int virtualKey, bool down)
    {
        // Las teclas inyectadas por nosotros (SendInput Unicode) no traen código virtual:
        // no son del usuario y no deben contar para el atajo.
        if (_disposed || virtualKey == 0) return;
        // El gancho entrega el modificador físico (0xA2 para Ctrl izquierdo); el atajo
        // guardado dice «Ctrl»: se unifican antes de comparar nada.
        virtualKey = DictationHotkey.Normalize(virtualKey);

        var hotkey = DictationHotkey.Parse(SettingsManager.Current.DictationHotkey);

        if (down)
        {
            _pressed.Add(virtualKey);
            if (Active)
            {
                if (DictationHotkey.Cancels(hotkey, virtualKey)) Cancel();
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
            Logger.Warn(ex, "No se pudo abrir el micrófono");
            Fail("IslandDictationNoMic", "No microphone available");
            return;
        }

        _cancelRequested = false;
        SetPhase(DictationPhase.Listening);
    }

    /// <summary>Cierra el micrófono, descarta el audio y no escribe nada (RF-4).</summary>
    public void Cancel()
    {
        if (Phase != DictationPhase.Listening) return;
        _cancelRequested = true;
        Stop();
    }

    /// <summary>Cierra el micrófono y transcribe lo grabado, si hay voz (RF-3/RF-5).</summary>
    public void Stop()
    {
        if (Phase != DictationPhase.Listening) return;

        float[] samples = _capture.Stop();
        float peak = _capture.Peak;
        bool cancelled = _cancelRequested;
        _cancelRequested = false;

        if (cancelled || peak < SilencePeak)
        {
            SetPhase(DictationPhase.Idle);
            return;
        }

        SetPhase(DictationPhase.Transcribing);
        string language = SettingsManager.Current.DictationLanguage;
        _ = Task.Run(async () =>
        {
            try
            {
                string text = await TranscribeAsync(samples, language, CancellationToken.None);
                if (_disposed) return;
                if (!string.IsNullOrWhiteSpace(text)) SendText(text.Trim());
                SetPhase(DictationPhase.Idle);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Falló la transcripción del dictado");
                Fail("IslandDictationFailed", "Could not transcribe the audio");
            }
        });
    }

    /// <summary>
    /// Carga el modelo en segundo plano para que el primer dictado no pague la carga
    /// entera (un `small` son cientos de MB desde disco). Se llama al cambiar de modelo
    /// o al activar el dictado en Ajustes; si falla, no se avisa: el fallo se ve al dictar.
    /// </summary>
    public void Preload()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureFactoryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "No se pudo precargar el modelo de dictado");
            }
        });
    }

    /// <summary>Ajuste en caliente: si el dictado dejó de estar activado, se corta en seco.</summary>
    public void RefreshSettings()
    {
        if (!SettingsManager.Current.DictationEnabled && Active)
        {
            _cancelRequested = true;
            if (Phase == DictationPhase.Listening) Stop();
            else SetPhase(DictationPhase.Idle);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _capture.Dispose();
        _engineLock.Dispose();
        _factory?.Dispose();
        _factory = null;
    }

    // ------------------------------------------------------------------
    // Motor local (whisper.cpp)
    // ------------------------------------------------------------------

    private async Task<WhisperFactory> EnsureFactoryAsync(CancellationToken cancellationToken)
    {
        string path = DictationModelStore.ResolveActivePath(SettingsManager.Current.DictationModel)
            ?? throw new FileNotFoundException("No hay modelo de dictado activo");

        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            if (_factory != null && _factoryPath == path) return _factory;
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(path);
            _factoryPath = path;
            Logger.Info($"Modelo de dictado cargado: {Path.GetFileName(path)} ({WhisperFactory.GetRuntimeInfo()})");
            return _factory;
        }
        finally
        {
            _engineLock.Release();
        }
    }

    /// <summary>
    /// Transcribe las muestras (16 kHz mono) con el idioma configurado: «auto» deja que el
    /// modelo lo detecte, así el mismo modelo multilingüe sirve para español e inglés (RF-7).
    /// </summary>
    private async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken cancellationToken)
    {
        WhisperFactory factory = await EnsureFactoryAsync(cancellationToken);
        string? activePath = _factoryPath;
        // Un modelo «.en» solo entiende inglés: pedirle español daría basura.
        string effectiveLanguage = activePath != null && DictationModelStore.IsEnglishOnly(activePath)
            ? "en"
            : string.IsNullOrWhiteSpace(language) ? "auto" : language;

        using var processor = factory.CreateBuilder()
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 2, 8))
            .WithLanguage(effectiveLanguage)
            .WithNoContext() // cada dictado es independiente: sin contexto arrastrado
            .Build();

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
        {
            text.Append(segment.Text);
        }
        return text.ToString();
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
    /// </summary>
    private sealed class MicCapture : IDisposable
    {
        private const int SampleRate = 16_000;
        /// <summary>Tope de un dictado: 2 minutos. Acota la memoria (7,7 MB de muestras).</summary>
        private const int MaxSamples = SampleRate * 120;

        // El dispositivo se abre en Start(), no al construir: en un equipo SIN micrófono
        // crear el objeto ya lanza, y este servicio nace con la ventana principal (el
        // dictado apagado no debe poder impedir que la aplicación arranque).
        private WaveIn? _wave;
        private readonly List<float> _samples = new(SampleRate * 15);
        private readonly Lock _lock = new();
        private volatile float _level;
        private volatile float _peak;

        /// <summary>Nivel del último bloque (0..1) para el visualizador.</summary>
        public float Level => _level;

        /// <summary>Pico de todo el dictado: decide si había voz (RF-5).</summary>
        public float Peak => _peak;

        public void Start()
        {
            lock (_lock)
            {
                _samples.Clear();
                _peak = 0;
                _level = 0;
            }
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
        }

        /// <summary>Cierra el micrófono y devuelve lo capturado.</summary>
        public float[] Stop()
        {
            try
            {
                var wave = _wave;
                if (wave != null)
                {
                    wave.DataAvailable -= OnData;
                    wave.StopRecording();
                    wave.Dispose();
                    _wave = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Error al cerrar el micrófono");
            }
            _level = 0;
            lock (_lock)
            {
                return [.. _samples];
            }
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            int count = e.BytesRecorded / 2;
            if (count <= 0) return;
            float sum = 0;
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    short raw = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
                    float sample = raw / 32768f;
                    if (_samples.Count < MaxSamples) _samples.Add(sample);
                    sum += sample * sample;
                    float absolute = Math.Abs(sample);
                    if (absolute > _peak) _peak = absolute;
                }
            }
            // RMS del bloque → nivel del visualizador (el suavizado lo hace el Island).
            float rms = MathF.Sqrt(sum / count);
            _level = Math.Clamp(rms * 9f, 0f, 1f);
        }

        public void Dispose()
        {
            var wave = _wave;
            _wave = null;
            if (wave == null) return;
            wave.DataAvailable -= OnData;
            try
            {
                wave.StopRecording();
            }
            catch
            {
                // El dispositivo ya podía estar cerrado.
            }
            wave.Dispose();
        }
    }
}
