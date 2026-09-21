// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>Qué hacer cuando vence el plazo de un aviso temporal.</summary>
public enum IslandNoticeStep
{
    /// <summary>El aviso ya no es de esta vista (otra tomó el relevo o hay una exclusiva): se olvida sin tocar la vista.</summary>
    Forget,

    /// <summary>El aviso sigue vigente pero no puede retirarse ahora (expandido o bajo el cursor): se reintenta.</summary>
    Retry,

    /// <summary>El aviso se retira: su vista vuelve al reposo.</summary>
    Retract,
}

/// <summary>
/// POLÍTICA del aviso temporal del Island, como decisiones puras y comprobables. Antes
/// la misma condición estaba escrita tres veces de dos formas distintas —en el armado y
/// en el vencimiento— y de ahí salían los avisos que se quedaban pegados o los que
/// aparecían sin que hubiera pasado nada.
/// </summary>
public static class IslandNoticePolicy
{
    /// <summary>
    /// ¿Se arma el plazo? Un aviso FORZADO (Bluetooth, cargador) vence aunque el modo sea
    /// «Visible mientras activo», porque su vista es una notificación y no se queda
    /// pegada; uno normal solo vive en «Aviso temporal». Una exclusiva vigente manda
    /// sobre todo (001 RF-2, 002 RF-16).
    /// </summary>
    public static bool Arms(bool forced, bool temporalMode, bool hasExclusive) =>
        !hasExclusive && (forced || temporalMode);

    /// <summary>
    /// Qué hacer al vencer el plazo (o al reparar un disparo perdido): el aviso no se
    /// cierra bajo el cursor ni con la vista expandida —se reintenta hasta que la vista
    /// vuelva a ser compacta y el ratón no estorbe, así nunca se queda pegado—.
    /// </summary>
    public static IslandNoticeStep OnExpired(bool forced, bool temporalMode, bool hasExclusive,
        bool boxShown, bool expanded, bool pointerOver)
    {
        if (hasExclusive || (!forced && !temporalMode)) return IslandNoticeStep.Forget;
        if (!boxShown) return IslandNoticeStep.Forget;
        if (expanded || pointerOver) return IslandNoticeStep.Retry;
        return IslandNoticeStep.Retract;
    }
}

/// <summary>Candidata a ser la «activa vigente» del contenedor.</summary>
public readonly record struct IslandActivityCandidate(bool Active, bool Usable, DateTime LastEvent);

/// <summary>
/// POLÍTICA de la actividad vigente: quién merece el compacto cuando hay varias
/// funcionalidades activas (001 MOD RF-4/RF-12). Gana la del EVENTO más reciente y, en
/// caso de empate (mismo instante o sin evento registrado), la PRIMERA del orden de las
/// pantallas. Es un desempate explícito: no depende del orden que devuelva un sort
/// inestable ni de ninguna lista de orden aparte.
/// </summary>
public static class IslandActivityPick
{
    /// <summary>Índice de la ganadora, o -1 si ninguna candidata está activa y usable.</summary>
    public static int Winner(IReadOnlyList<IslandActivityCandidate> candidates)
    {
        int best = -1;
        DateTime bestWhen = DateTime.MinValue;
        for (int index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (!candidate.Active || !candidate.Usable) continue;
            // Estrictamente más reciente: a igualdad de evento se queda la primera, que
            // es la del orden de pantallas.
            if (best < 0 || candidate.LastEvent > bestWhen)
            {
                best = index;
                bestWhen = candidate.LastEvent;
            }
        }
        return best;
    }
}
