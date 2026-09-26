using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;

// Comprobación ejecutable de la lógica PURA del Island. Cada bloque cubre una regla que
// hasta ahora solo se podía validar a ojo: el parseo de pantallas, la física del motor,
// el detector de cambio de pista, la política del aviso temporal y el desempate de la
// activa vigente. Un fallo aquí es un fallo del Island, sin abrir siquiera la ventana.

static void Check(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"FALLO: {what}");
}

static void CheckNear(double actual, double expected, double tolerance, string what)
{
    if (Math.Abs(actual - expected) > tolerance)
        throw new InvalidOperationException($"FALLO: {what} ({actual} != {expected} ±{tolerance})");
}

int passed = 0;
void Ok() => passed++;

// ------------------------------------------------------------------
// Pantallas: qué funcionalidad tiene vista y cuáles son AVISOS
// ------------------------------------------------------------------
Check(IslandFeatureIds.IsNotice("bluetooth") && IslandFeatureIds.IsNotice("power"), "bluetooth y power son avisos");
Check(!IslandFeatureIds.IsNotice("media") && !IslandFeatureIds.IsNotice("weather"), "media y clima no son avisos");
Check(IslandFeatureIds.Screenable.Count == IslandFeatureIds.All.Count - IslandFeatureIds.Notices.Count,
    "Screenable es All menos los avisos");
Ok();

// La pantalla de avisos de un ajuste viejo desaparece; lo demás se conserva.
Check(IslandFeatureIds.ParseScreen("bluetooth+power").Count == 0, "bluetooth+power se queda sin pantalla");
Check(IslandFeatureIds.ParseScreen("media+bluetooth").SequenceEqual(["media"]), "media+bluetooth conserva media");
Check(IslandFeatureIds.ParseScreen("media+shelf+timer+apps").SequenceEqual(["media", "shelf", "timer", "apps"]),
    "una pantalla de contenido se parsea entera, en orden");
Check(IslandFeatureIds.FormatScreen(["media", "power", "timer"]) == "media+timer", "FormatScreen descarta los avisos");
Check(IslandFeatureIds.ParseScreen("media+media+desconocida").SequenceEqual(["media"]), "ids repetidos y desconocidos fuera");
foreach (string screen in IslandFeatureIds.DefaultScreens)
{
    var ids = IslandFeatureIds.ParseScreen(screen);
    Check(ids.Count > 0 && ids.All(IslandFeatureIds.IsScreenable), $"la pantalla de fábrica «{screen}» es usable");
}
Ok();

// ------------------------------------------------------------------
// Física del motor: el muelle del contenedor
// ------------------------------------------------------------------
// Amortiguamiento documentado: ζ = c / (2·√k) ≈ 0,58 en el morfe y ≈ 0,72 en el
// revelado. Si alguien toca los coeficientes, el rebote deja de ser el de Apple y
// esta comprobación lo dice.
static double Damping(double k, double c) => c / (2 * Math.Sqrt(k));
var spring = IslandPhysics.Coefficients(300, notch: false);
CheckNear(Damping(spring.KP, spring.CP), 0.58, 0.02, "ζ del morfe");
CheckNear(Damping(spring.KQ, spring.CQ), 0.72, 0.02, "ζ del revelado");
// Notch empuja un 5% más fuerte (sube la rigidez, no el amortiguamiento): ζ baja un 2%.
var notchSpring = IslandPhysics.Coefficients(300, notch: true);
Check(notchSpring.KP > spring.KP, "notch empuja algo más fuerte");
CheckNear(Damping(notchSpring.KP, notchSpring.CP), 0.556, 0.005, "ζ del morfe en notch");
// La duración global escala k y c a la vez: el rebote es el mismo a cualquier velocidad.
var fast = IslandPhysics.Coefficients(150, notch: false);
var slow = IslandPhysics.Coefficients(600, notch: false);
CheckNear(Damping(fast.KP, fast.CP), Damping(slow.KP, slow.CP), 0.001, "ζ es la misma a cualquier velocidad");
// Y el memo devuelve el mismo valor para la misma entrada.
Check(IslandPhysics.Coefficients(300, notch: false) == spring, "los coeficientes están memorizados por (duración, estilo)");
Ok();

// Un paso del muelle llega a su destino (y se pasa un poco antes: es el rebote).
static (double X, double V, double Overshoot) Simulate(double k, double c, double dt, double seconds)
{
    double x = 0, v = 0, overshoot = 0;
    for (double t = 0; t < seconds; t += dt)
    {
        IslandPhysics.Step(ref x, ref v, 1, k, c, dt);
        if (x > overshoot) overshoot = x;
    }
    return (x, v, overshoot);
}
var settle = Simulate(spring.KP, spring.CP, 1.0 / 60, 2.0);
Check(IslandPhysics.Settled(settle.X, settle.V, 1), "el muelle asienta en su destino");
Check(settle.Overshoot > 1 && settle.Overshoot <= 1.15, "el muelle rebota y no se desboca");
// Un frame perdido (paso grande, ya acotado) tampoco lo desboca.
var rough = Simulate(spring.KP, spring.CP, IslandPhysics.MaxStepSeconds, 2.0);
Check(rough.Overshoot <= 1.15, "un frame perdido no desboca el rebote");
Ok();

