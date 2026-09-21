// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Media;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Dispositivos Bluetooth conectados en el Island: sexto contenido del contenedor,
/// junto a música, temporizador, cajón, estante y calendario.
///
/// <para>Reglas que sostiene (change island-bluetooth-conectado):</para>
/// <list type="bullet">
/// <item><b>Siempre aviso temporal</b> (RF-1): su vista es una notificación —icono
/// a la izquierda, nombre al medio y batería a la derecha si el dispositivo la
/// informa— que se retira al vencer el plazo configurado del aviso. Da igual el
/// modo de visibilidad elegido: un dispositivo conectado no se queda pegado a la
/// vista (de ahí el aviso FORZADO, que vence también en «Visible mientras
/// activo»).</item>
/// <item><b>Conectado, no presente</b> (RF-2): el vigía
/// (<see cref="IslandBluetoothMonitor"/>) exige conexión real; los dispositivos ya
/// conectados al arrancar no avisan (RF-3).</item>
/// <item><b>Nunca interrumpe</b> (RF-4): una conexión no sustituye la caja
/// expandida que el usuario está mirando ni la supresión (juego a pantalla
/// completa); sin vista expandida, el aviso entra en compacto.</item>
/// <item><b>Batería opcional</b> (RF-5): el aro de dos colores —verde lo que
/// queda, gris claro el resto— solo aparece si Windows conoce el nivel; si no,
/// esa zona no muestra nada.</item>
/// </list>
///
/// <para>El resto del contenedor no cambia: la funcionalidad cumple el mismo
/// contrato (<see cref="IslandFeatureIds.Bluetooth"/>) que las demás, así que
/// aparece en la navegación y en el orden configurable como una más.</para>
/// </summary>
public partial class IslandWindow
{
    // --- aro de batería: diámetro de la zona, grosor del trazo y el verde ---
    private const double BatteryRingSize = 18;
    private const double BatteryRingThickness = 2;
    /// <summary>Verde de la parte restante de la batería (el resto es el trazo gris del XAML).</summary>
    private static readonly Brush BatteryRemainingBrush = Frozen(Color.FromRgb(0x6C, 0xCB, 0x5F));
    /// <summary>Rojo del aviso de desconexión: rayo y porcentaje (001 MOD RF-6).</summary>
    private static readonly Brush BluetoothAlertBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush BluetoothGlyphBrush = Frozen(Colors.White);

    /// <summary>
    /// Variante del aviso de Bluetooth a la vista (001 MOD RF-6):
    /// <list type="bullet">
    /// <item><see cref="Connected"/> — dispositivo conectado: su icono, su nombre y
    /// su batería en aro (si Windows la conoce).</item>
    /// <item><see cref="Charging"/> — conectado Y cargando: rayo verde a la
    /// izquierda y el porcentaje en números verdes a la derecha.</item>
    /// <item><see cref="Disconnected"/> — se desconectó: rayo rojo a la izquierda y
    /// el último porcentaje conocido en rojo a la derecha.</item>
    /// </list>
    /// </summary>
    private enum BluetoothNoticeKind { Connected, Charging, Disconnected }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Último dispositivo conectado: es el que enseña el aviso y el que sostiene la funcionalidad.</summary>
    private IslandBluetoothDevice? _bluetoothDevice;
    private IslandBluetoothMonitor? _bluetooth;
    // Anti-rebote del aviso: al emparejar, algunos dispositivos se conectan,
    // sueltan y vuelven a conectarse en un par de segundos (perfiles), y eso NO
    // son dos conexiones que el usuario deba ver.
    private string? _bluetoothShownId;
    private DateTime _bluetoothShownAt = DateTime.MinValue;
    private static readonly TimeSpan BluetoothReboundGuard = TimeSpan.FromSeconds(2);
    // Variante del aviso a la vista (conectado, cargando o desconectado).
    private BluetoothNoticeKind _bluetoothNotice = BluetoothNoticeKind.Connected;

    /// <summary>Funcionalidad «dispositivos Bluetooth» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? BluetoothFeature => FeatureById(IslandFeatureIds.Bluetooth);

    /// <summary>¿La funcionalidad está encendida con el contenedor?</summary>
    private bool BluetoothModeAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandBluetoothEnabled;

