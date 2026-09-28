// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// La LÍNEA GRIS del Island —la manija que anuncia que hay algo que abrir— y la
/// FRANJA de puntero que la acompaña, como decisiones puras y comprobables.
///
/// <para>La línea es la MANIJA de una puerta y la franja del borde ES esa puerta: por
/// eso las dos se resuelven aquí y con las mismas reglas. Antes vivían en ramas
/// distintas —la línea en el pintado por frame, la franja en la presentación del
/// puntero— y de ahí salían las dos incoherencias de siempre: línea invisible con
/// puerta activa (un disparador que nadie ve y que resulta molesto) y puerta cerrada
/// con línea pintada (una manija que no abre nada).</para>
///
/// <list type="bullet">
/// <item><b>Un solo offset</b>: en notch la línea va pegada al borde; en la isla
/// flotante la baja el ajuste, acotado. La regla estaba escrita tres veces —pintado,
/// detección de puntero y veto de repliegue— y las tres tenían que coincidir a mano.</item>
/// <item><b>La manija existe si hay puerta</b>: sin nada que abrir no se pinta, porque
/// una raya que no lleva a ninguna parte miente.</item>
/// <item><b>La puerta sigue a lo visible</b>: sin nada dibujado solo hay franja si el
/// usuario pide una puerta invisible a propósito (<c>IslandHiddenAccess</c>).</item>
/// </list>
/// </summary>
public static class IslandLine
{
    /// <summary>Alto de la línea gris (DIP).</summary>
    public const double BarHeight = 3;

    /// <summary>Ancho de la línea gris con la caja oculta del todo (DIP).</summary>
    public const double BarWidth = 120;

    /// <summary>Offset de la línea en notch: es parte del borde y no se mueve (DIP).</summary>
    public const double NotchTopDip = 1;

    /// <summary>
    /// Offset vertical de la línea (y del punto de estado) en DIP desde el borde
    /// superior de la ventana: la regla ÚNICA que comparten el pintado, la franja del
    /// puntero y el veto de repliegue.
    /// </summary>
    public static double TopDip(bool notch, int configuredOffset) =>
        notch ? NotchTopDip : Math.Clamp(configuredOffset, 0, 60);

    /// <summary>
    /// Factor 0..1 del ancho de la línea: 1 con la caja oculta del todo y 0 cuando la
    /// caja (o el punto del revelado) ya ocupa su sitio. Sigue al MÁS RÁPIDO de los dos
    /// muelles porque <c>p</c> termina antes que <c>q</c> al emerger expandido, y se
    /// apaga además con la opacidad del contenido para que la raya no sobreviva al
    /// apagado hacia la pieza inactiva.
    /// </summary>
    public static double WidthFactor(double p, double q, double contentOpacity) =>
        (1 - Math.Max(IslandPhysics.Smooth(p), IslandPhysics.Smooth(q)))
        * Math.Clamp(contentOpacity, 0, 1);

    /// <summary>
    /// ¿Se pinta la línea (y su punto)? Hace falta el ajuste encendido Y algo que
    /// abrir: la línea es la manija de una puerta, nunca un adorno suelto.
    /// </summary>
    public static bool Shown(bool enabled, bool doorAvailable) => enabled && doorAvailable;

    /// <summary>
    /// ¿Existe la franja de puntero del borde superior? Con la caja a la vista la
    /// franja es la propia caja (más su tolerancia); con la caja oculta la sostiene la
    /// línea; y sin nada dibujado solo si el usuario pidió la puerta invisible. Sin
    /// franja, el puntero no hace nada en esa zona.
    /// </summary>
    public static bool AccessZone(bool boxShown, bool lineShown, bool allowWhenHidden) =>
        boxShown || lineShown || allowWhenHidden;
}