Check(IslandPhysics.Settled(1, 0, 1), "en destino y parado = asentado");
Check(!IslandPhysics.Settled(1, 1, 1), "con velocidad no está asentado");
CheckNear(IslandPhysics.ClampStep(0.5), IslandPhysics.MaxStepSeconds, 1e-9, "el paso se acota por arriba");
CheckNear(IslandPhysics.ClampStep(0.0001), IslandPhysics.MinStepSeconds, 1e-9, "el paso se acota por abajo");
CheckNear(IslandPhysics.Smooth(0), 0, 1e-9, "suavizado en 0");
CheckNear(IslandPhysics.Smooth(1), 1, 1e-9, "suavizado en 1");
Check(IslandPhysics.Smooth(0.3) < IslandPhysics.Smooth(0.7), "el suavizado no baja");
CheckNear(IslandPhysics.BounceCurve(-5), -IslandPhysics.BounceCompress, 1e-9, "la compresión tiene suelo");
CheckNear(IslandPhysics.BounceCurve(5), 1 + IslandPhysics.BounceLimit, 1e-9, "el rebote tiene techo");
CheckNear(IslandPhysics.RevealStretch(0.18), 0, 1e-9, "el estirón nace en el umbral del punto");
CheckNear(IslandPhysics.RevealStretch(1), 1, 1e-9, "el estirón aterriza en 1 (sin escalón)");
Check(IslandPhysics.RevealStretch(1.05) > 1, "el estirón deja pasar el rebote solo en la cola");
CheckNear(IslandPhysics.RevealStretch(5), 1 + IslandPhysics.BounceLimit, 1e-9, "el estirón no se desboca");
Ok();

// ------------------------------------------------------------------
// Detector de cambio de pista (001 MOD RF-1)
// ------------------------------------------------------------------
var track = new MediaTrackIdentity();
Check(!track.Observe("Canción", "Autor"), "el primer título no es un cambio");
Check(!track.Observe("Canción", "Autor"), "el mismo título no es un cambio");
Check(track.Observe("Otra", "Autor"), "otro título SÍ es un cambio");
Check(!track.Observe("Otra", "Autor"), "y no vuelve a serlo al repetirse");
Check(track.Observe("Otra", "Otro autor"), "otro autor (los dos lados lo aportan) es un cambio");
// Un reproductor que vacía el título entre pistas no marca un falso cambio...
Check(!track.Observe("", "Autor tardío"), "un evento sin título no es un cambio");
Check(track.Artist == "Autor tardío", "pero el autor conocido se conserva");
Check(!track.Observe("Otra", ""), "y la misma canción sigue sin ser un cambio");
// ...ni un autor que llega con retraso cuenta como canción nueva.
var late = new MediaTrackIdentity();
late.Observe("Tema", "");
Check(!late.Observe("Tema", "Autor que llega tarde"), "un autor tardío no es una canción nueva");
Ok();

// ------------------------------------------------------------------
// Política del aviso temporal (001 RF-2, 002 RF-16)
// ------------------------------------------------------------------
Check(!IslandNoticePolicy.Arms(forced: false, temporalMode: false, hasExclusive: false), "en «activo» un aviso normal no se arma");
Check(IslandNoticePolicy.Arms(forced: true, temporalMode: false, hasExclusive: false), "el aviso forzado vence también en «activo»");
Check(IslandNoticePolicy.Arms(forced: false, temporalMode: true, hasExclusive: false), "en «temporal» se arma");
Check(!IslandNoticePolicy.Arms(forced: true, temporalMode: true, hasExclusive: true), "una exclusiva manda: no se arma");
Check(!IslandNoticePolicy.Arms(forced: false, temporalMode: true, hasExclusive: true), "una exclusiva manda (aviso normal)");
// Al vencer: se retira al reposo si la vista es compacta y el ratón no estorba.
Check(IslandNoticePolicy.OnExpired(false, true, false, boxShown: true, expanded: false, pointerOver: false)
    == IslandNoticeStep.Retract, "vencido, compacto y sin ratón encima: se retira");
Check(IslandNoticePolicy.OnExpired(false, true, false, true, expanded: true, pointerOver: false)
    == IslandNoticeStep.Retry, "expandido: se reintenta");
Check(IslandNoticePolicy.OnExpired(false, true, false, true, expanded: false, pointerOver: true)
    == IslandNoticeStep.Retry, "bajo el cursor: se reintenta");
Check(IslandNoticePolicy.OnExpired(true, false, false, false, false, false)
    == IslandNoticeStep.Forget, "sin caja: se olvida");