    /// <summary>¿Sigue vivo el aviso del dispositivo mostrado? Su vista vive solo mientras SU plazo corre.</summary>
    private bool BluetoothNoticeAlive() => NoticeAliveFor(IslandContentMode.Bluetooth);

    private bool BluetoothActive() =>
        BluetoothModeAvailable() && _bluetoothDevice != null && BluetoothNoticeAlive();

    internal IslandFeatureState GetBluetoothFeatureState()
    {
        bool enabled = BluetoothModeAvailable();
        // Disponible en cuanto se conoce un dispositivo (el último conectado): sin
        // ninguno no hay nada que enseñar y el contenedor no debe abrir una vista
        // vacía (001 MOD RF-9/RF-13).
        bool available = enabled && _bluetoothDevice != null;
        return new IslandFeatureState(enabled, available, BluetoothActive(),
            Selected: _selectedFeature?.Id == IslandFeatureIds.Bluetooth, Exclusive: false);
    }

    /// <summary>
    /// Compacto del contrato: re-presenta el aviso del último dispositivo SIN
    /// reiniciar su plazo (la reapertura no es un evento nuevo: 001 RF-2). Devuelve
    /// false sin dispositivo, para que el contenedor pruebe la siguiente usable.
    /// </summary>
    internal bool ShowBluetoothCompactFromContract()
    {
        if (!BluetoothModeAvailable() || _bluetoothDevice == null) return false;
        ShowBluetoothCompact(restartNotice: false);
        return true;
    }

    /// <summary>
    /// Vincula el vigía de conexiones y lo arranca si la funcionalidad está
    /// encendida. Los eventos llegan desde su hilo de fondo: se cruzan al de UI
    /// antes de tocar nada (igual que los recordatorios del calendario).
    /// </summary>
    private void InitBluetooth()
    {
        _bluetooth = new IslandBluetoothMonitor();
        _bluetooth.Connected += OnBluetoothConnected;
        _bluetooth.Updated += OnBluetoothUpdated;
        _bluetooth.Disconnected += OnBluetoothDisconnected;
        if (BluetoothModeAvailable()) _bluetooth.Start();
    }

    private void ShutdownBluetooth()
    {
        var monitor = _bluetooth;
        _bluetooth = null;
        monitor?.Dispose();
    }

    /// <summary>
    /// Ajuste de la funcionalidad en caliente: encendida arranca el vigía; apagada
    /// lo para (no se observa nada) y repliega su vista si estaba puesta, sin
    /// dejar una superficie vacía.
    /// </summary>
    public void RefreshBluetoothContent() => Dispatcher.Invoke(() =>
    {
        if (BluetoothModeAvailable()) _bluetooth?.Start();
        else
        {
            _bluetooth?.Stop();
            if (IsBoxShown && ViewShowsFeature(IslandFeatureIds.Bluetooth)) FallbackFromBluetoothView();
        }
        UpdateArrows();
        PostActivity(IslandActivityReason.Bluetooth | IslandActivityReason.Settings);
    });

    // ------------------------------------------------------------------
    // Eventos del vigía (hilo de fondo -> hilo de UI)
    // ------------------------------------------------------------------

