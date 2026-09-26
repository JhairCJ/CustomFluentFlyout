// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Media;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Cargador del equipo en el Island: avisa cuando se enchufa y cuando se desenchufa,
/// con el rayo y el nivel de batería (change island-cargador).
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Siempre aviso temporal</b>: como el de Bluetooth, su vista es una
/// notificación —rayo verde y porcentaje al enchufar, rayo rojo y último porcentaje al
/// desenchufar— que se retira al vencer el plazo configurado, también en «Visible
/// mientras activo».</item>
/// <item><b>Nunca interrumpe</b>: no sustituye la caja expandida que el usuario está
/// mirando ni aparece sobre un juego a pantalla completa; sin vista expandida entra en
/// compacto.</item>
/// <item><b>Sin batería no hay aviso</b>: en un equipo que no la tiene, enchufar o
/// desenchufar no significa nada.</item>
/// </list>
/// </summary>
public partial class IslandWindow
{
    /// <summary>Verde del rayo al enchufar (el mismo de la batería del Bluetooth).</summary>
    private static readonly Brush PowerChargingBrush = Frozen(Color.FromRgb(0x6C, 0xCB, 0x5F));
    /// <summary>Rojo del rayo al quedarse a batería.</summary>
    private static readonly Brush PowerUnpluggedBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));

    /// <summary>Variante del aviso a la vista (enchufado o desenchufado).</summary>
    private enum PowerNoticeKind { Charging, Unplugged }

    private IslandPowerMonitor? _power;
    private IslandPowerStatus? _powerStatus;
    private PowerNoticeKind _powerNotice = PowerNoticeKind.Charging;

    /// <summary>Funcionalidad «cargador» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? PowerFeature => FeatureById(IslandFeatureIds.Power);

    /// <summary>¿La funcionalidad está encendida con el contenedor?</summary>
    private bool PowerModeAvailable() =>
        SettingsManager.Current.IslandEnabled && SettingsManager.Current.IslandPowerEnabled;

    /// <summary>¿Sigue vivo el aviso? Su vista vive solo mientras SU plazo corre.</summary>
    private bool PowerNoticeAlive() => NoticeAliveFor(IslandContentMode.Power);

    private bool PowerActive() => PowerModeAvailable() && _powerStatus != null && PowerNoticeAlive();

    internal IslandFeatureState GetPowerFeatureState()
    {
        bool enabled = PowerModeAvailable();
        bool available = enabled && _powerStatus != null;
        return new IslandFeatureState(enabled, available, PowerActive(),
            Selected: _selectedFeature?.Id == IslandFeatureIds.Power, Exclusive: false);
    }

    internal bool ShowPowerCompactFromContract()
    {
        if (!PowerModeAvailable() || _powerStatus == null) return false;
        ShowPowerCompact(restartNotice: false);
        return true;
    }

    /// <summary>
    /// Vincula el vigía del cargador y lo arranca si la funcionalidad está encendida.
    /// Sus eventos llegan en el hilo de UI (SystemEvents), así que se cruzan igual
    /// por el dispatcher para no depender de ello.
    /// </summary>
    private void InitPower()
    {
        _power = new IslandPowerMonitor();
        _power.ChargerConnected += OnChargerConnected;
        _power.ChargerDisconnected += OnChargerDisconnected;
        if (PowerModeAvailable()) _power.Start();
    }

    private void ShutdownPower()
    {
        var power = _power;
        _power = null;
        power?.Dispose();
    }

    /// <summary>
    /// Ajuste en caliente: encendida arranca el vigía; apagada lo para (no se observa
    /// nada) y repliega su vista si estaba puesta, sin dejar una superficie vacía.
    /// </summary>
    public void RefreshPowerContent() => Dispatcher.Invoke(() =>
    {
        if (PowerModeAvailable()) _power?.Start();
        else
        {
            _power?.Stop();
            if (IsBoxShown && ViewShowsFeature(IslandFeatureIds.Power)) FallbackFromPowerView();
        }
        ApplyContentVisibility();
        SyncMeasuredHeight();
        UpdateArrows();
        PostActivity(IslandActivityReason.Power | IslandActivityReason.Settings);
    });

    // ------------------------------------------------------------------
    // Eventos del vigía
    // ------------------------------------------------------------------

    /// <summary>Se enchufó a la corriente: rayo verde y el nivel de batería.</summary>
    private void OnChargerConnected(IslandPowerStatus status) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_disposed || !PowerModeAvailable()) return;
        _powerStatus = status;
        _powerNotice = PowerNoticeKind.Charging;
        NoteFeatureEvent(IslandFeatureIds.Power);
        if (!SettingsManager.Current.IslandEnabled) return;
        // Nunca interrumpe: con una vista expandida delante (o suprimido) el aviso no
        // se presenta; el estado queda adoptado y el próximo cambio sí avisa.
        if (Suppressed() || HasExclusive() || _expanded) return;
        ShowPowerCompact(restartNotice: true);
    }));

    /// <summary>Se desenchufó: rayo rojo y el último nivel conocido.</summary>
    private void OnChargerDisconnected(IslandPowerStatus status) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_disposed || !PowerModeAvailable()) return;
        _powerStatus = status;
        _powerNotice = PowerNoticeKind.Unplugged;
        NoteFeatureEvent(IslandFeatureIds.Power);
        if (!SettingsManager.Current.IslandEnabled) return;
        if (Suppressed() || HasExclusive() || _expanded) return;
        ShowPowerCompact(restartNotice: true);
    }));

    // ------------------------------------------------------------------
    // Presentación
    // ------------------------------------------------------------------

    /// <summary>
    /// Aviso temporal del cargador: presenta el compacto con el aviso FORZADO (vence
    /// también en «Visible mientras activo», como el de Bluetooth). Solo el cambio de
    /// estado reinicia el plazo; volver a presentarlo lo conserva.
    /// </summary>
    private void ShowPowerCompact(bool restartNotice)
    {
        if (!PowerModeAvailable() || _powerStatus == null) { SnapHidden(); return; }
        RefreshPowerUI();
        ShowCompactView(IslandContentMode.Power, PowerFeature, RefreshPowerUI,
            forceNotice: true, restartNotice: restartNotice);
    }

    /// <summary>
    /// Pinta el aviso: rayo verde y porcentaje al enchufar, rayo rojo y porcentaje (el
    /// último conocido) al desenchufar. Sin nivel conocido se enseña el texto solo.
    /// </summary>
    private void RefreshPowerUI()
    {
        var status = _powerStatus;
        if (status == null) return;
        bool unplugged = _powerNotice == PowerNoticeKind.Unplugged;
        PowerGlyph.Symbol = unplugged
            ? Wpf.Ui.Controls.SymbolRegular.BatteryWarning24
            : Wpf.Ui.Controls.SymbolRegular.BatteryCharge24;
        PowerGlyph.Foreground = unplugged ? PowerUnpluggedBrush : PowerChargingBrush;
        // «Cargando» solo si de verdad está cargando: enchufado y al 100 % Windows
        // informa que no carga, y decir «Cargando» ahí sería mentira.
        string unpluggedText = IslandStrings.Get("IslandPowerUnplugged", "Charger unplugged");
        string chargingText = IslandStrings.Get("IslandPowerCharging", "Charging");
        PowerTitle.Text = unplugged ? unpluggedText
            : status.Charging ? chargingText
            : IslandStrings.Get("IslandPowerPlugged", "Plugged in");
        PowerPercent.Text = status.PercentText ?? "";
        PowerPercent.Foreground = unplugged ? PowerUnpluggedBrush : PowerChargingBrush;
        PowerPercent.Visibility = status.PercentText == null ? Visibility.Collapsed : Visibility.Visible;
        PowerCompactGrid.ToolTip = status.PercentText is { } percent
            ? IslandStrings.Format(unplugged ? "IslandPowerTooltipUnplugged" : "IslandPowerTooltipCharging",
                unplugged ? "Charger unplugged · battery {0}" : "Charging · battery {0}", percent)
            : unplugged ? unpluggedText : chargingText;
    }

    /// <summary>Repinta SOLO si el aviso del cargador es la vista de delante.</summary>
    private void ReconcilePowerState()
    {
        if (!PowerModeAvailable() || _powerStatus == null) return;
        if (!IsBoxShown || !ViewShowsFeature(IslandFeatureIds.Power)) return;
        RefreshPowerUI();
    }

    /// <summary>
    /// La vista del cargador dejó de ser presentable (se apagó la funcionalidad): se
    /// repliega a la activa vigente o al reposo, sin dejar la superficie del aviso.
    /// </summary>
    private void FallbackFromPowerView()
    {
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
}
