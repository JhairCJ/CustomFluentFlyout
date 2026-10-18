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
        && double.IsFinite(SettingsManager.Current.IslandWeatherLatitude) && SettingsManager.Current.IslandWeatherLatitude is >= -90 and <= 90
        && double.IsFinite(SettingsManager.Current.IslandWeatherLongitude) && SettingsManager.Current.IslandWeatherLongitude is >= -180 and <= 180;

    /// <summary>
    /// ¿La vista del clima se sostiene sola? No tiene actividad propia (el clima no
    /// «está pasando»): en «Visible mientras activo» la sostiene el puntero y en
    /// «Aviso temporal», su plazo, como el cajón y el estante.
    /// </summary>
    private bool WeatherKeepsView() =>
        WeatherModeAvailable()
        && SettingsManager.Current.IslandVisibilityMode == 1
        && _noticeUntil > IslandClock.Now;

    internal IslandFeatureState GetWeatherFeatureState()
    {
        bool enabled = WeatherModeAvailable();
        bool available = enabled;
        return new IslandFeatureState(enabled, available, Active: false,
            Selected: _selectedFeature?.Id == IslandFeatureIds.Weather, Exclusive: false);
    }

    internal bool ShowWeatherCompactFromContract()
    {
        if (!WeatherModeAvailable()) return false;
        ShowWeatherCompact();
        return true;
    }

    internal bool ShowWeatherExpandedFromContract()
    {
        if (!WeatherModeAvailable() || _weatherSnapshot == null) return false;
        ExpandWeather();
        return true;
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
        _weather.StateChanged += OnWeatherStateChanged;
        ApplyWeatherSettings();
    }

    private void ShutdownWeather()
    {
        var weather = _weather;
        _weather = null;
        if (weather != null)
        {
            weather.Changed -= OnWeatherChanged;
            weather.StateChanged -= OnWeatherStateChanged;
        }
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
        if (!WeatherModeAvailable() && ViewShowsFeature(IslandFeatureIds.Weather) && IsBoxShown)
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
            settings.IslandWeatherError = IslandStrings.Get("IslandWeatherNeedPlace",
                "Pick a place to check its weather.");
            return;
        }
        settings.IslandWeatherError = "";
        settings.IslandWeatherStatus = IslandStrings.Get("IslandWeatherChecking", "Checking…");
        var requestedPlace = weather.Place;
        bool ok = await weather.RefreshNowAsync();
        if (_disposed || !ReferenceEquals(weather, _weather) || !WeatherModeAvailable()
            || !Equals(requestedPlace, weather.Place)) return;
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
            ? WeatherUpdatedText(snapshot)
            : "";
        settings.IslandWeatherError = weather.Error ?? (ok ? "" : IslandStrings.Get(
            "IslandWeatherUnavailable", "Could not read the weather."));
    }

    /// <summary>«Actualizado a las 14:35.» en el idioma activo (hora local del dato).</summary>
    private static string WeatherUpdatedText(IslandWeatherSnapshot snapshot) =>
        IslandStrings.Format("IslandWeatherUpdated", "Updated at {0}.",
            snapshot.UpdatedUtc.ToLocalTime().ToString("HH:mm"));

    /// <summary>Lleva a la fuerza el lugar elegido al servicio y deja el ciclo en marcha o parado.</summary>
    private void ApplyWeatherSettings()
    {
        var weather = _weather;
        if (weather == null) return;
        var settings = SettingsManager.Current;
        var previousPlace = weather.Place;
        weather.Place = WeatherModeAvailable()
            ? new IslandWeatherPlace(settings.IslandWeatherPlace.Trim(), "", "", settings.IslandWeatherLatitude, settings.IslandWeatherLongitude)
            : null;
        if (!Equals(previousPlace, weather.Place)) _weatherSnapshot = null;
        if (WeatherModeAvailable()) weather.Start();
        else weather.Stop();
    }

    // ------------------------------------------------------------------
    // Dato que llega (hilo de fondo -> hilo de UI)
    // ------------------------------------------------------------------

    private void OnWeatherStateChanged() => QueueIslandUi(() =>
    {
        if (_weather == null || !WeatherModeAvailable()) return;
        SettingsManager.Current.IslandWeatherError = _weather.Error ?? "";
        if (ViewShowsFeature(IslandFeatureIds.Weather)) RefreshWeatherUI();
    });

    private void OnWeatherChanged(IslandWeatherSnapshot snapshot) => QueueIslandUi(() =>
    {
        if (_disposed || !ReferenceEquals(snapshot, _weather?.Snapshot)) return;
        var previous = _weatherSnapshot;
        _weatherSnapshot = snapshot;
        SettingsManager.Current.IslandWeatherError = _weather?.Error ?? "";
        SettingsManager.Current.IslandWeatherStatus = WeatherUpdatedText(snapshot);
        // El dato nuevo solo repinta si la vista del clima está delante (nunca la
        // despliega: el clima no es un evento que deba abrir la caja).
        if (previous == null || ViewShowsFeature(IslandFeatureIds.Weather))
        {
            ApplyContentVisibility();
            SyncMeasuredHeight();
        }
        UpdateArrows();
        PostActivity(IslandActivityReason.Weather);
    });

    // ------------------------------------------------------------------
    // Presentación
    // ------------------------------------------------------------------

    private void ShowWeatherCompact()
    {
        if (!WeatherModeAvailable()) { SnapHidden(); return; }
        RefreshWeatherUI();
        ShowCompactView(IslandContentMode.Weather, WeatherFeature, RefreshWeatherUI);
    }

    private void ExpandWeather()
    {
        if (!WeatherModeAvailable()) return;
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
        if (snapshot == null)
        {
            WeatherPlace.Text = SettingsManager.Current.IslandWeatherPlace;
            WeatherCompactTemp.Text = WeatherExpandedTemp.Text = "—°";
            WeatherCondition.Text = _weather?.Error ?? (_weather?.IsRefreshing == true ? IslandStrings.Get("IslandWeatherLoading", "Loading weather…") : IslandStrings.Get("IslandWeatherNoData", "No weather data is available. Try refreshing in settings."));
            WeatherRange.Text = "";
            WeatherUpdated.Text = "";
            return;
        }
        WeatherCompactGlyph.Symbol = snapshot.Glyph;
        WeatherCompactTemp.Text = snapshot.TemperatureText;
        WeatherCompactGrid.ToolTip = WeatherTooltip(snapshot);

        WeatherPlace.Text = snapshot.Place;
        WeatherExpandedGlyph.Symbol = snapshot.Glyph;
        WeatherExpandedTemp.Text = snapshot.TemperatureText;
        WeatherCondition.Text = snapshot.WeatherCode is int code ? IslandWeatherService.Describe(code).Condition : snapshot.Condition;
        WeatherRange.Text = snapshot.RangeText ?? "";
        WeatherRange.Visibility = snapshot.RangeText == null ? Visibility.Collapsed : Visibility.Visible;
        WeatherUpdated.Text = _weather?.Error is { Length: > 0 } || DateTime.UtcNow - snapshot.UpdatedUtc > TimeSpan.FromMinutes(30)
            ? IslandStrings.Format("IslandDataStale", "Older data · {0}", snapshot.UpdatedUtc.ToLocalTime().ToString("HH:mm"))
            : snapshot.UpdatedUtc.ToLocalTime().ToString("HH:mm");
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
        if (!IsBoxShown || !ViewShowsFeature(IslandFeatureIds.Weather)) return;
        RefreshWeatherUI();
    }

    /// <summary>
    /// La vista del clima dejó de ser presentable (se apagó la funcionalidad, se quitó
    /// el lugar): se repliega a la activa vigente o al reposo, sin dejar la superficie
    /// del clima puesta.
    /// </summary>
    private void FallbackFromWeatherView()
    {
        // Con una pantalla delante, se recompone con sus miembros usables: la vista no
        // se cae por perder a uno de ellos (change island-pantallas).
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
