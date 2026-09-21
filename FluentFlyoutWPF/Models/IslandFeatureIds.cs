// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Models;

/// <summary>
/// Identificadores estables de las funcionalidades del Island.
///
/// <para>El orden de <see cref="All"/> es el orden por defecto de las PANTALLAS (una
/// por funcionalidad) y el orden en el que las funcionalidades se ofrecen en el editor.
/// La navegación y la vista las manda <c>IslandScreens</c>: la rueda y las flechas
/// recorren pantallas y cada pantalla compone sus funcionalidades de izquierda a
/// derecha. No hay ninguna otra lista de orden.</para>
///
/// <para>Cada pantalla lleva como máximo <see cref="MaxFeaturesPerScreen"/>
/// funcionalidades: son las que caben en su composición (las columnas del expandido).
/// En el compacto la pantalla enseña UNA sola: la funcionalidad que sostiene la vista.</para>
///
/// <para>Las pantallas son una AGRUPACIÓN: no hace falta una por funcionalidad. El
/// defecto son dos pantallas de contenido (música/temporizador/cajón/estante e
/// información del día). Los AVISOS (Bluetooth, cargador) no son pantalla: viven
/// fuera de esta lista y se presentan solos cuando ocurre su evento.</para>
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

    /// <summary>Cargador del equipo (avisos de enchufado y desenchufado).</summary>
    public const string Power = "power";

    /// <summary>Orden por defecto de las funcionalidades del contenedor.</summary>
    public static readonly IReadOnlyList<string> All = [Media, Timer, Apps, Shelf, Calendar, Bluetooth, Clipboard, Weather, Power];

    /// <summary>
    /// Funcionalidades que NO son una pantalla: su vista es un AVISO de un evento
    /// (un dispositivo que se conecta, el equipo que se enchufa). No tienen expandido,
    /// no se navega hasta ellas y no se configuran en el editor de pantallas: el
    /// contenedor las presenta cuando su evento ocurre, sin depender de dónde estén
    /// colocadas, porque no están en ninguna parte. Exigirles una pantalla era el
    /// fallo: sin ella el aviso se perdía en silencio (change island-avisos).
    /// </summary>
    public static readonly IReadOnlyList<string> Notices = [Bluetooth, Power];

    /// <summary>¿Es un aviso? (no tiene vista propia: solo su tarjeta temporal)</summary>
    public static bool IsNotice(string? id) => id != null && Notices.Contains(id);

    /// <summary>Funcionalidades que pueden formar parte de una pantalla (todas menos los avisos).</summary>
    public static readonly IReadOnlyList<string> Screenable = [.. All.Where(id => !IsNotice(id))];

    /// <summary>¿Puede esta funcionalidad estar en una pantalla? Los avisos, no.</summary>
    public static bool IsScreenable(string? id) => IsKnown(id) && !IsNotice(id);

    /// <summary>Separador de funcionalidades dentro de una pantalla combinada.</summary>
    public const char ScreenSeparator = '+';

    /// <summary>
    /// Máximo de funcionalidades por pantalla: es lo que cabe en una sola pantalla del
    /// Island (una ficha por funcionalidad en el compacto y una columna en el
    /// expandido). Una pantalla nunca se guarda con más.
    /// </summary>
    public const int MaxFeaturesPerScreen = 4;

    /// <summary>
    /// Pantallas por defecto de una instalación nueva: agrupan por lo que la
    /// funcionalidad ES (contenido del Island, información del día y avisos), no una
    /// pantalla por funcionalidad. Cada grupo cabe en una sola pantalla (máximo
    /// <see cref="MaxFeaturesPerScreen"/> funcionalidades) y ninguna queda fuera: sin
    /// pantalla una funcionalidad no se muestra (change island-pantallas).
    /// </summary>
    public static IReadOnlyList<string> DefaultScreens =>
    [
        Screen(Media, Timer, Apps, Shelf),
        Screen(Calendar, Clipboard, Weather),
    ];

    /// <summary>Compone una pantalla con las funcionalidades dadas (formato guardado).</summary>
    private static string Screen(params string[] ids) => FormatScreen(ids);

    /// <summary>
    /// Lee una pantalla configurada («media+timer») y devuelve sus funcionalidades
    /// en orden, como máximo <see cref="MaxFeaturesPerScreen"/>. Descarta ids
    /// desconocidos, vacíos y repetidos: una pantalla nunca se cae por un ajuste viejo
    /// ni muestra la misma funcionalidad dos veces. El saneado de los ajustes reparte
    /// en varias pantallas lo que viniera de más (nada se pierde al cargar).
    /// </summary>
    public static IReadOnlyList<string> ParseScreen(string? screen)
    {
        if (string.IsNullOrWhiteSpace(screen)) return [];
        var ids = new List<string>();
        foreach (var raw in screen.Split(ScreenSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string id = raw.Trim();
            if (!IsScreenable(id) || ids.Contains(id)) continue;
            ids.Add(id);
            if (ids.Count == MaxFeaturesPerScreen) break;
        }
        return ids;
    }

    /// <summary>Escribe una pantalla a partir de sus funcionalidades (formato guardado).</summary>
    public static string FormatScreen(IEnumerable<string> ids) =>
        string.Join(ScreenSeparator, ids.Where(IsScreenable).Distinct());

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
        Power => "Cargador",
        _ => id ?? "",
    };
}
