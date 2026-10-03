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
// Línea gris y su franja: una manija con su puerta (IslandLine)
// ------------------------------------------------------------------
// Un solo offset para las tres copias que antes lo repetían (pintado, detección
// de puntero y veto de repliegue): en notch la línea es parte del borde.
CheckNear(IslandLine.TopDip(notch: false, configuredOffset: 0), 0, 1e-9, "flotante: el offset lo pone el ajuste (0)");
CheckNear(IslandLine.TopDip(notch: false, configuredOffset: 60), 60, 1e-9, "flotante: el ajuste manda");
CheckNear(IslandLine.TopDip(notch: false, configuredOffset: 999), 60, 1e-9, "flotante: el offset se acota por arriba");
CheckNear(IslandLine.TopDip(notch: false, configuredOffset: -5), 0, 1e-9, "flotante: el offset se acota por abajo");
CheckNear(IslandLine.TopDip(notch: true, configuredOffset: 45), IslandLine.NotchTopDip, 1e-9, "notch: la línea va pegada al borde");
// La manija existe si hay puerta: una raya que no abre nada miente.
Check(!IslandLine.Shown(enabled: true, doorAvailable: false), "sin nada que abrir no hay línea");
Check(!IslandLine.Shown(enabled: false, doorAvailable: true), "con el ajuste apagado no hay línea");
Check(IslandLine.Shown(enabled: true, doorAvailable: true), "con ajuste y puerta, la línea se pinta");
// La franja del borde sigue a lo visible; sin nada dibujado solo si se pide.
Check(IslandLine.AccessZone(boxShown: true, lineShown: false, allowWhenHidden: false), "con la caja a la vista hay franja");
Check(IslandLine.AccessZone(boxShown: false, lineShown: true, allowWhenHidden: false), "con la línea a la vista hay franja");
Check(!IslandLine.AccessZone(boxShown: false, lineShown: false, allowWhenHidden: false), "sin nada dibujado el puntero no hace nada ahí");
Check(IslandLine.AccessZone(boxShown: false, lineShown: false, allowWhenHidden: true), "la puerta invisible es opcional");
// El ancho: entera con la caja oculta, cero cuando la caja ocupa su sitio.
CheckNear(IslandLine.WidthFactor(0, 0, 1), 1, 1e-9, "caja oculta: línea entera");
CheckNear(IslandLine.WidthFactor(0, 1, 1), 0, 1e-9, "caja revelada: sin línea");
CheckNear(IslandLine.WidthFactor(1, 1, 1), 0, 1e-9, "expandido: sin línea");
CheckNear(IslandLine.WidthFactor(0, 0, 0), 0, 1e-9, "contenido apagado (pieza): sin línea");
Check(IslandLine.WidthFactor(0.1, 0, 1) < 1, "la línea se repliega con la caja");
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
// RF-4 MODIFIED: una tecla ajena pulsada mientras se graba se IGNORA (Ctrl+C no descarta la
// frase que se está dictando); solo Escape cancela, y las del propio atajo tampoco.
Check(!DictationHotkey.Cancels(0x43), "una tecla ajena (C) no cancela el dictado");
Check(DictationHotkey.Cancels(DictationHotkey.VkEscape), "Escape sí cancela el dictado");
Check(!DictationHotkey.Cancels(hookVk), "la tecla del propio atajo no cancela");

// Combinación: solo dispara cuando está COMPLETA, sea cual sea el orden.
var combo = DictationHotkey.Parse("Ctrl+Shift+M");
Check(DictationHotkey.Format(combo) == "Ctrl+Shift+M", "la combinación se escribe igual que se lee");
Check(DictationHotkey.Parse("shift+ctrl+m").SequenceEqual(combo), "el orden al pulsar no cambia el atajo");
// La caja de captura guarda lo que el usuario mantiene (un HashSet, sin orden): lo
// formatea y lo relee el servicio. Las dos direcciones tienen que cuadrar.
string captured = DictationHotkey.Format([0x10, 0x11, 0x4D]);
Check(captured == "Ctrl+Shift+M", "la captura se guarda con los modificadores primero");
Check(DictationHotkey.Parse(captured).SequenceEqual(combo), "y el servicio la relee igual");
Check(!DictationHotkey.Triggers(combo, 0x4D, [0x11, 0x4D]), "sin todos los modificadores no dispara");
Check(DictationHotkey.Triggers(combo, 0x4D, [0x11, 0x10, 0x4D]), "la combinación completa dispara");
Check(DictationHotkey.Ends(combo, 0x10), "soltar cualquiera de sus teclas cierra el dictado");
Check(!DictationHotkey.Cancels(0x4D) && !DictationHotkey.Cancels(0x11),
    "las teclas del propio atajo no cancelan");

