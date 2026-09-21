// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Classes;

/// <summary>
/// Coeficientes del muelle subamortiguado del Island, ya escalados por la duración
/// global de animaciones.
/// </summary>
public readonly record struct IslandSpring(double KP, double CP, double KQ, double CQ);

/// <summary>
/// FÍSICA del Island: el muelle que gobierna su geometría y las curvas que la pintan.
/// Es aritmética pura, sin WPF y sin estado del contenedor, así que se puede comprobar
/// sin abrir una ventana (antes vivía dentro del motor por frame, donde solo se podía
/// validar a ojo).
///
/// <para>Reglas que sostiene:</para>
/// <list type="bullet">
/// <item>Amortiguamiento elegido para que el muelle REBOTE como los de Apple
/// (ζ ≈ 0.58 en el morfe, ζ ≈ 0.72 en el revelado): al llegar al tamaño final se pasa
/// un poco y vuelve. El rebote es el mismo a cualquier velocidad configurada, porque la
/// duración escala frecuencia y amortiguamiento a la vez.</item>
/// <item>El paso del muelle está acotado por debajo (1/240 s) y por arriba (1/25 s), de
/// modo que un frame perdido o una suspensión no lo desbocan.</item>
/// <item>El rebote pintado tiene techo (<see cref="BounceLimit"/>) y suelo mínimo
/// (<see cref="BounceCompress"/>): comprimirse por debajo del destino se escala con el
/// salto al expandido y adelgazaba el reposo.</item>
/// </list>
/// </summary>
public static class IslandPhysics
{
    /// <summary>Paso mínimo del muelle (un frame a 240 Hz).</summary>
    public const double MinStepSeconds = 1.0 / 240.0;

    /// <summary>Paso máximo del muelle (un frame a 25 Hz): un frame perdido no desboca el rebote.</summary>
    public const double MaxStepSeconds = 1.0 / 25.0;

    /// <summary>Techo del rebote: cuánto puede pasarse la geometría de su tamaño final.</summary>
    public const double BounceLimit = 0.12;

    /// <summary>Cuánto puede comprimirse la geometría por debajo de su destino.</summary>
    public const double BounceCompress = 0.015;

    /// <summary>
    /// Coeficientes del muelle para una duración global y un estilo, memorizados por
    /// (duración, estilo): el frame solo los lee, no los recalcula.
    /// </summary>
    public static IslandSpring Coefficients(double durationMs, bool notch)
    {
        if (durationMs != _duration || notch != _notch)
        {
            _duration = durationMs;
            _notch = notch;
            double durationScale = durationMs > 0 ? durationMs / 300.0 : 1.0;
            double frequencyScale = 1.0 / (durationScale * durationScale);
            double dampingScale = 1.0 / durationScale;
            double kp = 520 * frequencyScale;
            double cp = 26 * dampingScale;
            double kq = 200 * frequencyScale;
            double cq = 20 * dampingScale; // ambos estilos emergen desde el centro como Island
            // El notch empuja un 5% más fuerte (sube la rigidez, no el amortiguamiento),
            // así que su ζ baja un 2%: 0,570 → 0,556. Es el rebote que ya se veía; está
            // fijado en el check para que nadie lo cambie sin querer.
            if (notch) kp *= 1.05;
            _spring = new IslandSpring(kp, cp, kq, cq);
        }
        return _spring;
    }

    private static double _duration = double.NaN;
    private static bool _notch;
    private static IslandSpring _spring;

    /// <summary>
    /// Un paso del muelle (integración semi-implícita de Euler). El desplazamiento se
    /// acota a [-0.15, 1.15] para que el overshoot se vea pero no se desborde.
    /// </summary>
    public static void Step(ref double x, ref double v, double target, double k, double c, double dt)
    {
        double a = (target - x) * k - v * c;
        v += a * dt;
        x += v * dt;
        x = Math.Clamp(x, -0.15, 1.15);
    }

    /// <summary>¿El muelle ya está en su destino? (umbral de posición y de velocidad)</summary>
    public static bool Settled(double x, double v, double target) =>
        Math.Abs(x - target) < 0.002 && Math.Abs(v) < 0.02;

    /// <summary>Suavizado de Hermite (arranca y frena solo): el easing de todo el Island.</summary>
    public static double Smooth(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// Progreso con el REBOTE del muelle intacto por arriba y comprimido por abajo: el
    /// contenedor se pasa un poco de su tamaño y vuelve —el rebote de Apple— en lugar de
    /// frenar en seco, pero no se adelgaza por debajo del destino.
    /// </summary>
    public static double BounceCurve(double t) => Math.Clamp(t, -BounceCompress, 1 + BounceLimit);

    /// <summary>
    /// Curva del estirón del revelado (oculto → punto → ancho de reposo). Mantiene la
    /// forma suave original —el punto nace sin dar un salto al cruzar su umbral de
    /// ancho— y deja pasar el rebote SOLO en la cola, cuando el muelle ya se ha pasado de
    /// su objetivo: inyectarlo en toda la curva multiplicaría el valor del umbral y el
    /// punto pegaría un tirón al empezar a estirarse.
    /// </summary>
    public static double RevealStretch(double q)
    {
        double t = (q - 0.18) / 0.82;
        if (t <= 0) return 0;
        if (t < 1) return Smooth(t);
        return 1 + Math.Min(t - 1, BounceLimit);
    }

    /// <summary>Acota el paso de un frame al rango que el muelle entiende.</summary>
    public static double ClampStep(double seconds) =>
        Math.Clamp(seconds, MinStepSeconds, MaxStepSeconds);
}
