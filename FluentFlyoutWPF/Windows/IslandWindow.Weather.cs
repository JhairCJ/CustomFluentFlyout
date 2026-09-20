// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;
using System.Windows;
using System.Windows.Media;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// Clima del lugar configurado en el Island: una funcionalidad más del contenedor,
/// junto a música, temporizador, cajón, estante, calendario, Bluetooth y
/// portapapeles.
///
/// <para>Reglas que sostiene (change island-clima):</para>
/// <list type="bullet">
/// <item><b>Sin lugar no hay vista</b> (RF-2): la funcionalidad nace apagada y, con
/// ella apagada o sin lugar elegido, no se consulta nada a la red ni se ofrece en la
/// navegación.</item>
/// <item><b>Evento, no sondeo</b> (RF-3): el dato llega cuando llega (arranque,
/// cambio de lugar, cada cuarto de hora del servicio) y el contenedor solo repinta si
/// la vista del clima está delante.</item>
/// <item><b>Dato conservado</b> (RF-4): un ciclo que falle no borra lo que ya se
/// sabía —mejor un dato de hace un rato que un hueco— y el motivo queda en ajustes.</item>
/// </list>
/// </summary>
public partial class IslandWindow
{
    /// <summary>Servicio del clima: consulta y publica; el Island solo pinta.</summary>
    private IslandWeatherService? _weather;
    /// <summary>Último dato publicado (null mientras no haya ninguno).</summary>
    private IslandWeatherSnapshot? _weatherSnapshot;

    /// <summary>Funcionalidad «clima» registrada (nunca null tras el arranque).</summary>
    private IIslandFeature? WeatherFeature => FeatureById(IslandFeatureIds.Weather);

    /// <summary>¿La funcionalidad está encendida, con el contenedor y con lugar elegido?</summary>
    private bool WeatherModeAvailable() =>
        SettingsManager.Current.IslandEnabled
        && SettingsManager.Current.IslandWeatherEnabled
        && SettingsManager.Current.IslandWeatherPlace.Trim().Length > 0
        && (SettingsManager.Current.IslandWeatherLatitude != 0 || SettingsManager.Current.IslandWeatherLongitude != 0);

    /// <summary>
    /// ¿La vista del clima se sostiene sola? No tiene actividad propia (el clima no
    /// «está pasando»): en «Visible mientras activo» la sostiene el puntero y en
    /// «Aviso temporal», su plazo, como el cajón y el estante.
    /// </summary>
    private bool WeatherKeepsView() =>
        WeatherModeAvailable()
        && SettingsManager.Current.IslandVisibilityMode == 1
        && _noticeUntil > DateTime.UtcNow;

    internal IslandFeatureState GetWeatherFeatureState()
    {
        bool enabled = WeatherModeAvailable();
        bool available = enabled && _weatherSnapshot != null;
        return new IslandFeatureState(enabled, available, Active: false,
            Selected: _selectedFeature?.Id == IslandFeatureIds.Weather, Exclusive: false);
    }

    internal bool ShowWeatherCompactFromContract()
    {
        if (!WeatherModeAvailable() || _weatherSnapshot == null) return false;
        ShowWeatherCompact();
        return true;
    }

    internal bool ShowWeatherExpandedFromContract()
    {
        if (!WeatherModeAvailable() || _weatherSnapshot == null) return false;
        ExpandWeather();
        return true;
    }

    /// <summary>Resumen de una línea del clima para las pantallas combinadas.</summary>
    internal IslandFeatureSummary WeatherSummary()
    {
        var snapshot = _weatherSnapshot;
        return snapshot == null
            ? new IslandFeatureSummary(Wpf.Ui.Controls.SymbolRegular.WeatherCloudy24, "Sin dato")
            : new IslandFeatureSummary(snapshot.Glyph, snapshot.TemperatureText);
    }

    /// <summary>
    /// Vincula el servicio del clima y lo arranca si la funcionalidad está encendida
    /// con lugar elegido. Los datos llegan desde su hilo de fondo: se cruzan al de UI
    /// antes de tocar nada (igual que el vigía de Bluetooth).
    /// </summary>
    private void InitWeather()
    {
        _weather = new IslandWeatherService();
        _weather.Changed += OnWeatherChanged;
        ApplyWeatherSettings();
    }

    private void ShutdownWeather()
    {
        var weather = _weather;
        _weather = null;
        if (weather != null) weather.Changed -= OnWeatherChanged;
        weather?.Dispose();
    }

    /// <summary>
    /// Ajuste en caliente: lugar nuevo, encendido/apagado. Arranca o para el ciclo
    /// (apagado no se llama a la red) y, si su vista estaba puesta y deja de ser
    /// presentable, el contenedor se repliega sin dejar una superficie vacía.
    /// </summary>
    public void RefreshWeatherContent() => Dispatcher.Invoke(() => RefreshWeatherContentCore());

    private void RefreshWeatherContentCore()
    {
        ApplyWeatherSettings();
        if (!WeatherModeAvailable() && _contentMode == IslandContentMode.Weather && IsBoxShown)
        {
            FallbackFromWeatherView();
            return;
        }
        ApplyContentVisibility();
        SyncMeasuredHeight();
        UpdateArrows();
        PostActivity(IslandActivityReason.Weather | IslandActivityReason.Settings);
    }

    /// <summary>
    /// Refresco a mano desde ajustes: consulta el lugar configurado ahora mismo y
    /// repinta si su vista está a la vista. El ciclo periódico sigue igual.
    /// </summary>
    public void RefreshWeatherNow() => Dispatcher.Invoke(() => _ = RefreshWeatherNowCoreAsync());