// Un ajuste roto no puede dejar el dictado mudo.
Check(!DictationHotkey.IsValid("") && !DictationHotkey.IsValid("   ") && !DictationHotkey.IsValid("Pez"),
    "un atajo vacío o desconocido no es válido");
Check(DictationHotkey.IsValid(DictationHotkey.Default), "el atajo de fábrica es válido");
Check(DictationHotkey.Parse("F5").SequenceEqual([0x74]), "F5 se lee como código de tecla");
Check(DictationHotkey.Parse("ctrl+ctrl+x").SequenceEqual([DictationHotkey.VkCtrl, 0x58]), "sin repetidos");
Ok();

// ------------------------------------------------------------------
// Lista de eventos activos: la vista sale de aquí (IslandActivityRegistry)
// ------------------------------------------------------------------
var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
var registry = new IslandActivityRegistry();
Check(registry.Alive(t0).Count == 0, "sin entradas no hay nada activo");
registry.SetLive("media", active: true, t0);
Check(registry.IsAlive("media", t0) && registry.Alive(t0).Count == 1, "una actividad viva está viva");
registry.SetLive("media", active: true, t0.AddSeconds(5));
Check(registry.Live("media", t0)!.Value.StartedUtc == t0, "repetir el mismo estado no cambia el instante de nacimiento");
registry.SetLive("media", active: false, t0);
Check(!registry.IsAlive("media", t0), "cerrar la actividad la retira de la lista");
// Un AVISO vive su plazo y, a diferencia de la actividad viva, vence solo.
registry.Pulse("bluetooth", t0, t0.AddSeconds(4));
Check(registry.IsAlive("bluetooth", t0.AddSeconds(3)), "el aviso vive mientras corre su plazo");
Check(!registry.IsAlive("bluetooth", t0.AddSeconds(4)) && registry.Alive(t0.AddSeconds(9)).Count == 0,
    "el aviso vencido no sostiene nada");
// Renovar un aviso NO lo convierte en un evento nuevo (su instante no cambia): es lo que
// permite reintentar el vencimiento bajo el cursor sin reiniciar el plazo.
registry.Pulse("bluetooth", t0, t0.AddSeconds(4));
Check(registry.Renew("bluetooth", t0.AddSeconds(6)) && registry.NoticeAlive("bluetooth", t0.AddSeconds(5)),
    "renovar prolonga el plazo sin reiniciarlo");
Check(registry.NoticeStarted("bluetooth", t0.AddSeconds(5)) == t0, "renovar conserva el instante de nacimiento");
Check(registry.NextNoticeExpiry(t0.AddSeconds(5)) == t0.AddSeconds(6), "el vencimiento más cercano manda el despertador");
Check(!registry.Renew("power", t0.AddSeconds(6)), "sin aviso no hay nada que renovar");
// Sólo el aviso FORZADO vence también en «Visible mientras activo» (Bluetooth, cargador).
registry.Pulse("power", t0, t0.AddSeconds(4), alwaysTemporal: true);
Check(registry.NoticeAlwaysTemporal("power", t0) && !registry.NoticeAlwaysTemporal("bluetooth", t0),
    "el aviso forzado se distingue del que solo vive en modo aviso");
// Dos caras de la misma funcionalidad: la más reciente de las dos es la que desempata.
registry.SetLive("timer", active: true, t0);
registry.Pulse("timer", t0.AddSeconds(2), t0.AddSeconds(9));
Check(registry.Live("timer", t0.AddSeconds(3))!.Value.Kind == IslandActivityKind.Notice,
    "de sus dos entradas manda la más reciente");
