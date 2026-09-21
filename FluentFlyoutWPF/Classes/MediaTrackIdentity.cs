// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Identidad de la última canción PRESENTADA (título + autor) y detector de cambio de
/// pista. Es lógica pura —sin sesiones, sin UI— y por eso se comprueba sola.
///
/// <para>Reglas que sostiene (001 MOD RF-1):</para>
/// <list type="bullet">
/// <item>Un evento sin TÍTULO no marca nada: los reproductores que lo vacían un instante
/// entre pistas no pueden contar como cambio de canción. El autor conocido se conserva
/// si el evento llega sin él.</item>
/// <item>El título manda: nombre distinto = canción distinta.</item>
/// <item>El autor cuenta solo cuando los DOS lados lo aportan, para no confundir a un
/// reproductor que rellena el autor con retraso con un cambio de canción.</item>
/// </list>
/// </summary>
public sealed class MediaTrackIdentity
{
    /// <summary>Título registrado (vacío = todavía no se conoce ninguna canción).</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Autor registrado (vacío = no se conoce).</summary>
    public string Artist { get; private set; } = string.Empty;

    /// <summary>
    /// Registra lo que anuncia el reproductor y responde si es una canción NUEVA.
    /// </summary>
    public bool Observe(string? title, string? artist)
    {
        string t = (title ?? string.Empty).Trim();
        string a = (artist ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            if (a.Length > 0) Artist = a;
            return false;
        }
        bool changed = Title.Length > 0
            && (!string.Equals(Title, t, StringComparison.Ordinal)
                || (Artist.Length > 0 && a.Length > 0
                    && !string.Equals(Artist, a, StringComparison.Ordinal)));
        Title = t;
        if (a.Length > 0) Artist = a;
        return changed;
    }
}