    private async Task RefreshWeatherNowCoreAsync()
    {
        var weather = _weather;
        var settings = SettingsManager.Current;
        ApplyWeatherSettings();
        if (weather == null || !WeatherModeAvailable())
        {
            settings.IslandWeatherError = "Elige un lugar para poder mirar su clima.";
            return;
        }
        settings.IslandWeatherError = "";
        settings.IslandWeatherStatus = "Consultando…";
        bool ok = await weather.RefreshNowAsync();
        var snapshot = weather.Snapshot;
        if (snapshot != null)
        {
            _weatherSnapshot = snapshot;
            RefreshWeatherUI();
            ApplyContentVisibility();
            SyncMeasuredHeight();
            UpdateArrows();
            PostActivity(IslandActivityReason.Weather);
        }
        settings.IslandWeatherStatus = ok && snapshot != null
            ? $"Actualizado a las {snapshot.UpdatedUtc.ToLocalTime():HH:mm}."
            : "";
        settings.IslandWeatherError = weather.Error ?? (ok ? "" : "No se pudo leer el clima.");
    }

    /// <summary>Lleva a la fuerza el lugar elegido al servicio y deja el ciclo en marcha o parado.</summary>
    private void ApplyWeatherSettings()
    {
        var weather = _weather;
        if (weather == null) return;
        var settings = SettingsManager.Current;
        weather.Place = WeatherModeAvailable()
            ? new IslandWeatherPlace(settings.IslandWeatherPlace.Trim(), "", "", settings.IslandWeatherLatitude, settings.IslandWeatherLongitude)
            : null;
        if (WeatherModeAvailable()) weather.Start();
        else weather.Stop();
    }

    // ------------------------------------------------------------------
    // Dato que llega (hilo de fondo -> hilo de UI)
    // ------------------------------------------------------------------

    private void OnWeatherChanged(IslandWeatherSnapshot snapshot) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_disposed) return;
        var previous = _weatherSnapshot;
        _weatherSnapshot = snapshot;
        SettingsManager.Current.IslandWeatherError = _weather?.Error ?? "";
        SettingsManager.Current.IslandWeatherStatus = $"Actualizado a las {snapshot.UpdatedUtc.ToLocalTime():HH:mm}.";
        // El dato nuevo solo repinta si la vista del clima está delante (nunca la
        // despliega: el clima no es un evento que deba abrir la caja).
        if (previous == null || _contentMode == IslandContentMode.Weather)
        {
            ApplyContentVisibility();
            SyncMeasuredHeight();
        }
        UpdateArrows();
        PostActivity(IslandActivityReason.Weather);
    }));

    // ------------------------------------------------------------------
    // Presentación
    // ------------------------------------------------------------------

    private void ShowWeatherCompact()
    {
        if (!WeatherModeAvailable() || _weatherSnapshot == null) { SnapHidden(); return; }
        RefreshWeatherUI();
        ShowCompactView(IslandContentMode.Weather, WeatherFeature, RefreshWeatherUI);
    }

    private void ExpandWeather()
    {
        if (!WeatherModeAvailable() || _weatherSnapshot == null) return;
        RefreshWeatherUI();
        ShowExpandedView(IslandContentMode.Weather, WeatherFeature, RefreshWeatherUI);
    }

    /// <summary>
    /// Pinta el dato: en compacto el glifo del cielo y la temperatura; en expandido,
    /// además, el lugar, el estado y los extremos del día. Sin dato no se pinta nada
    /// (la vista no llega a presentarse).
    /// </summary>
    private void RefreshWeatherUI()
    {
        var snapshot = _weatherSnapshot;
        if (snapshot == null) return;
        WeatherCompactGlyph.Symbol = snapshot.Glyph;
        WeatherCompactTemp.Text = snapshot.TemperatureText;
        WeatherCompactGrid.ToolTip = WeatherTooltip(snapshot);

        WeatherPlace.Text = snapshot.Place;
        WeatherExpandedGlyph.Symbol = snapshot.Glyph;
        WeatherExpandedTemp.Text = snapshot.TemperatureText;
        WeatherCondition.Text = snapshot.Condition;
        WeatherRange.Text = snapshot.RangeText ?? "";
        WeatherRange.Visibility = snapshot.RangeText == null ? Visibility.Collapsed : Visibility.Visible;
        WeatherUpdated.Text = snapshot.UpdatedUtc.ToLocalTime().ToString("HH:mm");
    }

    private static string WeatherTooltip(IslandWeatherSnapshot snapshot)
    {
        string head = $"{snapshot.Place} · {snapshot.Condition}";
        return snapshot.RangeText is { } range ? $"{head}\n{range}" : head;
    }

    /// <summary>Repinta SOLO si la vista del clima es la de delante (un dato nuevo no despliega nada).</summary>
    private void ReconcileWeatherState()
    {
        if (!WeatherModeAvailable() || _weatherSnapshot == null) return;
        if (!IsBoxShown || _contentMode != IslandContentMode.Weather) return;
        RefreshWeatherUI();
    }

    /// <summary>
    /// La vista del clima dejó de ser presentable (se apagó la funcionalidad, se quitó
    /// el lugar): se repliega a la activa vigente o al reposo, sin dejar la superficie
    /// del clima puesta.
    /// </summary>
    private void FallbackFromWeatherView()
    {
        _expanded = false;
        _contentMode = IslandContentMode.Media;
        ApplyContentVisibility();
        if (SettingsManager.Current.IslandVisibilityMode == 0
            && ResolveActiveVigenteForVisible() is { } vigente && ShowScreenOfFeature(vigente))
            return;
        HidePerMode();
    }
}