var live = registry.Alive(t0.AddSeconds(3));
Check(live.Count == 3 && live.Count(e => e.Id == "timer") == 1,
    "cada funcionalidad con algo vivo aparece UNA sola vez en la lista");
registry.Forget("timer");
Check(!registry.Contains("timer"), "olvidar retira actividad y aviso");
// La exclusiva manda: la alerta del temporizador y el dictado.
registry.SetExclusive("timer", true, t0);
Check(registry.HasExclusive(t0) && registry.ExclusiveId(t0) == "timer", "la exclusiva vigente se identifica");
registry.SetExclusive("timer", false, t0);
Check(!registry.HasExclusive(t0), "cerrar la exclusiva la retira");
Ok();

// ------------------------------------------------------------------
// Política de presentación: la lista de activos traducida a UNA vista
// ------------------------------------------------------------------
string[] screens = ["media", "timer", "calendar"];
var at = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
IslandPresentationInput Input(IslandActivityRegistry activity, string[] ids,
    bool suppressed = false, bool returnToInactive = true, bool userExpanded = false,
    string? userExpandedFeatureId = null, bool anyScreenUsable = true,
    string? presentedNoticeId = null, DateTime presentedNoticeStarted = default,
    string[]? usable = null, string[]? expandable = null)
    => new(suppressed, returnToInactive, userExpanded, userExpandedFeatureId, anyScreenUsable,
        presentedNoticeId, presentedNoticeStarted, ids, expandable ?? ids, usable ?? ids, activity, at);

var idle = new IslandActivityRegistry();
// Sin nada activo: pieza inactiva si el ajuste la pide y hay algo que abrir; si no, nada.
var rest = IslandPresentation.Resolve(Input(idle, screens));
Check(rest.View == IslandDesiredView.Inactive && rest.FeatureId == null, "sin actividad reposa en la pieza");
Check(IslandPresentation.Resolve(Input(idle, screens, returnToInactive: false)).View == IslandDesiredView.Hidden,
    "sin «volver a inactivo» el reposo es nada");
Check(IslandPresentation.Resolve(Input(idle, screens, anyScreenUsable: false)).View == IslandDesiredView.Hidden,
    "sin pantalla usable no se ancla una caja vacía");
// Un evento activo se presenta en compacto, en el mismo instante de su evento.
var playing = new IslandActivityRegistry();
playing.SetLive("media", true, at);
var active = IslandPresentation.Resolve(Input(playing, screens));
Check(active.View == IslandDesiredView.Compact && active.FeatureId == "media",
    "un evento activo se presenta en compacto");
// Suprimido: nada, aunque haya actividad... salvo exclusiva.
Check(IslandPresentation.Resolve(Input(playing, screens, suppressed: true)).View == IslandDesiredView.Hidden,
    "suprimido no se muestra nada");
playing.SetExclusive("timer", true, now);
var exclusive = IslandPresentation.Resolve(Input(playing, screens, suppressed: true));
Check(exclusive.View == IslandDesiredView.Expanded && exclusive.FeatureId == "timer",
    "la exclusiva manda y atraviesa la supresión");
// Una exclusiva sin expandido —el dictado vive fuera de las pantallas— se queda en compacto.
var dictating = new IslandActivityRegistry();
dictating.SetExclusive("dictation", true, now);
var dictation = IslandPresentation.Resolve(Input(dictating, [.. screens, "dictation"], expandable: screens));
Check(dictation.View == IslandDesiredView.Compact && dictation.FeatureId == "dictation",
    "una exclusiva sin expandido se queda en compacto");
var fullscreenDictation = IslandPresentation.Resolve(Input(dictating, [.. screens, "dictation"],
    suppressed: true, expandable: screens));
Check(fullscreenDictation.View == IslandDesiredView.Compact && fullscreenDictation.FeatureId == "dictation",
    "el dictado activo conserva su tarjeta compacta en pantalla completa");