    /// <summary>
    /// Se conectó un dispositivo: se adopta como el mostrado y se enseña el aviso
    /// temporal. Con la caja expandida, suprimida o con la alerta del temporizador
    /// delante NO se interrumpe nada: el usuario ya tiene una vista decidida
    /// (RF-4, 001 MOD RF-28).
    /// </summary>
    private void OnBluetoothConnected(IslandBluetoothDevice device) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_disposed) return;
        _bluetoothDevice = device;
        // Conectado cargando: el aviso es el del rayo y el porcentaje (001 MOD RF-6).
        _bluetoothNotice = device.Charging == true
            ? BluetoothNoticeKind.Charging : BluetoothNoticeKind.Connected;
        NoteFeatureEvent(IslandFeatureIds.Bluetooth);
        if (!SettingsManager.Current.IslandEnabled || !BluetoothModeAvailable()) return;
        if (Suppressed() || HasExclusive() || _expanded) return;
        bool rebound = _bluetoothShownId == device.Id
            && DateTime.UtcNow - _bluetoothShownAt < BluetoothReboundGuard;
        if (rebound) return;
        _bluetoothShownId = device.Id;
        _bluetoothShownAt = DateTime.UtcNow;
        ShowBluetoothCompact(restartNotice: true);
    }));

    /// <summary>
    /// El dispositivo ya conectado cambió (batería que Windows publica unos
    /// segundos después): se refina el dato. No es un evento nuevo, así que solo
    /// repinta si el aviso sigue a la vista y JAMÁS lo re-despliega ni reinicia su
    /// plazo (001 MOD RF-2/RF-28).
    /// </summary>
    private void OnBluetoothUpdated(IslandBluetoothDevice device) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_disposed) return;
        if (_bluetoothDevice?.Id != device.Id) return;
        _bluetoothDevice = device;
        // El dispositivo empezó a cargar con su aviso a la vista (o dejó de
        // cargar): el aviso cambia de variante en el sitio, sin reiniciar su plazo
        // (001 MOD RF-6).
        if (ViewShowsFeature(IslandFeatureIds.Bluetooth) && IsBoxShown
            && _bluetoothNotice != BluetoothNoticeKind.Disconnected)
            _bluetoothNotice = device.Charging == true
                ? BluetoothNoticeKind.Charging : BluetoothNoticeKind.Connected;
        PostActivity(IslandActivityReason.Bluetooth);
    }));

    /// <summary>
    /// Se desconectó (o desapareció) el dispositivo mostrado: su aviso ya no dice
    /// la verdad, así que se retira; si era otro el mostrado, no se toca nada.
    /// </summary>
    private void OnBluetoothDisconnected(string id) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_disposed) return;
        var device = _bluetoothDevice;
        if (device?.Id != id) return;
        // El aviso de desconexión es un evento nuevo: rayo rojo y el último
        // porcentaje conocido en rojo, con un plazo entero por delante (001 MOD RF-6).
        _bluetoothNotice = BluetoothNoticeKind.Disconnected;
        _bluetoothShownId = id;
        _bluetoothShownAt = DateTime.UtcNow;
        if (!SettingsManager.Current.IslandEnabled || !BluetoothModeAvailable()) return;
        if (Suppressed() || HasExclusive() || _expanded) return;
        ShowBluetoothCompact(restartNotice: true);
    }));

    // ------------------------------------------------------------------
    // Presentación
    // ------------------------------------------------------------------

    /// <summary>
    /// Aviso temporal de Bluetooth: presenta el compacto del dispositivo con el
    /// aviso FORZADO (vence también en «Visible mientras activo», RF-1). Solo la
    /// conexión nueva reinicia el plazo; volver a presentarlo lo conserva.
    /// </summary>
    private void ShowBluetoothCompact(bool restartNotice)
    {
        if (!BluetoothModeAvailable() || _bluetoothDevice == null) { SnapHidden(); return; }
        RefreshBluetoothUI();
        ShowCompactView(IslandContentMode.Bluetooth, BluetoothFeature, RefreshBluetoothUI,
            forceNotice: true, restartNotice: restartNotice);
    }

    /// <summary>
    /// Pinta el contenido del aviso según su variante (001 MOD RF-6): conectado
    /// (icono del tipo + nombre + aro de batería), conectado cargando (rayo verde +
    /// porcentaje verde) o desconectado (rayo rojo + porcentaje rojo). Sin batería
    /// conocida la zona derecha no muestra nada (RF-5).
    /// </summary>
    private void RefreshBluetoothUI()
    {
        var device = _bluetoothDevice;
        if (device == null) return;
        BluetoothName.Text = device.Name;
        bool ring = _bluetoothNotice == BluetoothNoticeKind.Connected;
        bool alert = _bluetoothNotice == BluetoothNoticeKind.Disconnected;
        BluetoothGlyph.Symbol = ring ? device.Glyph : Wpf.Ui.Controls.SymbolRegular.Flash24;
        BluetoothGlyph.Foreground = ring ? BluetoothGlyphBrush
            : alert ? BluetoothAlertBrush : BatteryRemainingBrush;
        // Zona derecha: el aro del conectado o el porcentaje en números del rayo.
        if (ring && device.BatteryPercent is int ringPercent)
        {
            BluetoothBatteryZone.Visibility = Visibility.Visible;
            BluetoothBatteryArc.Stroke = BatteryRemainingBrush;
            BluetoothBatteryArc.Data = BuildBatteryArc(ringPercent);
            BluetoothBatteryZone.ToolTip = device.BatteryText;
        }
        else
        {
            // Sin batería conocida la zona no muestra nada (ni aro vacío ni 0 %)
            // (RF-5).
            BluetoothBatteryZone.Visibility = Visibility.Collapsed;
            BluetoothBatteryZone.ToolTip = null;
            BluetoothBatteryArc.Data = null;
        }
        if (!ring && device.BatteryPercent is int percent)
        {
            BluetoothPercent.Text = $"{percent} %";
            BluetoothPercent.Foreground = alert ? BluetoothAlertBrush : BatteryRemainingBrush;
            BluetoothPercent.Visibility = Visibility.Visible;
        }
        else
        {
            BluetoothPercent.Visibility = Visibility.Collapsed;
            BluetoothPercent.Text = "";
        }
        BluetoothCompactGrid.ToolTip = BluetoothTooltip(device);
    }

    /// <summary>Tooltip del aviso: dice qué pasó y, si se conoce, la batería.</summary>
    private string BluetoothTooltip(IslandBluetoothDevice device)
    {
        string head = _bluetoothNotice switch
        {
            BluetoothNoticeKind.Charging => $"{device.Name} conectado y cargando",
            BluetoothNoticeKind.Disconnected => $"{device.Name} desconectado",
            _ => $"{device.Name} conectado",
        };
        return device.BatteryText is { } battery ? $"{head} · {battery}" : head;
    }

    /// <summary>
    /// Repinta SOLO si el aviso de Bluetooth es la vista de delante: una
    /// actualización (batería tardía) no re-despliega nada (001 MOD RF-2/RF-28).
    /// </summary>
    private void ReconcileBluetoothState()
    {
        if (!BluetoothModeAvailable() || _bluetoothDevice == null) return;
        if (!IsBoxShown || !ViewShowsFeature(IslandFeatureIds.Bluetooth)) return;
        RefreshBluetoothUI();
    }

    /// <summary>
    /// La vista de Bluetooth dejó de ser presentable (se apagó la funcionalidad, el
    /// dispositivo se desconectó o el ajuste del contenedor cambió): se repliega a
    /// la activa vigente o al reposo, sin dejar la superficie del aviso puesta.
    /// </summary>
    private void FallbackFromBluetoothView()
    {
        // El aviso forzado es el de esta vista: no puede quedar armado sobre la
        // vista siguiente.
        if (_noticeForced) ClearTemporaryNotice();
        // Con una pantalla delante, se recompone con sus miembros usables.
        if (RecoverScreensAfterMemberLost()) return;
        _expanded = false;
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } vigente && ShowScreenOfFeature(vigente))
            return;
        HidePerMode();
    }

    /// <summary>
    /// Geometría del aro de batería: arco desde las 12 en sentido horario con la
    /// fracción restante. El resto del aro es el trazo gris claro del XAML, así que
    /// aquí solo se dibuja la parte verde.
    /// </summary>
    private static Geometry BuildBatteryArc(double percent)
    {
        double sweep = Math.Clamp(percent, 0, 100) * 3.6;
        // 100 % no puede ser un arco de 360° (inicio y fin coincidirían y no se
        // dibujaría nada): se cierra en 359,9°, que con los extremos redondeados se
        // ve como un aro completo.
        if (sweep >= 359.9) sweep = 359.9;
        if (sweep <= 0.4) return Geometry.Empty;
        double radius = (BatteryRingSize - BatteryRingThickness) / 2;
        var center = new Point(BatteryRingSize / 2, BatteryRingSize / 2);
        Point start = new(center.X, center.Y - radius);
        double radians = sweep * Math.PI / 180;
        Point end = new(center.X + radius * Math.Sin(radians), center.Y - radius * Math.Cos(radians));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, sweep > 180,
                SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }
}
