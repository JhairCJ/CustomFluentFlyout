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

    /// <summary>Orden por defecto de las funcionalidades del contenedor.</summary>
    public static readonly IReadOnlyList<string> All = [Media, Timer, Apps, Shelf, Calendar];

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
        _ => id ?? "",
    };
}