var dictationError = new IslandActivityRegistry();
dictationError.Pulse("dictation", at, at.AddSeconds(5), alwaysTemporal: true);
var fullscreenError = IslandPresentation.Resolve(Input(dictationError, [.. screens, "dictation"],
    suppressed: true, expandable: screens));
Check(fullscreenError.View == IslandDesiredView.Compact && fullscreenError.FeatureId == "dictation"
    && fullscreenError.RestartNotice && fullscreenError.AlwaysTemporal,
    "el error de dictado se muestra en pantalla completa con su plazo temporal");
Check(!IslandPresentation.Resolve(Input(dictationError, [.. screens, "dictation"], suppressed: true,
    presentedNoticeId: "dictation", presentedNoticeStarted: at, expandable: screens)).RestartNotice,
    "reconciliar el error en pantalla completa no prolonga su aviso");
Check(IslandPresentation.Resolve(Input(dictationError, [.. screens, "dictation"], suppressed: true,
    usable: screens)).View == IslandDesiredView.Hidden,
    "deshabilitar el dictado oculta su aviso también en pantalla completa");
Check(IslandPresentation.Resolve(Input(dictationError, [.. screens, "dictation"], suppressed: true)
    with { Now = at.AddSeconds(5) }).View == IslandDesiredView.Hidden,
    "al vencer el error se vuelve a ocultar en pantalla completa");
dictationError.SetExclusive("timer", true, at);
Check(IslandPresentation.Resolve(Input(dictationError, [.. screens, "dictation"], suppressed: true,
    expandable: screens)).FeatureId == "timer",
    "la alerta del temporizador conserva prioridad sobre el error de dictado");
// El expandido que el usuario abrió se respeta: un evento ordinario no le quita la vista.
var expanded = IslandPresentation.Resolve(Input(playing, screens, userExpanded: true, userExpandedFeatureId: "timer"));
Check(expanded.View == IslandDesiredView.Expanded && expanded.FeatureId == "timer",
    "el expandido del usuario no lo roba una actividad");
// Con varias activas gana la del evento más reciente y, a igualdad, la del orden de pantallas.
var both = new IslandActivityRegistry();
both.SetLive("timer", true, at.AddSeconds(5));
both.SetLive("media", true, at.AddSeconds(3));
Check(IslandPresentation.Resolve(Input(both, screens)).FeatureId == "timer", "gana el evento más reciente");
var tie = new IslandActivityRegistry();
tie.SetLive("timer", true, at);
tie.SetLive("media", true, at);
Check(IslandPresentation.Resolve(Input(tie, screens)).FeatureId == "media", "empate: la primera del orden de pantallas");
// Un aviso nuevo estrena plazo; re-presentar el mismo no lo reinicia (001 RF-2).
string[] notices = ["bluetooth", "power"];
var notice = new IslandActivityRegistry();
notice.Pulse("bluetooth", at, at.AddSeconds(5), alwaysTemporal: true);
var fresh = IslandPresentation.Resolve(Input(notice, notices));
Check(IslandPresentation.Resolve(Input(notice, notices, suppressed: true)).View == IslandDesiredView.Hidden,
    "los avisos ajenos al dictado siguen ocultos en pantalla completa");
Check(fresh.FeatureId == "bluetooth" && fresh.RestartNotice && fresh.AlwaysTemporal,
    "un aviso nuevo estrena su plazo y conserva su carácter forzado");
var again = IslandPresentation.Resolve(Input(notice, notices, presentedNoticeId: "bluetooth", presentedNoticeStarted: at));
Check(again.FeatureId == "bluetooth" && !again.RestartNotice, "re-presentar el mismo aviso no reinicia su plazo");
// Una actividad que no puede abrirse no sostiene la vista: ni la música apagada ni un aviso
// cuya funcionalidad se deshabilitó.
var mediaOnly = new IslandActivityRegistry();
mediaOnly.SetLive("media", true, at);
Check(IslandPresentation.Resolve(Input(mediaOnly, screens, usable: ["timer"])).View == IslandDesiredView.Inactive,
    "una activa no usable no abre nada");
