// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http;
using System.Text.Json;
using Wpf.Ui.Controls;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Lugar del que se mira el clima: lo que devuelve el buscador de lugares y lo que
/// se guarda en ajustes (nombre, región y país para que se reconozca, y las
/// coordenadas que usa la consulta del clima).
/// </summary>
public sealed record IslandWeatherPlace(string Name, string Region, string Country, double Latitude, double Longitude)
{
    /// <summary>Nombre visible con lo que lo distingue («Vigo, Galicia, España»).</summary>
    public string Label
    {
        get
        {
            var parts = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(Region) && !string.Equals(Region, Name, StringComparison.OrdinalIgnoreCase))
                parts.Add(Region);
            if (!string.IsNullOrWhiteSpace(Country) && !string.Equals(Country, Region, StringComparison.OrdinalIgnoreCase))
                parts.Add(Country);
            return string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Clima del lugar elegido en un instante: temperatura actual, extremos del día,
/// estado del cielo (texto y glifo) y de cuándo es el dato.
/// </summary>
public sealed record IslandWeatherSnapshot(
    string Place,
    double TemperatureC,
    double? HighC,
    double? LowC,
    string Condition,
    SymbolRegular Glyph,
    DateTime UpdatedUtc)
{
    /// <summary>Temperatura redondeada para los textos («18°»).</summary>
    public string TemperatureText => $"{Math.Round(TemperatureC)}°";

    /// <summary>Extremos del día si el proveedor los dio («máx 22° · mín 11°»).</summary>
    public string? RangeText => HighC is double high && LowC is double low
        ? $"máx {Math.Round(high)}° · mín {Math.Round(low)}°"
        : null;
}

/// <summary>
/// Clima del lugar configurado (change island-clima): Open-Meteo, que no pide clave
/// de API (el usuario no tiene que registrarse en ningún sitio), para el buscador de
/// lugares y para el dato actual.
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item><b>Sin sondeo</b>: el dato se pide al arrancar, al cambiar de lugar y cada
/// cuarto de hora (una consulta por ciclo, no por segundo); lo que se publica es un
/// evento, y el Island solo repinta si su vista está a la vista.</item>
/// <item><b>Sin lugar, sin consulta</b>: con la funcionalidad apagada o sin lugar
/// elegido no se llama a la red.</item>
/// <item><b>Un fallo no rompe nada</b>: se registra, se guarda el motivo para ajustes
/// y se conserva el último dato bueno (mejor un dato viejo que un hueco).</item>
/// </list>
/// </summary>
public sealed class IslandWeatherService : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private const string GeocodingUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";

    /// <summary>Cadencia del dato: cada 15 minutos es de sobra para una temperatura.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private CancellationTokenSource? _loop;
    private bool _disposed;

    /// <summary>Último dato bueno publicado (null mientras no haya ninguno).</summary>
    public IslandWeatherSnapshot? Snapshot { get; private set; }

    /// <summary>Motivo del último fallo, para la página de ajustes (vacío si todo fue bien).</summary>
    public string? Error { get; private set; }

    /// <summary>Último lugar pedido: sin él no se consulta nada.</summary>
    public IslandWeatherPlace? Place { get; set; }

    /// <summary>Se publicó un dato nuevo (o el primero): el consumidor decide si repinta.</summary>
    public event Action<IslandWeatherSnapshot>? Changed;

    /// <summary>
    /// Arranca el ciclo de refresco (idempotente). Sin lugar configurado no consulta
    /// nada; en cuanto se le da uno, el ciclo lo usa.
    /// </summary>
    public void Start()
    {
        if (_disposed || _loop != null) return;
        var cts = new CancellationTokenSource();
        _loop = cts;
        _ = LoopAsync(cts.Token);
    }

    /// <summary>Para el ciclo y olvida lo observado (apagar la funcionalidad no deja nada corriendo).</summary>
    public void Stop()
    {
        var loop = _loop;
        _loop = null;
        try { loop?.Cancel(); } catch { /* ya cancelado */ }
        loop?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>Refresco a mano (ajustes): devuelve true si se publicó un dato nuevo.</summary>
    public async Task<bool> RefreshNowAsync()
    {
        var snapshot = await FetchAsync(Place, CancellationToken.None).ConfigureAwait(false);
        if (snapshot == null) return false;
        Publish(snapshot);
        return true;
    }

    /// <summary>
    /// Buscador de lugares: devuelve hasta seis coincidencias con nombre, región y
    /// país, que es lo que permite elegir el lugar correcto entre varios del mismo
    /// nombre.
    /// </summary>
    public static async Task<IReadOnlyList<IslandWeatherPlace>> SearchPlacesAsync(string query, CancellationToken token)
    {
        var places = new List<IslandWeatherPlace>();
        string trimmed = (query ?? "").Trim();
        if (trimmed.Length < 2) return places;
        try
        {
            string url = $"{GeocodingUrl}?name={Uri.EscapeDataString(trimmed)}&count=6&language=es&format=json";
            using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("results", out var results)) return places;
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("latitude", out var lat) || !item.TryGetProperty("longitude", out var lon)) continue;
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) continue;
                places.Add(new IslandWeatherPlace(
                    name,
                    item.TryGetProperty("admin1", out var admin1) ? admin1.GetString() ?? "" : "",
                    item.TryGetProperty("country", out var country) ? country.GetString() ?? "" : "",
                    lat.GetDouble(),
                    lon.GetDouble()));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Clima: fallo al buscar lugares para {Query}", trimmed);
        }
        return places;
    }

    // --- ciclo de refresco ---

    private async Task LoopAsync(CancellationToken token)
    {
        // Primera consulta inmediata (al activar la funcionalidad o arrancar la app) y
        // después una por intervalo. Sin lugar configurado se espera sin consultar.
        while (!token.IsCancellationRequested)
        {
            if (Place != null)
            {
                var snapshot = await FetchAsync(Place, token).ConfigureAwait(false);
                if (snapshot != null) Publish(snapshot);
            }
            try { await Task.Delay(RefreshInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Publish(IslandWeatherSnapshot snapshot)
    {
        Snapshot = snapshot;
        Error = null;
        try { Changed?.Invoke(snapshot); }
        catch (Exception ex) { Log.Warn(ex, "Clima: fallo al publicar el dato"); }
    }

    private async Task<IslandWeatherSnapshot?> FetchAsync(IslandWeatherPlace? place, CancellationToken token)
    {
        if (place == null) return null;
        try
        {
            string url = $"{ForecastUrl}?latitude={place.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                $"&longitude={place.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                "&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1";
            using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("current", out var current)) return null;
            double temperature = current.TryGetProperty("temperature_2m", out var temp) ? temp.GetDouble() : 0;
            int code = current.TryGetProperty("weather_code", out var wc) ? wc.GetInt32() : 0;
            double? high = null;
            double? low = null;
            if (root.TryGetProperty("daily", out var daily))
            {
                if (daily.TryGetProperty("temperature_2m_max", out var max) && max.GetArrayLength() > 0) high = max[0].GetDouble();
                if (daily.TryGetProperty("temperature_2m_min", out var min) && min.GetArrayLength() > 0) low = min[0].GetDouble();
            }
            var (condition, glyph) = Describe(code);
            return new IslandWeatherSnapshot(place.Label, temperature, high, low, condition, glyph, DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = "No se pudo leer el clima. Se reintentará en el próximo ciclo.";
            Log.Warn(ex, "Clima: fallo al leer el dato de {Place}", place.Label);
            return null;
        }
    }

    /// <summary>
    /// Traduce el código WMO del proveedor a un texto en español y el glifo del
    /// Island. Los códigos van por grupos (despejado, nubes, lluvia, nieve, tormenta),
    /// así que cada glifo cubre su familia sin tabla de treinta entradas.
    /// </summary>
    private static (string Condition, SymbolRegular Glyph) Describe(int code) => code switch
    {
        0 => ("Despejado", SymbolRegular.WeatherSunny24),
        1 => ("Mayormente despejado", SymbolRegular.WeatherSunny24),
        2 => ("Parcialmente nublado", SymbolRegular.WeatherPartlyCloudyDay24),
        3 => ("Nublado", SymbolRegular.WeatherCloudy24),
        45 or 48 => ("Niebla", SymbolRegular.WeatherFog24),
        >= 51 and <= 57 => ("Llovizna", SymbolRegular.WeatherDrizzle24),
        61 or 63 or 65 or 80 or 81 or 82 => ("Lluvia", SymbolRegular.WeatherRain24),
        >= 66 and <= 67 => ("Lluvia helada", SymbolRegular.WeatherRainShowersDay24),
        71 or 73 or 75 or 77 or 85 or 86 => ("Nieve", SymbolRegular.WeatherSnow24),
        >= 95 => ("Tormenta", SymbolRegular.WeatherThunderstorm24),
        _ => ("Tiempo variable", SymbolRegular.WeatherCloudy24),
    };
}
