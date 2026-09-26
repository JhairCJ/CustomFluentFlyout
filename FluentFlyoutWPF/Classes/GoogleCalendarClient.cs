// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Models;
using FluentFlyoutWPF.ViewModels;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Cliente mínimo de Google Calendar: autorización OAuth 2.0 de aplicación de
/// escritorio (bucle local en 127.0.0.1 + PKCE, sin secreto en el tráfico), refresco
/// del token de acceso y lectura del calendario principal. Sin SDK de Google: la app
/// solo necesita leer los próximos eventos.
///
/// <para>El usuario trae su propio cliente OAuth (tipo «Aplicación de escritorio»)
/// desde ajustes: id y secreto de cliente. El secreto de una app instalada no es
/// confidencial —Google lo exige igualmente en el intercambio de tokens—, así que se
/// guarda con el resto de ajustes.</para>
/// </summary>
public static class GoogleCalendarClient
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string EventsEndpoint = "https://www.googleapis.com/calendar/v3/calendars/primary/events";
    private const string PrimaryCalendarEndpoint = "https://www.googleapis.com/calendar/v3/calendars/primary";

    /// <summary>Único permiso que pide la app: leer el calendario.</summary>
    private const string Scope = "https://www.googleapis.com/auth/calendar.readonly";

    /// <summary>Cuánto se espera a que el usuario termine en el navegador.</summary>
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Tokens devueltos por Google.</summary>
    public sealed record Tokens(string AccessToken, string RefreshToken, int ExpiresInSeconds);

    /// <summary>
    /// Inicia sesión: abre el navegador en la pantalla de permiso de Google y captura
    /// la respuesta en un servidor efímero en 127.0.0.1 (el único tipo de redirección
    /// que Google admite para apps de escritorio). Devuelve los tokens ya listos.
    /// </summary>
    public static async Task<Tokens> AuthorizeAsync(string clientId, string clientSecret, CancellationToken ct = default)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        // Puerto libre elegido por el sistema: Google admite cualquier puerto en las
        // redirecciones de apps de escritorio, así que no hay nada fijo que reservar.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirect = $"http://127.0.0.1:{port}/";
        try
        {
            string url = $"{AuthEndpoint}?client_id={Uri.EscapeDataString(clientId)}"
                + $"&redirect_uri={Uri.EscapeDataString(redirect)}"
                + $"&response_type=code&scope={Uri.EscapeDataString(Scope)}"
                + "&access_type=offline&prompt=consent"
                + $"&code_challenge={challenge}&code_challenge_method=S256"
                + $"&state={state}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(AuthTimeout);
            var query = ParseQuery(await WaitForCallbackAsync(listener, timeout.Token));

            if (query.TryGetValue("error", out string? error))
                throw new InvalidOperationException(error == "access_denied"
                    ? IslandStrings.Get("IslandCalendarDenied", "Permission denied in Google.")
                    : IslandStrings.Format("IslandCalendarGoogleError", "Google returned an error: {0}", error));
            if (!query.TryGetValue("state", out string? gotState) || gotState != state)
                throw new InvalidOperationException(IslandStrings.Get("IslandCalendarStateMismatch", "The response does not match the request (state)."));
            if (!query.TryGetValue("code", out string? code) || code.Length == 0)
                throw new InvalidOperationException(IslandStrings.Get("IslandCalendarNoCode", "Google did not return the authorization code."));

            return await ExchangeAsync(clientId, clientSecret, code, verifier, redirect, ct);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Devuelve un token de acceso válido, renovándolo con el de refresco si venció
    /// (o está a punto). Los tokens recién emitidos se guardan en los ajustes.
    /// </summary>
    public static async Task<string> EnsureAccessTokenAsync(CancellationToken ct = default)
    {
        var settings = SettingsManager.Current;
        if (settings.GoogleCalendarAccessToken.Length > 0
            && settings.GoogleCalendarTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            return settings.GoogleCalendarAccessToken;

        if (settings.GoogleCalendarRefreshToken.Length == 0)
            throw new InvalidOperationException(IslandStrings.Get("IslandCalendarNoSession", "No Google session started."));

        var tokens = await RefreshAsync(settings.GoogleCalendarClientId, settings.GoogleCalendarClientSecret,
            settings.GoogleCalendarRefreshToken, ct);
        Save(settings, tokens);
        return tokens.AccessToken;
    }

    /// <summary>Renueva el token de acceso con el de refresco (aquí NO hay navegador).</summary>
    public static Task<Tokens> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct = default) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct);

    /// <summary>
    /// Lee los próximos eventos del calendario principal entre dos instantes (hora
    /// local) y los devuelve ya ordenados por comienzo.
    /// </summary>
    public static async Task<List<GoogleCalendarEvent>> FetchUpcomingAsync(
        string accessToken, DateTime from, DateTime to, int max, CancellationToken ct = default)
    {
        string url = $"{EventsEndpoint}?singleEvents=true&orderBy=startTime"
            + "&showDeleted=false"
            + $"&maxResults={Math.Clamp(max, 1, 50)}"
            + $"&timeMin={Uri.EscapeDataString(Rfc3339(from))}"
            + $"&timeMax={Uri.EscapeDataString(Rfc3339(to))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Http.SendAsync(request, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeError(json, (int)response.StatusCode,
                IslandStrings.Get("IslandCalendarReadFailed", "Could not read the calendar")));
        return ParseEvents(json);
    }

    /// <summary>Nombre de la cuenta conectada (el resumen del calendario principal).</summary>
    public static async Task<string> FetchAccountAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PrimaryCalendarEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await Http.SendAsync(request, ct);
            string json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return "";
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            // El nombre de la cuenta es un adorno de ajustes: no es motivo para fallar
            // una conexión que ya funcionó.
            Logger.Warn(ex, "Calendario: no se pudo leer el nombre de la cuenta");
            return "";
        }
    }

    /// <summary>
    /// Guarda en los ajustes los tokens recién emitidos y los persiste en el acto: una
    /// sesión no puede perderse porque la app se cierre sin pasar por ajustes.
    /// </summary>
    public static void Save(UserSettings settings, Tokens tokens)
    {
        settings.GoogleCalendarAccessToken = tokens.AccessToken;
        if (tokens.RefreshToken.Length > 0) settings.GoogleCalendarRefreshToken = tokens.RefreshToken;
        settings.GoogleCalendarTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresInSeconds));
        SettingsManager.SaveSettings();
    }

    // --- red ---

    private static async Task<Tokens> ExchangeAsync(
        string clientId, string clientSecret, string code, string verifier, string redirect, CancellationToken ct) =>
        await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirect,
            ["grant_type"] = "authorization_code",
        }, ct);

    private static async Task<Tokens> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(TokenEndpoint, content, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeError(json, (int)response.StatusCode,
                IslandStrings.Get("IslandCalendarTokenRejected", "Google rejected the token request")));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string access = root.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
        string refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
        int expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int seconds) ? seconds : 3600;
        if (access.Length == 0)
            throw new InvalidOperationException(IslandStrings.Get("IslandCalendarNoAccessToken", "Google did not return an access token."));
        return new Tokens(access, refresh, expires);
    }

    /// <summary>
    /// Escucha la redirección: acepta la primera petición, saca su destino y responde
    /// una página de cortesía para que el navegador no se quede colgado.
    /// </summary>
    private static async Task<string> WaitForCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        string target = "/";
        string? line = await reader.ReadLineAsync(ct);
        if (line != null)
        {
            string[] parts = line.Split(' ');
            if (parts.Length >= 2) target = parts[1];
        }
        // Consumir las cabeceras para no cerrarle el socket antes de responder.
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }

        string body = "<!doctype html><meta charset=\"utf-8\"><title>FluentFlyout</title>"
            + "<body style=\"margin:0;height:100vh;display:grid;place-items:center;background:#101010;"
            + "color:#f3f3f3;font:16px 'Segoe UI',sans-serif\">"
            + "<div style=\"text-align:center\"><h2 style=\"font-weight:600\">"
            + IslandStrings.Get("IslandCalendarPageTitle", "Done")
            + "</h2><p style=\"opacity:.7\">"
            + IslandStrings.Get("IslandCalendarPageBody", "You can close this tab and return to FluentFlyout.")
            + "</p></div>";
        byte[] bytes = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\nContent-Length: "
            + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        return target;
    }

    // --- análisis ---

    private static List<GoogleCalendarEvent> ParseEvents(string json)
    {
        var events = new List<GoogleCalendarEvent>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return events;
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("status", out var status) && status.GetString() == "cancelled") continue;
            // Un recordatorio solo tiene sentido para lo que de verdad ocupa el tiempo:
            // fuera lo marcado como «libre» (no bloquea la agenda) y lo que el usuario
            // rechazó. Se piden expresamente para poder descartarlos aquí.
            if (item.TryGetProperty("transparency", out var transparency) && transparency.GetString() == "transparent") continue;
            if (IsDeclined(item)) continue;
            if (!TryReadMoment(item, "start", out DateTime start)) continue;
            TryReadMoment(item, "end", out DateTime end);
            bool allDay = item.TryGetProperty("start", out var startNode) && startNode.TryGetProperty("date", out _);
            string title = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
            events.Add(new GoogleCalendarEvent
            {
                Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Title = title.Length > 0 ? title : IslandStrings.Get("IslandCalendarUntitled", "(no title)"),
                Start = start,
                End = end >= start ? end : start,
                Location = item.TryGetProperty("location", out var l) ? l.GetString() ?? "" : "",
                AllDay = allDay,
                Link = item.TryGetProperty("htmlLink", out var h) ? h.GetString() ?? "" : "",
            });
        }
        events.Sort((a, b) => a.Start.CompareTo(b.Start));
        return events;
    }

    /// <summary>¿El usuario rechazó la invitación? Entonces no es asunto suyo recordarlo.</summary>
    private static bool IsDeclined(JsonElement item)
    {
        if (!item.TryGetProperty("attendees", out var attendees)) return false;
        foreach (var attendee in attendees.EnumerateArray())
        {
            if (!attendee.TryGetProperty("self", out var self) || self.ValueKind != JsonValueKind.True) continue;
            return attendee.TryGetProperty("responseStatus", out var response) && response.GetString() == "declined";
        }
        return false;
    }

    /// <summary>
    /// Lee «start»/«end» de un evento: con hora viene en «dateTime» (RFC 3339 con
    /// desplazamiento) y de día entero en «date» (solo fecha).
    /// </summary>
    private static bool TryReadMoment(JsonElement item, string key, out DateTime moment)
    {
        moment = default;
        if (!item.TryGetProperty(key, out var node)) return false;
        if (node.TryGetProperty("dateTime", out var dateTime) && dateTime.GetString() is { Length: > 0 } text)
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
            moment = parsed.LocalDateTime;
            return true;
        }
        if (node.TryGetProperty("date", out var date) && date.GetString() is { Length: > 0 } day)
            return DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out moment);
        return false;
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int mark = target.IndexOf('?');
        if (mark < 0) return values;
        foreach (string pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            string value = eq < 0 ? "" : pair[(eq + 1)..];
            values[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return values;
    }

    /// <summary>Mensaje de error legible: el de Google si lo trae, si no el código.</summary>
    private static string DescribeError(string json, int statusCode, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error_description", out var description)
                && description.GetString() is { Length: > 0 } text)
                return text;
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                string? code = error.ValueKind == JsonValueKind.String ? error.GetString()
                    : error.TryGetProperty("message", out var message) ? message.GetString()
                    : null;
                if (code is { Length: > 0 }) return $"{fallback} ({code})";
            }
        }
        catch (JsonException)
        {
            // Sin JSON legible: queda el código HTTP.
        }
        return $"{fallback} (HTTP {statusCode})";
    }

    private static string Rfc3339(DateTime local) =>
        local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
