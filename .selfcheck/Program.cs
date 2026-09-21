using FluentFlyoutWPF.Models;

// Comprobación del parseo de pantallas: es la pieza que decide qué funcionalidad
// tiene vista y cuáles son avisos. Un ajuste viejo con la pantalla «bluetooth+power»
// (la que no debía existir) tiene que quedarse sin pantalla y no llevarse nada por
// delante; las funcionalidades de verdad siguen parseándose igual.

static void Check(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"FALLO: {what}");
    Console.WriteLine($"ok: {what}");
}

// Un aviso no es pantalla.
Check(IslandFeatureIds.IsNotice("bluetooth") && IslandFeatureIds.IsNotice("power"), "bluetooth y power son avisos");
Check(!IslandFeatureIds.IsNotice("media") && !IslandFeatureIds.IsNotice("weather"), "media y clima no son avisos");
Check(IslandFeatureIds.Screenable.Count == IslandFeatureIds.All.Count - IslandFeatureIds.Notices.Count,
    "Screenable es All menos los avisos");

// La pantalla de avisos de un ajuste viejo desaparece; lo demás se conserva.
Check(IslandFeatureIds.ParseScreen("bluetooth+power").Count == 0, "bluetooth+power se queda sin pantalla");
Check(IslandFeatureIds.ParseScreen("media+bluetooth").SequenceEqual(["media"]), "media+bluetooth conserva media");
Check(IslandFeatureIds.ParseScreen("media+shelf+timer+apps").SequenceEqual(["media", "shelf", "timer", "apps"]),
    "una pantalla de contenido se parsea entera, en orden");
Check(IslandFeatureIds.FormatScreen(["media", "power", "timer"]) == "media+timer", "FormatScreen descarta los avisos");
Check(IslandFeatureIds.ParseScreen("media+media+desconocida").SequenceEqual(["media"]), "ids repetidos y desconocidos fuera");

// Las de fábrica son válidas y sin avisos: envolver cada cadena tal cual (el bug del
// fallback) dejaba pantallas sin ninguna funcionalidad conocida.
foreach (string screen in IslandFeatureIds.DefaultScreens)
{
    var ids = IslandFeatureIds.ParseScreen(screen);
    Check(ids.Count > 0 && ids.All(IslandFeatureIds.IsScreenable), $"la pantalla de fábrica «{screen}» es usable");
}
Check(IslandFeatureIds.DefaultScreens.All(s => s.IndexOf('+') >= 0), "las de fábrica son pantallas, no ids sueltos");

Console.WriteLine("selfcheck: todo correcto");
