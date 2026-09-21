// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Windows;

namespace FluentFlyoutWPF.Windows;

/// <summary>
/// MODO ULTRA COMPACTO: con la cápsula replegada solo se ven sus DOS EXTREMOS —lo que
/// va a la izquierda y lo que va a la derecha— y el hueco del medio queda vacío. En el
/// control de medios quedan, por ejemplo, la carátula y el ecualizador: lo que
/// identifica el contenido y lo que se puede accionar, sin título ni artista.
///
/// <para>Es el ajuste pensado para convivir con «Volver a inactivo» APAGADO: el Island
/// no vive en pantalla, solo aparece cuando algo hay que enseñar, y su presencia de
/// reposo es mínima. Como en ese caso no hay ni pieza ni cápsula a la que apuntar, la
/// franja de arriba —de la línea de actividad al borde superior— es la puerta que lo
/// trae de vuelta expandido.</para>
/// </summary>
public partial class IslandWindow
{
    /// <summary>
    /// Ancho del compacto en modo ultra: dos extremos y nada más. Está medido sobre el
    /// par más ancho (el icono del temporizador a la izquierda y sus dígitos a la
    /// derecha), que pide unos 107 DIPs con los márgenes del XAML y su separación.
    /// </summary>
    private const double UltraCompactWidth = 120;

    /// <summary>¿Está puesto el modo ultra compacto?</summary>
    private static bool UltraCompactOn => SettingsManager.Current.IslandUltraCompact;

    /// <summary>
    /// Ancho del compacto vigente con el modo ultra por delante: manda sobre el ancho de
    /// la funcionalidad y sobre el del estilo (notch incluido), porque en ultra la
    /// cápsula la fijan sus dos extremos.
    /// </summary>
    private static double RestCompactWidth(double styleWidth) =>
        UltraCompactOn ? UltraCompactWidth : styleWidth;

    /// <summary>
    /// Cuántas celdas caben en un renglón compacto (cajón, estante, portapapeles): el
    /// modo ultra deja UNA, y el resto se cuenta con su «+N», que es el extremo derecho.
    /// </summary>
    private static int CompactRowFit(int fullFit) => UltraCompactOn ? 1 : fullFit;

    /// <summary>
    /// Aplica el modo ultra al compacto vigente: aparta el medio de la vista de delante o
    /// lo devuelve a su regla —que declara la propia funcionalidad en su ficha, porque el
    /// progreso del temporizador depende de su ajuste y el resto se ven siempre—.
    /// <paramref name="refitRows"/> re-cuenta además los renglones de una celda; solo hace
    /// falta al CAMBIAR el ajuste, porque cada refresco de contenido ya cuenta con el modo
    /// (<see cref="CompactRowFit"/>).
    /// </summary>
    private void ApplyUltraCompactContent(bool refitRows = false)
    {
        foreach (var card in _featureCards.Values)
        {
            if (card.Middle is not { } middle) continue;
            middle.Visibility = UltraCompactOn && card.Mode == _contentMode
                ? Visibility.Collapsed
                : card.RestoreMiddle?.Invoke() ?? Visibility.Visible;
        }
        if (!refitRows) return;
        RefreshAppList();
        RefreshShelfList();
        RefreshClipboardViews();
    }

    /// <summary>
    /// ¿Procede que el puntero traiga el Island desde arriba? Solo en modo ultra con el
    /// Island OCULTO del todo: sin pieza ni cápsula no hay nada a lo que apuntar, así que
    /// la franja superior es la única puerta que queda.
    /// </summary>
    private bool UltraCompactPullsFromTop =>
        UltraCompactOn && !IsBoxShown && !Suppressed();

    /// <summary>
    /// Ajuste del modo ultra en caliente: aparta (o devuelve) el medio de la vista
    /// vigente, re-cuenta los renglones y re-mide la cápsula con el ancho nuevo.
    /// </summary>
    public void RefreshUltraCompact() => Dispatcher.Invoke(() =>
    {
        ApplyUltraCompactContent(refitRows: true);
        ApplyContentVisibility();
        RefreshAppearance();
    });
}