Check(IslandPresentation.Resolve(Input(notice, notices, usable: [])).View == IslandDesiredView.Inactive,
    "un aviso no usable tampoco sostiene la vista");
Ok();

// ------------------------------------------------------------------
// Widget: la misma superficie crece hacia el monitor, sin hueco ni segunda tarjeta.
// ------------------------------------------------------------------
var work = new TaskbarWidgetExpansion.Box(0, 0, 1920, 1032);
var bottomBar = new TaskbarWidgetExpansion.Box(0, 1032, 1920, 48);
var widget = new TaskbarWidgetExpansion.Box(600, 1036, 160, 40);
var expandedWidget = TaskbarWidgetExpansion.Expand(widget, bottomBar, work, 340, 156);
CheckNear(expandedWidget.Left, 510, 0.001, "se expande alrededor del centro del widget");
CheckNear(expandedWidget.Bottom, widget.Bottom, 0.001, "conserva el borde inferior de la superficie compacta");
CheckNear(expandedWidget.Top, 920, 0.001, "crece hacia arriba sin separación");
var rightAligned = TaskbarWidgetExpansion.Expand(new(1890, 1036, 30, 40), bottomBar, work, 340, 156);
CheckNear(rightAligned.Right, 1920, 0.001, "el widget expandido no sale por el borde derecho");
var topBar = new TaskbarWidgetExpansion.Box(0, 0, 1920, 48);
var topWork = new TaskbarWidgetExpansion.Box(0, 48, 1920, 1032);
var topWidget = new TaskbarWidgetExpansion.Box(600, 4, 160, 40);
CheckNear(TaskbarWidgetExpansion.Expand(topWidget, topBar, topWork, 340, 156).Top, 4, 0.001,
    "con la barra arriba crece hacia abajo conservando su borde");
var leftBar = new TaskbarWidgetExpansion.Box(0, 0, 48, 1080);
var leftWork = new TaskbarWidgetExpansion.Box(48, 0, 1872, 1080);
CheckNear(TaskbarWidgetExpansion.Expand(new(4, 600, 40, 160), leftBar, leftWork, 340, 156).Left, 4, 0.001,
    "con la barra izquierda conserva su borde y crece hacia la derecha");
var rightBar = new TaskbarWidgetExpansion.Box(1872, 0, 48, 1080);
var rightWork = new TaskbarWidgetExpansion.Box(0, 0, 1872, 1080);
CheckNear(TaskbarWidgetExpansion.Expand(new(1876, 600, 40, 160), rightBar, rightWork, 340, 156).Right, 1916, 0.001,
    "con la barra derecha crece hacia la izquierda");
Check(TaskbarWidgetExpansion.EdgeOf(bottomBar, new(0, 0, 1920, 1080)) == TaskbarWidgetExpansion.Edge.Bottom,
    "la barra autooculta conserva la dirección de expansión");
// Un monitor a la izquierda del principal y escalado al 150 % conserva píxeles físicos.
var negativeBar = new TaskbarWidgetExpansion.Box(-2560, -100, 2560, 72);
var negativeWork = new TaskbarWidgetExpansion.Box(-2560, -1468, 2560, 1368);
var negativeWidget = new TaskbarWidgetExpansion.Box(-2000, -94, 240, 60);
var scaledExpansion = TaskbarWidgetExpansion.Expand(negativeWidget, negativeBar, negativeWork, 340 * 1.5, 156 * 1.5);
CheckNear(scaledExpansion.Bottom, -34, 0.001, "DPI y coordenadas negativas conservan el anclaje");
CheckNear(scaledExpansion.Width / 1.5, 340, 0.001, "el ancho visual sigue siendo 340 DIPs");
var tiny = TaskbarWidgetExpansion.Expand(new(0, 90, 60, 10), new(0, 90, 100, 10), new(0, 0, 100, 90), 340, 156);
Check(tiny.Left >= 0 && tiny.Top >= 0 && tiny.Right <= 100 && tiny.Bottom <= 100,
    "una pantalla pequeña limita también el tamaño, no solo la posición");
Ok();

Console.WriteLine($"selfcheck: {passed} bloques correctos");
