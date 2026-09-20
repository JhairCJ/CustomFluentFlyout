// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Wpf.Ui.Controls;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Dispositivo Bluetooth conectado tal y como lo publica el vigía: identidad para
/// reconocerlo, nombre visible, glifo de su tipo y batería cuando Windows la
/// conoce (los dispositivos que no la informan salen con <c>null</c> y el Island
/// no pinta aro: nada de un aro «0 %» inventado).
/// </summary>
public sealed record IslandBluetoothDevice(
    string Id,
    string Name,
    SymbolRegular Glyph,
    int? BatteryPercent)
{
    /// <summary>Texto de batería para el tooltip; null si el dispositivo no la informa.</summary>
    public string? BatteryText => BatteryPercent is int p ? $"Batería {p} %" : null;
}

/// <summary>
/// Vigía de conexiones Bluetooth por eventos (change island-bluetooth-conectado):
/// un <see cref="DeviceWatcher"/> de AssociationEndpoint sobre los dispositivos
/// Bluetooth presentes en el sistema.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Conexión = evento</b> (ADDED RF-1): el <c>Added</c>/<c>Updated</c> del
/// vigía ES el evento de conexión. Nada de sondeo: sin cambio no hay trabajo, y
/// el coste en reposo es cero.</item>
/// <item><b>Conectado de verdad</b> (ADDED RF-2): se exige
/// <c>System.Devices.Aep.IsConnected</c>. Un móvil emparejado que está cerca
/// aparece como presente pero NO conectado, y no debe avisar.</item>
/// <item><b>Arranque silencioso</b> (ADDED RF-3): durante la enumeración inicial
/// los dispositivos ya conectados solo se siembran; el aviso es para la conexión
/// que ocurre con el Island funcionando, no para reabrir lo que ya estaba.</item>
/// <item><b>Batería tardía</b> (ADDED RF-4): Windows rellena la batería unos
/// segundos después de conectar. Si no viene en el evento se reintenta un par de
/// veces (contadas, nunca un bucle) y el refinamiento se publica como
/// actualización, que solo repinta lo que ya está a la vista.</item>
/// </list>
///
/// <para>Los eventos del vigía llegan en un hilo de fondo: el consumidor (el
/// Island) es quien los cruza a su hilo de UI. Cualquier fallo (proyección no
/// disponible, vigía denegado) se registra y deja el vigía parado: la
/// funcionalidad simplemente no avisa, sin romper el resto del contenedor.</para>
/// </summary>
public sealed class IslandBluetoothMonitor : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private const string UnknownName = "Dispositivo Bluetooth";

    // Propiedades que el vigía debe traer en cada evento: identidad, estado de
    // conexión, tipo de dispositivo (para el glifo) y batería si Windows la tiene.
    private static readonly string[] RequestedProperties =
    [
        "System.ItemNameDisplay",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.Bluetooth.Cod.Major",
        "System.Devices.Aep.Bluetooth.Cod.Services.Audio",
        "System.Devices.BatteryPlusCharging",
        "System.Devices.BatteryPlusChargingText",
    ];

    // Reintentos de batería: acumulativos (2,5 s, 9 s y 21 s tras conectar) y
    // contados. Algunos auriculares tardan en informar su nivel.
    private static readonly int[] BatteryRetryDelaysMs = [2500, 6500, 12000];

    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SymbolRegular> _glyphs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _connected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _battery = new(StringComparer.OrdinalIgnoreCase);

    private DeviceWatcher? _watcher;
    private bool _seeding = true;
    private bool _disposed;
    private int _generation;

    /// <summary>Se conectó un dispositivo (no en la siembra de arranque).</summary>
    public event Action<IslandBluetoothDevice>? Connected;

    /// <summary>Cambió algo del dispositivo ya conectado (batería que llega tarde).</summary>
    public event Action<IslandBluetoothDevice>? Updated;

    /// <summary>El dispositivo se desconectó o desapareció (id).</summary>
    public event Action<string>? Disconnected;

    /// <summary>¿El vigía está en marcha? Con él parado no hay avisos.</summary>
    public bool IsRunning => _watcher != null;

    /// <summary>
    /// Arranca el vigía. Idempotente: llamarlo dos veces no crea dos vigías. Sin
    /// proyección de Windows disponible se registra y se queda parado.
    /// </summary>
    public void Start()
    {
        if (_disposed || _watcher != null) return;
        try
        {
            string selector = BluetoothDevice.GetDeviceSelector();
            var watcher = DeviceInformation.CreateWatcher(
                selector, RequestedProperties, DeviceInformationKind.AssociationEndpoint);
            watcher.Added += OnAdded;
            watcher.Updated += OnUpdated;
            watcher.Removed += OnRemoved;
            watcher.EnumerationCompleted += OnEnumerationCompleted;
            watcher.Stopped += OnStopped;
            _seeding = true;
            _watcher = watcher;
            watcher.Start();
            Log.Info("Bluetooth: vigía de conexiones en marcha");
        }
        catch (Exception ex)
        {
            _watcher = null;
            _seeding = false;
            Log.Warn(ex, "Bluetooth: vigía no disponible; no habrá avisos de conexión");
        }
    }

    /// <summary>
    /// Para el vigía y olvida lo observado: al reanudar, lo que ya estaba
    /// conectado se siembra otra vez (no es un evento nuevo).
    /// </summary>
    public void Stop()
    {
        var watcher = _watcher;
        _watcher = null;
        _generation++;
        if (watcher != null)
        {
            try
            {
                watcher.Added -= OnAdded;
                watcher.Updated -= OnUpdated;
                watcher.Removed -= OnRemoved;
                watcher.EnumerationCompleted -= OnEnumerationCompleted;
                watcher.Stopped -= OnStopped;
                watcher.Stop();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Bluetooth: fallo al parar el vigía");
            }
        }
        _names.Clear();
        _glyphs.Clear();
        _connected.Clear();
        _battery.Clear();
        _seeding = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // --- eventos del vigía ---

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        if (_disposed) return;
        try
        {
            string id = info.Id;
            _names[id] = CleanName(info.Name);
            _glyphs[id] = GlyphFor(info.Properties);
            bool connected = ReadConnected(info.Properties);
            _connected[id] = connected;
            if (ReadBattery(info.Properties) is int battery) _battery[id] = battery;
            // La conexión de verdad avisa; la siembra de arranque solo observa.
            if (connected && !_seeding)
            {
                RaiseConnected(id);
                _ = RetryBatteryAsync(id, _generation);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Bluetooth: fallo al procesar un dispositivo presente");
        }
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (_disposed) return;
        try
        {
            string id = update.Id;
            var props = update.Properties;
            int? incoming = ReadBattery(props);
            if (incoming is int battery
                && (!_battery.TryGetValue(id, out int? known) || known != battery))
            {
                _battery[id] = battery;
                // Batería nueva de un dispositivo ya conectado: se refina (solo
                // repinta si su aviso sigue a la vista).
                if (_connected.TryGetValue(id, out bool live) && live && _names.ContainsKey(id))
                {
                    Updated?.Invoke(Build(id));
                    return;
                }
            }
            if (ReadConnectedOrNull(props) is not bool now) return;
            bool was = _connected.TryGetValue(id, out bool previous) && previous;
            _connected[id] = now;
            if (now && !was)
            {
                if (!_seeding) RaiseConnected(id);
                _ = RetryBatteryAsync(id, _generation);
                return;
            }
            if (!now && was) Disconnected?.Invoke(id);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Bluetooth: fallo al procesar una actualización");
        }
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (_disposed) return;
        string id = update.Id;
        bool was = _connected.TryGetValue(id, out bool previous) && previous;
        _names.Remove(id);
        _glyphs.Remove(id);
        _connected.Remove(id);
        _battery.Remove(id);
        if (was) Disconnected?.Invoke(id);
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args) => _seeding = false;

    private void OnStopped(DeviceWatcher sender, object args)
    {
        // El vigía se cayó (permiso, servicio): se deja constancia y no se
        // reintenta en bucle. VOLVER a activar la funcionalidad en ajustes lo
        // arranca de nuevo.
        Log.Warn("Bluetooth: el vigía se detuvo; no habrá más avisos de conexión");
        _watcher = null;
        _seeding = true;
        _generation++;
    }

    // --- publicación ---

    private void RaiseConnected(string id)
    {
        if (_disposed || !_names.ContainsKey(id)) return;
        Connected?.Invoke(Build(id));
    }

    private IslandBluetoothDevice Build(string id) => new(
        id,
        _names.TryGetValue(id, out var name) ? name : UnknownName,
        _glyphs.TryGetValue(id, out var glyph) ? glyph : SymbolRegular.Bluetooth24,
        _battery.TryGetValue(id, out int? battery) ? battery : null);

    /// <summary>
    /// Reintento contado de la batería (ADDED RF-4): Windows la rellena unos
    /// segundos después de la conexión. Sin batería conocida se reintenta; con
    /// ella ya no se consulta nada más.
    /// </summary>
    private async Task RetryBatteryAsync(string id, int generation)
    {
        foreach (int delay in BatteryRetryDelaysMs)
        {
            try { await Task.Delay(delay).ConfigureAwait(false); } catch { return; }
            if (_disposed || generation != _generation) return;
            if (_battery.TryGetValue(id, out int? known) && known != null) return;
            if (!_connected.TryGetValue(id, out bool live) || !live) return;
            try
            {
                var info = await DeviceInformation.CreateFromIdAsync(id, RequestedProperties).AsTask()
                    .ConfigureAwait(false);
                if (_disposed || generation != _generation) return;
                if (info == null) return;
                if (ReadBattery(info.Properties) is not int battery) continue;
                _battery[id] = battery;
                Updated?.Invoke(Build(id));
                return;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Bluetooth: no se pudo releer la batería del dispositivo");
                return;
            }
        }
    }

    // --- lectura de propiedades ---

    private static bool ReadConnected(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue("System.Devices.Aep.IsConnected", out var value) && value is bool on && on;

    private static bool? ReadConnectedOrNull(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue("System.Devices.Aep.IsConnected", out var value) && value is bool on ? on : null;

    /// <summary>
    /// Batería restante en porcentaje, o null si el dispositivo no la informa.
    /// Windows la publica en <c>System.Devices.BatteryPlusCharging</c> (la usan
    /// la propia página de Bluetooth de Ajustes) y, en algunos dispositivos, solo
    /// como texto («78 %»).
    /// </summary>
    private static int? ReadBattery(IReadOnlyDictionary<string, object> props)
    {
        if (props.TryGetValue("System.Devices.BatteryPlusCharging", out var raw))
        {
            int? value = raw switch
            {
                byte b => b,
                ushort u => u,
                short sh => sh,
                int i => i,
                uint ui => (int)ui,
                _ => null,
            };
            if (value is int percent && percent is >= 0 and <= 100) return percent;
        }
        if (props.TryGetValue("System.Devices.BatteryPlusChargingText", out var text) && text is string s)
        {
            string digits = new(s.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int percent) && percent is >= 0 and <= 100) return percent;
        }
        return null;
    }

    /// <summary>
    /// Glifo del dispositivo por su clase Bluetooth: auriculares o altavoz (clase
    /// Audio/Video), ordenador, móvil o el genérico. Es solo el icono de la
    /// izquierda del aviso: sin dato se usa el genérico.
    /// </summary>
    private static SymbolRegular GlyphFor(IReadOnlyDictionary<string, object> props)
    {
        if (props.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Services.Audio", out var audio)
            && audio is bool offersAudio && offersAudio)
            return SymbolRegular.Headphones24;
        int major = props.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Major", out var raw)
            ? raw switch
            {
                ushort u => u,
                byte b => b,
                short sh => sh,
                int i => i,
                uint ui => (int)ui,
                _ => -1,
            }
            : -1;
        return major switch
        {
            1 => SymbolRegular.Laptop24,     // Computer
            2 => SymbolRegular.Phone24,      // Phone
            4 => SymbolRegular.Headphones24, // Audio/Video
            _ => SymbolRegular.Bluetooth24,
        };
    }

    /// <summary>
    /// Nombre visible del dispositivo. Un nombre vacío o que es solo su dirección
    /// no dice nada al usuario: se usa el genérico.
    /// </summary>
    private static string CleanName(string? name)
    {
        string clean = (name ?? "").Trim();
        if (clean.Length == 0) return UnknownName;
        if (clean.All(c => char.IsAsciiHexDigit(c) || c is ':' or '-' or ' ')) return UnknownName;
        return clean;
    }
}
