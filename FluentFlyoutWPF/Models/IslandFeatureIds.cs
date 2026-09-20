// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Models;

/// <summary>
/// Identificadores estables de las funcionalidades del Island y su orden por defecto.
///
/// <para>El orden de <see cref="All"/> es el que usa el contenedor para navegar (rueda
/// del ratón y flechas laterales) y para desempatar la activa vigente cuando varias
/// funcionalidades lo están a la vez. El usuario puede cambiarlo desde ajustes: se
/// guarda en <c>IslandFeatureOrder</c> y el contenedor reordena su registro en el
/// acto.</para>
/// </summary>
public static class IslandFeatureIds
{
    /// <summary>Control multimedia.</summary>
    public const string Media = "media";

    /// <summary>Temporizador.</summary>
    public const string Timer = "timer";

    /// <summary>Cajón de aplicaciones.</summary>
    public const string Apps = "apps";

    /// <summary>Estante de archivos.</summary>
    public const string Shelf = "shelf";

    /// <summary>Recordatorios de Google Calendar.</summary>
    public const string Calendar = "calendar";

    /// <summary>Dispositivos Bluetooth conectados.</summary>
    public const string Bluetooth = "bluetooth";

    /// <summary>Portapapeles (texto e imágenes).</summary>
    public const string Clipboard = "clipboard";

    /// <summary>Clima del lugar configurado.</summary>
    public const string Weather = "weather";

    /// <summary>Orden por defecto de las funcionalidades del contenedor.</summary>
    public static readonly IReadOnlyList<string> All = [Media, Timer, Apps, Shelf, Calendar, Bluetooth, Clipboard, Weather];

    /// <summary>Separador de funcionalidades dentro de una pantalla combinada.</summary>
    public const char ScreenSeparator = '+';

    /// <summary>
    /// Pantallas por defecto: una por funcionalidad, en el orden de <see cref="All"/>.
    /// Es el comportamiento histórico (cada funcionalidad con su vista rica).
    /// </summary>
    public static IReadOnlyList<string> DefaultScreens => [.. All];

    /// <summary>
    /// Lee una pantalla configurada («media+timer») y devuelve sus funcionalidades
    /// en orden. Descarta ids desconocidos, vacíos y repetidos: una pantalla nunca
    /// se cae por un ajuste viejo ni muestra la misma funcionalidad dos veces.
    /// </summary>
    public static IReadOnlyList<string> ParseScreen(string? screen)
    {
        if (string.IsNullOrWhiteSpace(screen)) return [];
        var ids = new List<string>();
        foreach (var raw in screen.Split(ScreenSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string id = raw.Trim();
            if (!IsKnown(id) || ids.Contains(id)) continue;
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>Escribe una pantalla a partir de sus funcionalidades (formato guardado).</summary>
    public static string FormatScreen(IEnumerable<string> ids) =>
        string.Join(ScreenSeparator, ids.Where(IsKnown).Distinct());

    /// <summary>¿Es un identificador conocido? (los desconocidos se descartan al cargar)</summary>
    public static bool IsKnown(string? id) => id != null && All.Contains(id);

    /// <summary>Nombre visible de una funcionalidad para la página de ajustes.</summary>
    public static string DisplayName(string? id) => id switch
    {
        Media => "Control multimedia",
        Timer => "Temporizador",
        Apps => "Cajón de aplicaciones",
        Shelf => "Estante de archivos",
        Calendar => "Recordatorios de calendario",
        Bluetooth => "Dispositivos Bluetooth",
        Clipboard => "Portapapeles",
        Weather => "Clima",
        _ => id ?? "",
    };
}
