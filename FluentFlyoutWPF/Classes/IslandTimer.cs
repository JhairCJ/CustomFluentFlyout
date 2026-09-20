// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Estado de la cuenta única del temporizador (spec 002: solo una a la vez).
/// </summary>
public enum IslandTimerState
{
    Idle,
    Running,
    Paused,
    Alerting,
}

/// <summary>
/// Motor de la cuenta atrás única. Guarda hora objetivo + restante congelado
/// (plan 002 D1) para seguir exacto aunque el Island esté oculto o se cambie
/// de funcionalidad. Sin hilos ni temporizadores propios: el host lo sondea
/// con <see cref="Poll"/> desde su tick de UI.
/// </summary>
public sealed class IslandTimer
{
    public const int MaxDurationSeconds = 24 * 3600;
    public const int MinDurationSeconds = 1;
    public const int MaxPresets = 10;

    public IslandTimerState State { get; private set; } = IslandTimerState.Idle;
    public TimeSpan Configured { get; private set; } = TimeSpan.Zero;

    /// <summary>
    /// Etiqueta del aviso: «Timer» si vino de tiempo libre, nombre del preset si no.
    /// </summary>
    public string OriginLabel { get; private set; } = "Timer";

    private DateTime _endUtc;
    private TimeSpan _frozen = TimeSpan.Zero;

    /// <summary>
    /// Se eleva una sola vez al llegar a cero (el host muestra el aviso, RF-6).
    /// </summary>
    public event Action? Finished;

    /// <summary>
    /// Se eleva en CADA cambio de estado (iniciar, pausar, reanudar, reiniciar,
    /// cancelar y vencimiento) — 002 MOD RF-2. El host lo usa para notificar la
    /// actividad, rearmar su despertador único de vencimiento y refrescar solo
    /// lo visible; la cuenta NO depende de ningún latido global (002 MOD RF-13).
    /// </summary>
    public event Action? Changed;

    public TimeSpan Remaining
    {
        get
        {
            switch (State)
            {
                case IslandTimerState.Running:
                    var left = _endUtc - DateTime.UtcNow;
                    return left < TimeSpan.Zero ? TimeSpan.Zero : left;
                case IslandTimerState.Paused:
                    return _frozen;
                case IslandTimerState.Alerting:
                    return TimeSpan.Zero;
                default:
                    return Configured;
            }
        }
    }

    /// <summary>
    /// Fracción transcurrida 0..1 para el progreso central del compacto (RF-8).
    /// </summary>
    public double ElapsedFraction
    {
        get
        {
            if (Configured <= TimeSpan.Zero) return 0;
            if (State == IslandTimerState.Alerting) return 1;
            double total = Configured.TotalSeconds;
            double done = total - Remaining.TotalSeconds;
            return Math.Clamp(done / total, 0, 1);
        }
    }

    public bool IsCounting => State == IslandTimerState.Running || State == IslandTimerState.Paused;

    public static bool IsValidDuration(TimeSpan duration) =>
        duration.TotalSeconds >= MinDurationSeconds && duration.TotalSeconds <= MaxDurationSeconds;

    /// <summary>
    /// Inicia una cuenta nueva sustituyendo la anterior si la había (RF-3).
    /// Devuelve false y no cambia nada si la duración no es válida (RF-2).
    /// </summary>
    public bool Start(TimeSpan duration, string originLabel)
    {
        if (!IsValidDuration(duration)) return false;
        Configured = duration;
        OriginLabel = string.IsNullOrWhiteSpace(originLabel) ? "Timer" : originLabel;
        _endUtc = DateTime.UtcNow + duration;
        _frozen = TimeSpan.Zero;
        State = IslandTimerState.Running;
        Changed?.Invoke();
        return true;
    }

    public void Pause()
    {
        if (State != IslandTimerState.Running) return;
        _frozen = Remaining;
        State = IslandTimerState.Paused;
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (State != IslandTimerState.Paused) return;
        _endUtc = DateTime.UtcNow + _frozen;
        State = IslandTimerState.Running;
        Changed?.Invoke();
    }

    /// <summary>
    /// Reinicia: devuelve el tiempo al valor configurado y deja sin cuenta activa (RF-5).
    /// </summary>
    public void Restart()
    {
        _frozen = TimeSpan.Zero;
        State = IslandTimerState.Idle;
        Changed?.Invoke();
    }

    /// <summary>
    /// Cancela/descarta: sin cuenta activa y sin valor configurado (RF-5, X del aviso).
    /// </summary>
    public void Cancel()
    {
        Configured = TimeSpan.Zero;
        OriginLabel = "Timer";
        _frozen = TimeSpan.Zero;
        State = IslandTimerState.Idle;
        Changed?.Invoke();
    }

    /// <summary>
    /// Comprobación de vencimiento. El host la dispara desde su despertador
    /// ÚNICO sobre la hora objetivo (y desde su red de recuperación de 5 s tras
    /// una suspensión), nunca desde un latido global: al pasar la hora objetivo
    /// detiene la cuenta y pasa a avisando una sola vez (RF-6).
    /// </summary>
    public void Poll(DateTime utcNow)
    {
        if (State != IslandTimerState.Running) return;
        if (utcNow < _endUtc) return;
        _frozen = TimeSpan.Zero;
        State = IslandTimerState.Alerting;
        Changed?.Invoke();
        Finished?.Invoke();
    }

    /// <summary>
    /// Formato del temporizador: siempre h:mm:ss con dos dígitos (00:00:00).
    /// </summary>
    public static string FormatHms(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        long total = (long)t.TotalSeconds;
        return $"{total / 3600:00}:{(total % 3600) / 60:00}:{total % 60:00}";
    }

    /// <summary>
    /// Acepta h:mm:ss, m:ss y segundos sueltos. Solo rango 00:00:01–24:00:00.
    /// </summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.All(char.IsDigit))
        {
            if (!long.TryParse(text, out var seconds)) return false;
            duration = TimeSpan.FromSeconds(seconds);
            return IsValidDuration(duration);
        }
        var parts = text.Split(':');
        if (parts.Length is not (2 or 3)) return false;
        if (!parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return false;
        try
        {
            long seconds = 0;
            foreach (var p in parts) seconds = seconds * 60 + long.Parse(p);
            // m:ss no admite minutos de 2+ dígitos fuera de 0-59 cuando hay horas
            if (parts.Length == 3)
            {
                long m = long.Parse(parts[1]);
                long s = long.Parse(parts[2]);
                if (m > 59 || s > 59) return false;
            }
            else if (long.Parse(parts[1]) > 59) return false;
            duration = TimeSpan.FromSeconds(seconds);
            return IsValidDuration(duration);
        }
        catch { return false; }
    }
}