Check(IslandNoticePolicy.OnExpired(false, false, false, true, false, false)
    == IslandNoticeStep.Forget, "modo «activo» con aviso no forzado: se olvida");
Check(IslandNoticePolicy.OnExpired(false, true, true, true, false, false)
    == IslandNoticeStep.Forget, "una exclusiva vigente: se olvida");
Check(IslandNoticePolicy.OnExpired(true, false, false, true, false, false)
    == IslandNoticeStep.Retract, "el forzado se retira también en «activo»");
Ok();

// ------------------------------------------------------------------
// Activa vigente: el evento más reciente, y a igualdad el orden de pantallas
// ------------------------------------------------------------------
var now = DateTime.UtcNow;
Check(IslandActivityPick.Winner([]) == -1, "sin candidatas no hay activa vigente");
Check(IslandActivityPick.Winner([new(false, true, now), new(false, true, now)]) == -1, "sin actividad no hay activa vigente");
Check(IslandActivityPick.Winner([new(true, false, now), new(false, true, now)]) == -1, "una que no es usable no gana");
Check(IslandActivityPick.Winner([new(true, true, now), new(true, true, now)]) == 0, "empate: gana la primera del orden de pantallas");
Check(IslandActivityPick.Winner([new(true, true, now), new(true, true, now.AddSeconds(1))]) == 1, "gana la del evento más reciente");
Check(IslandActivityPick.Winner([new(false, true, now.AddSeconds(9)), new(true, true, now)]) == 1, "una inactiva no gana aunque sea más reciente");
Check(IslandActivityPick.Winner([new(true, true, default), new(true, true, now)]) == 1, "sin evento registrado vale la fecha mínima");
Ok();

// ------------------------------------------------------------------
// Dictado (spec 006): el atajo que dispara, termina y cancela
// ------------------------------------------------------------------
// El gancho de teclado entrega el modificador FÍSICO (0xA2 = Ctrl izquierdo) y el
// ajuste dice «Ctrl»: sin esta unificación un atajo de Ctrl no dispararía nunca.
Check(DictationHotkey.Normalize(0xA2) == DictationHotkey.VkCtrl, "Ctrl izquierdo → Ctrl");
Check(DictationHotkey.Normalize(0xA5) == DictationHotkey.VkAlt, "Alt derecho → Alt");
Check(DictationHotkey.Normalize(0x5C) == DictationHotkey.VkWin, "Win derecho → Win");
Check(DictationHotkey.Normalize(0x4D) == 0x4D, "una tecla normal no se toca");

// Una tecla sola: mantén Ctrl, habla, suelta.
var singleKey = DictationHotkey.Parse("Ctrl");
Check(singleKey.SequenceEqual([DictationHotkey.VkCtrl]), "«Ctrl» es un atajo de una tecla");
int hookVk = DictationHotkey.Normalize(0xA2);
Check(DictationHotkey.Triggers(singleKey, hookVk, [hookVk]), "mantener Ctrl dispara el dictado");
Check(!DictationHotkey.Triggers(singleKey, hookVk, []), "Ctrl sin estar pulsado no dispara nada");
Check(DictationHotkey.Ends(singleKey, hookVk), "soltar Ctrl cierra el dictado");
Check(DictationHotkey.Cancels(singleKey, 0x43), "una tecla ajena (Ctrl+C) cancela el dictado");

// Combinación: solo dispara cuando está COMPLETA, sea cual sea el orden.
var combo = DictationHotkey.Parse("Ctrl+Shift+M");
Check(DictationHotkey.Format(combo) == "Ctrl+Shift+M", "la combinación se escribe igual que se lee");
Check(DictationHotkey.Parse("shift+ctrl+m").SequenceEqual(combo), "el orden al pulsar no cambia el atajo");
Check(!DictationHotkey.Triggers(combo, 0x4D, [0x11, 0x4D]), "sin todos los modificadores no dispara");
Check(DictationHotkey.Triggers(combo, 0x4D, [0x11, 0x10, 0x4D]), "la combinación completa dispara");
Check(DictationHotkey.Ends(combo, 0x10), "soltar cualquiera de sus teclas cierra el dictado");
Check(!DictationHotkey.Cancels(combo, 0x4D) && !DictationHotkey.Cancels(combo, 0x11),
    "las teclas del propio atajo no cancelan");

// Un ajuste roto no puede dejar el dictado mudo.
Check(!DictationHotkey.IsValid("") && !DictationHotkey.IsValid("   ") && !DictationHotkey.IsValid("Pez"),
    "un atajo vacío o desconocido no es válido");
Check(DictationHotkey.IsValid(DictationHotkey.Default), "el atajo de fábrica es válido");
Check(DictationHotkey.Parse("F5").SequenceEqual([0x74]), "F5 se lee como código de tecla");
Check(DictationHotkey.Parse("ctrl+ctrl+x").SequenceEqual([DictationHotkey.VkCtrl, 0x58]), "sin repetidos");
Ok();

Console.WriteLine($"selfcheck: {passed} bloques correctos");
