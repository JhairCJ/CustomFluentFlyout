// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>Vista que el contenedor debe mostrar AHORA (change island-lista-de-activos).</summary>
public enum IslandDesiredView
{
    /// <summary>Nada: ni pieza ni caja (supresión, o reposo sin pieza).</summary>
    Hidden,

    /// <summary>La pieza inactiva (cuadrado oscuro estrecho).</summary>
    Inactive,

    /// <summary>El compacto de una funcionalidad (lo activo, en el mismo turno).</summary>
    Compact,

    /// <summary>El expandido de una funcionalidad o de su pantalla.</summary>
    Expanded,
}

/// <summary>
/// Todo lo que la política necesita saber para decidir la vista. Es un snapshot: la
/// política no consulta nada por su cuenta (ni ajustes, ni el sistema multimedia, ni
/// Windows), así que es pura y comprobable.
/// </summary>
public readonly record struct IslandPresentationInput(
    bool Suppressed,
    bool ReturnToInactive,
    bool UserExpanded,
    string? UserExpandedFeatureId,
    bool AnyScreenUsable,
    string? PresentedNoticeId,
    DateTime PresentedNoticeStarted,
    IReadOnlyList<string> ScreenOrderedIds,
    /// <summary>Funcionalidades que TIENEN expandido propio: las de una pantalla. Una exclusiva
    /// sin expandido (el dictado) vive en el compacto, así que no se despliega.</summary>
    IReadOnlyList<string> ExpandableIds,
    IReadOnlyList<string> UsableIds,
    IslandActivityRegistry Activity,
    DateTime Now);

/// <summary>Vista deseada, con la funcionalidad que la sostiene y el plazo del aviso.</summary>
public readonly record struct IslandPresentationResult(
    IslandDesiredView View,
    string? FeatureId,
    bool RestartNotice,
    bool AlwaysTemporal);

/// <summary>
/// POLÍTICA de presentación del Island (change island-lista-de-activos): traduce la LISTA
/// DE EVENTOS ACTIVOS a UNA vista deseada, en un solo sitio y sin ramas por modo
/// repartidas por medio contenedor.
///
/// <para>Reglas, en este orden:</para>
/// <list type="number">
/// <item>Acceso exclusivo vigente → su vista expandida; manda sobre todo y atraviesa la
/// supresión (001 RF-8/14, 002 RF-2, spec 006 RF-9).</item>
/// <item>Supresión contextual → nada (001 MOD RF-14).</item>
/// <item>El expandido que el usuario tiene abierto → se respeta: un evento ordinario no le
/// quita la vista que él decidió abrir (001 RF-24).</item>
/// <item>Actividad viva → el COMPACTO de la ganadora: la del evento más reciente y, a
/// igualdad, la primera del orden de las pantallas (001 MOD RF-4/RF-12). Un aviso nuevo
/// estrena su plazo; re-presentar el mismo no lo reinicia (001 RF-2).</item>
/// <item>Sin actividad → reposo: la pieza inactiva si el ajuste la pide y hay algo usable
/// que abrir; si no, nada (001 MOD RF-2).</item>
/// </list>
///
/// <para>Antes estas reglas estaban escritas seis veces —en la actividad orientada a
/// eventos, en los replegues, en el aviso y en los ajustes—, cada una con sus guardas: de
/// ahí salían los compactos que tardaban en aparecer y los que no aparecían nunca.</para>
/// </summary>
public static class IslandPresentation
{
    /// <summary>Vista que toca mostrar con el estado dado.</summary>
    public static IslandPresentationResult Resolve(IslandPresentationInput input)
    {
        // 1. La exclusiva manda: la alerta del temporizador (tiene expandido) y el dictado
        // en marcha (su tarjeta vive en el compacto, fuera de las pantallas).
        if (input.Activity.ExclusiveId(input.Now) is { } exclusive && IsUsable(input, exclusive))
        {
            bool expandable = input.ExpandableIds.Contains(exclusive);
            return new IslandPresentationResult(
                expandable ? IslandDesiredView.Expanded : IslandDesiredView.Compact,
                exclusive, RestartNotice: !expandable, AlwaysTemporal: !expandable);
        }

        // 2. Suprimido (juego a pantalla completa, presentación, equipo bloqueado): nada.
        if (input.Suppressed) return new IslandPresentationResult(IslandDesiredView.Hidden, null, false, false);

        // 3. El expandido que el usuario abrió se respeta mientras nada exclusivo lo tome.
        if (input.UserExpanded)
            return new IslandPresentationResult(IslandDesiredView.Expanded, input.UserExpandedFeatureId, false, false);

        // 4. La lista de activos: gana la del evento más reciente, empate por pantallas.
        var winner = Winner(input);
        if (winner is { } entry)
        {
            bool notice = entry.Kind == IslandActivityKind.Notice;
            bool restart = notice
                && (input.PresentedNoticeId != entry.Id || input.PresentedNoticeStarted != entry.StartedUtc);
            return new IslandPresentationResult(IslandDesiredView.Compact, entry.Id, restart,
                notice && entry.AlwaysTemporal);
        }

        // 5. Reposo: la pieza inactiva o nada.
        bool piece = input.ReturnToInactive && input.AnyScreenUsable;
        return new IslandPresentationResult(piece ? IslandDesiredView.Inactive : IslandDesiredView.Hidden, null, false, false);
    }

    /// <summary>
    /// Entrada ganadora: la más reciente de las que están vivas Y son usables, con las
    /// candidatas en ORDEN DE PANTALLAS —que es el desempate— y la elección en la política
    /// pura de siempre (<see cref="IslandActivityPick"/>).
    /// </summary>
    private static IslandActivityEntry? Winner(IslandPresentationInput input)
    {
        var candidates = new List<IslandActivityCandidate>(input.ScreenOrderedIds.Count);
        var entries = new List<IslandActivityEntry?>(input.ScreenOrderedIds.Count);
        foreach (string id in input.ScreenOrderedIds)
        {
            var entry = input.Activity.Live(id, input.Now);
            entries.Add(entry);
            bool usable = IsUsable(input, id);
            candidates.Add(new IslandActivityCandidate(entry != null, usable,
                entry?.StartedUtc ?? DateTime.MinValue));
        }
        int index = IslandActivityPick.Winner(candidates);
        return index < 0 ? null : entries[index];
    }

    /// <summary>¿Esta funcionalidad puede abrirse ahora mismo? (habilitada y disponible).</summary>
    private static bool IsUsable(IslandPresentationInput input, string id) => input.UsableIds.Contains(id);
}
