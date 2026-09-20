using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

const string AepSelector = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
string[] props =
[
    "System.ItemNameDisplay",
    "System.Devices.Aep.IsConnected",
    "System.Devices.Aep.DeviceAddress",
    "System.Devices.BatteryPlusCharging",
    "System.Devices.BatteryPlusChargingText",
    "System.Devices.Aep.ContainerId",
];

// A) Dispositivos AEP + contenedores: a ver dónde aparece alguna batería.
Console.WriteLine("=== A) AEP + contenedores ===");
foreach (var kind in new[] { DeviceInformationKind.AssociationEndpoint, DeviceInformationKind.AssociationEndpointContainer })
{
    try
    {
        var all = await DeviceInformation.FindAllAsync(AepSelector, props, kind);
        Console.WriteLine($"-- {kind}: {all.Count}");
        foreach (var info in all)
        {
            bool connected = info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is bool b && b;
            Console.WriteLine($"   {info.Name} | connected={connected} | kind={info.Kind}");
            foreach (var kv in info.Properties)
                if (kv.Key.Contains("attery", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Icon", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"      {kv.Key} = {kv.Value ?? "<null>"}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"   {kind} FALLO: {ex.Message}"); }
}

// B) Conjunto completo de propiedades de un dispositivo conectado (sin lista filtrada).
Console.WriteLine("=== B) Propiedades completas del conectado ===");
try
{
    var conn = await DeviceInformation.FindAllAsync(
        BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
        props, DeviceInformationKind.AssociationEndpoint);
    foreach (var info in conn)
    {
        Console.WriteLine($"-- {info.Name} id={info.Id}");
        var full = await DeviceInformation.CreateFromIdAsync(info.Id);
        foreach (var kv in full.Properties)
            Console.WriteLine($"     {kv.Key} = {kv.Value ?? "<null>"}");
    }
}
catch (Exception ex) { Console.WriteLine($"   FALLO: {ex.Message}"); }

// C) GATT: servicio de batería estándar sobre el dispositivo conectado.
Console.WriteLine("=== C) GATT batería ===");
try
{
    var conn = await DeviceInformation.FindAllAsync(
        BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), props);
    foreach (var info in conn)
    {
        string? address = null;
        if (info.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var raw) && raw is string s)
            address = s;
        Console.WriteLine($"-- {info.Name} address={address ?? "<null>"} id={info.Id}");
        if (address == null) continue;
        ulong mac = Convert.ToUInt64(address.Replace(":", ""), 16);
        using var ble = await BluetoothLEDevice.FromBluetoothAddressAsync(mac);
        if (ble == null) { Console.WriteLine("      BluetoothLEDevice null (¿sin capacidad/permiso?)"); continue; }
        Console.WriteLine($"      BLE name={ble.Name} status={ble.ConnectionStatus}");
        var services = await ble.GetGattServicesForUuidAsync(
            GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
        Console.WriteLine($"      status={services.Status} servicios={services.Services.Count}");
        foreach (var service in services.Services)
        {
            var ch = await service.GetCharacteristicsForUuidAsync(
                GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
            Console.WriteLine($"         chars={ch.Characteristics.Count} status={ch.Status}");
            foreach (var characteristic in ch.Characteristics)
            {
                var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                Console.WriteLine($"         lectura={read.Status} bytes={read.Value?.Length}");
                if (read.Value is { Length: > 0 } buffer)
                {
                    using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
                    Console.WriteLine($"         BATTERY = {reader.ReadByte()} %");
                }
            }
        }
    }
}
catch (Exception ex) { Console.WriteLine($"   FALLO: {ex.GetType().Name}: {ex.Message}"); }
