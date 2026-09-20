// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using Microsoft.Win32;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Estado de energía del equipo en un instante: si está enchufado a la corriente,
/// si tiene batería, cuánta queda (null si Windows no lo sabe) y si está cargando.
/// </summary>
public sealed record IslandPowerStatus(bool AcOnline, bool HasBattery, int? BatteryPercent, bool Charging)
{
    /// <summary>Texto del porcentaje para los avisos; null si el nivel no se conoce.</summary>
    public string? PercentText => BatteryPercent is int percent ? $"{percent} %" : null;
}

/// <summary>
/// Vigía del cargador (change island-cargador): avisa cuando el equipo se enchufa a
/// la corriente y cuando se desenchufa, con el nivel de batería en ese momento.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Evento, no sondeo</b>: la fuente es
/// <see cref="SystemEvents.PowerModeChanged"/> —el aviso de Windows de que cambió el
/// estado de energía o de que el equipo volvió de suspensión—, no un reloj
/// preguntando cada segundo. El estado se lee de Win32
/// (<see cref="NativeMethods.GetSystemPowerStatus"/>) al recibirlo.</item>
/// <item><b>Arranque silencioso</b>: la primera lectura solo siembra el estado. El
/// aviso es para el cambio que ocurre con el Island funcionando, no para reabrir lo
/// que ya estaba (igual que el vigía de Bluetooth).</item>
/// <item><b>Solo donde tiene sentido</b>: un equipo sin batería (ni un sobremesa) no
/// avisa: ahí «enchufar» no dice nada.</item>
/// </list>
/// </summary>
public sealed class IslandPowerMonitor : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private bool _started;
    private bool _disposed;
    private bool _hasStatus;
    private bool _acOnline;
    private bool _hasBattery;

    /// <summary>El equipo se enchufó a la corriente (no en la siembra de arranque).</summary>
    public event Action<IslandPowerStatus>? ChargerConnected;

    /// <summary>El equipo se quedó a batería: se desenchufó (no en la siembra de arranque).</summary>
    public event Action<IslandPowerStatus>? ChargerDisconnected;

    /// <summary>¿El vigía está en marcha?</summary>
    public bool IsRunning => _started;

    /// <summary>Arranca el vigía (idempotente) y siembra el estado actual sin avisar.</summary>
    public void Start()
    {
        if (_disposed || _started) return;
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _started = true;
            // Siembra: el primer estado conocido no es un cambio que el usuario deba ver.
            if (Read(out _, out _)) _hasStatus = true;
        }
        catch (Exception ex)
        {
            _started = false;
            Log.Warn(ex, "Cargador: no se pudo escuchar el estado de energía");
        }
    }

    /// <summary>Para el vigía y olvida el estado: al reanudar, lo que ya estaba se siembra otra vez.</summary>
    public void Stop()
    {
        if (_started)
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; }
            catch (Exception ex) { Log.Warn(ex, "Cargador: fallo al soltar la escucha de energía"); }
        }
        _started = false;
        _hasStatus = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposed) return;
        // StatusChange es el cambio de energía (enchufar/desenchufar, nivel); Resume
        // llega al volver de suspensión y obliga a releer porque durante el sueño no
        // hay avisos. Suspend no aporta nada aquí.
        if (e.Mode != PowerModes.StatusChange && e.Mode != PowerModes.Resume) return;
        if (!Read(out bool acOnline, out IslandPowerStatus status)) return;
        if (!_hasStatus)
        {
            _hasStatus = true;
            _acOnline = acOnline;
            _hasBattery = status.HasBattery;
            return;
        }
        bool wasOnline = _acOnline;
        _acOnline = acOnline;
        _hasBattery = status.HasBattery;
        // Sin batería no hay nada que avisar (un sobremesa está siempre enchufado).
        if (!status.HasBattery && !_hasBattery) return;
        if (acOnline == wasOnline) return;
        try
        {
            if (acOnline) ChargerConnected?.Invoke(status);
            else ChargerDisconnected?.Invoke(status);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Cargador: fallo al publicar el cambio de energía");
        }
    }

    /// <summary>Lee el estado de energía del sistema (false si Windows no lo da).</summary>
    private static bool Read(out bool acOnline, out IslandPowerStatus status)
    {
        acOnline = false;
        status = new IslandPowerStatus(false, false, null, false);
        try
        {
            if (!NativeMethods.GetSystemPowerStatus(out var raw)) return false;
            if (raw.AcLineStatus == 255) return false;
            bool hasBattery = (raw.BatteryFlag & 128) == 0 && raw.BatteryFlag != 255;
            int? percent = raw.BatteryLifePercent == 255 ? null : raw.BatteryLifePercent;
            bool charging = (raw.BatteryFlag & 8) != 0;
            acOnline = raw.AcLineStatus == 1;
            status = new IslandPowerStatus(acOnline, hasBattery, percent, charging);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Cargador: no se pudo leer el estado de energía");
            return false;
        }
    }
}
