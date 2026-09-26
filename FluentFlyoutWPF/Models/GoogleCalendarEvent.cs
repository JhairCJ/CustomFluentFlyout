// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Classes;
using System.Globalization;

namespace FluentFlyoutWPF.Models;

/// <summary>
/// Evento del calendario principal de Google tal y como lo pinta el Island: solo lo
/// que hace falta para un recordatorio (título, cuándo empieza y acaba, dónde y si
/// ocupa el día entero).
///
/// <para>Las horas se guardan ya en hora LOCAL: Google devuelve el instante con su
/// desplazamiento y aquí solo se compara con el reloj del equipo, así que no hay
/// conversiones sueltas por la vista.</para>
/// </summary>
public sealed class GoogleCalendarEvent
{
    /// <summary>Identificador del evento en Google (estable entre sincronizaciones).</summary>
    public string Id { get; init; } = "";

    /// <summary>Título. Nunca vacío: un evento sin título se pinta «(sin título)».</summary>
    public string Title { get; init; } = "";

    /// <summary>Comienzo en hora local.</summary>
    public DateTime Start { get; init; }

    /// <summary>Fin en hora local.</summary>
    public DateTime End { get; init; }

    /// <summary>Lugar del evento ("" si no lo tiene).</summary>
    public string Location { get; init; } = "";

    /// <summary>¿Ocupa el día entero? Sin hora no hay cuenta atrás ni recordatorio.</summary>
    public bool AllDay { get; init; }

    /// <summary>Enlace al evento en Google Calendar ("" si no lo trae).</summary>
    public string Link { get; init; } = "";

    // --- texto para las vistas (se recalcula al pintar) ---

    /// <summary>Franja del evento: «09:30–10:15» o «Todo el día».</summary>
    public string WhenText => AllDay
        ? IslandStrings.Get("IslandCalendarAllDay", "All day")
        : $"{Start:HH:mm}–{End:HH:mm}";

    /// <summary>Lugar del evento o su franja si no hay lugar: la segunda línea de la fila.</summary>
    public string DetailText => Location.Length > 0 ? Location : WhenText;

    /// <summary>Cuánto falta para que empiece, en palabras: «en 4 min», «ahora»…</summary>
    public string CountdownText
    {
        get
        {
            if (AllDay) return IslandStrings.Get("IslandCountdownToday", "today");
            TimeSpan left = Start - DateTime.Now;
            if (left <= TimeSpan.Zero) return IslandStrings.Get("IslandCountdownNow", "now");
            if (left.TotalMinutes < 1) return IslandStrings.Get("IslandCountdownUnderMinute", "in less than 1 min");
            if (left.TotalMinutes < 60) return IslandStrings.Format("IslandCountdownMinutes", "in {0} min", Math.Ceiling(left.TotalMinutes));
            if (left.TotalHours < 24) return IslandStrings.Format("IslandCountdownHours", "in {0} h {1} min", (int)left.TotalHours, left.Minutes.ToString("00"));
            if (left.TotalDays < 2) return IslandStrings.Get("IslandCountdownTomorrow", "tomorrow");
            return IslandStrings.Format("IslandCountdownDays", "in {0} days", (int)left.TotalDays);
        }
    }

    /// <summary>Título y franja en una línea: el tooltip de la vista.</summary>
    public string ToolTipText => AllDay || Location.Length == 0
        ? $"{Title}\n{WhenText}"
        : $"{Title}\n{WhenText}\n{Location}";
}
