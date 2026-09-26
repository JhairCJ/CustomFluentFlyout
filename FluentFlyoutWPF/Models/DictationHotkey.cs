// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Models;

/// <summary>
/// Atajo del dictado (spec 006): una tecla O una combinación, guardada como texto
/// («Ctrl», «Ctrl+Shift+M») y decidida aquí como lógica PURA —parseo, formato y las
/// tres reglas que usa el gancho de teclado—, para poder comprobarla sin abrir la
/// ventana ni simular teclas (va compilada en <c>.selfcheck</c>).
///
/// <para>Reglas:</para>
/// <list type="bullet">
/// <item><b>Dispara</b> cuando la tecla que baja es parte del atajo y todas las del
/// atajo están pulsadas: da igual el orden en que se pulse la combinación.</item>
/// <item><b>Termina</b> cuando se suelta cualquier tecla del atajo: soltar Ctrl en
/// «Ctrl+Shift+M» cierra el dictado igual que soltar la M.</item>
/// <item><b>Cancela</b> solo con Escape: una tecla ajena al atajo que se pulse mientras se
/// graba se IGNORA —mantener el atajo y rozar otra tecla no puede llevarse por delante la
/// frase que se está dictando—. Escape sí es una decisión: descarta el audio a propósito.</item>
/// </list>
/// </summary>
public static class DictationHotkey
{
    /// <summary>Atajo por defecto: Ctrl a secas (la tecla más cómoda de mantener sin escribir).</summary>
    public const string Default = "Ctrl";

    public const int VkShift = 0x10;
    public const int VkCtrl = 0x11;
    public const int VkAlt = 0x12;
    public const int VkWin = 0x5B;
    /// <summary>VK_ESCAPE: la única tecla que aborta el dictado a propósito.</summary>
    public const int VkEscape = 0x1B;

    /// <summary>¿Es una tecla modificadora (puede formar atajo por sí sola)?</summary>
    public static bool IsModifier(int vk) => vk is VkShift or VkCtrl or VkAlt or VkWin;

    /// <summary>
    /// Une las dos formas en que Windows nombra a un modificador: el gancho de teclado
    /// de bajo nivel y WPF entregan el lado físico (VK_LCONTROL 0xA2, VK_RALT 0xA5…) y el
    /// atajo guardado dice «Ctrl». Sin esto, un atajo de Ctrl no dispararía NUNCA, porque
    /// la tecla que llega del gancho no es la que está escrita en los ajustes.
    /// </summary>
    public static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => VkShift, // VK_LSHIFT / VK_RSHIFT
        0xA2 or 0xA3 => VkCtrl,  // VK_LCONTROL / VK_RCONTROL
        0xA4 or 0xA5 => VkAlt,   // VK_LMENU / VK_RMENU
        0x5C => VkWin,           // VK_RWIN (VK_LWIN ya es genérico)
        _ => vk,
    };

    /// <summary>
    /// Lee un atajo guardado y devuelve sus códigos de tecla, sin repetidos y con los
    /// modificadores primero. Un ajuste viejo o vacío no rompe nada: sin códigos, el
    /// dictado simplemente no dispara.
    /// </summary>
    public static IReadOnlyList<int> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var keys = new List<int>();
        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryNameToVirtualKey(raw, out int vk) || keys.Contains(vk)) continue;
            keys.Add(vk);
        }
        return Sort(keys);
    }

    /// <summary>Escribe un atajo para guardarlo y enseñarlo («Ctrl+Shift+M»).</summary>
    public static string Format(IEnumerable<int> virtualKeys) =>
        string.Join('+', Sort(virtualKeys).Select(Name));

    /// <summary>¿El texto guardado forma un atajo usable (al menos una tecla)?</summary>
    public static bool IsValid(string? text) => Parse(text).Count > 0;

    /// <summary>
    /// ¿Esta tecla, al bajar, COMPLETA el atajo? Solo entonces empieza a grabarse: pulsar
    /// un modificador de la combinación antes de tiempo no arranca nada.
    /// </summary>
    public static bool Triggers(IReadOnlyList<int> hotkey, int vk, IReadOnlyCollection<int> pressed)
    {
        if (hotkey.Count == 0 || !hotkey.Contains(vk)) return false;
        foreach (int key in hotkey)
        {
            if (!pressed.Contains(key)) return false;
        }
        return true;
    }

    /// <summary>¿Soltar esta tecla cierra el dictado? (cualquier tecla del atajo)</summary>
    public static bool Ends(IReadOnlyList<int> hotkey, int vk) => hotkey.Contains(vk);

    /// <summary>
    /// ¿Bajar esta tecla mientras se graba cancela el dictado? Solo Escape. Cualquier otra
    /// tecla ajena al atajo se ignora en silencio (RF-4 MODIFIED): mantener el atajo y rozar
    /// otra tecla no puede descartar lo que se está dictando, y el atajo de delante sigue
    /// funcionando igual (un Ctrl+C sigue copiando, nunca se convierte en texto).
    /// </summary>
    public static bool Cancels(int vk) => vk == VkEscape;

    /// <summary>Nombre visible de un código de tecla (el que se guarda en los ajustes).</summary>
    public static string Name(int vk) => vk switch
    {
        VkCtrl => "Ctrl",
        VkShift => "Shift",
        VkAlt => "Alt",
        VkWin => "Win",
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x14 => "CapsLock",
        0x6A => "Num*",
        0x6B => "Num+",
        0x6D => "Num-",
        0x6E => "Num.",
        0x6F => "Num/",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(), // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(), // A-Z
        >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),   // teclado numérico
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),     // F1-F24
        _ => $"0x{vk:X2}",
    };

    /// <summary>¿El nombre escrito por el usuario es una tecla conocida?</summary>
    public static bool TryNameToVirtualKey(string? name, out int vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string key = name.Trim();
        switch (key.ToLowerInvariant())
        {
            case "ctrl" or "control" or "lctrl" or "rctrl": vk = VkCtrl; return true;
            case "shift" or "lshift" or "rshift": vk = VkShift; return true;
            case "alt" or "lalt" or "ralt": vk = VkAlt; return true;
            case "win" or "windows" or "meta" or "super": vk = VkWin; return true;
            case "space": vk = 0x20; return true;
            case "enter" or "return": vk = 0x0D; return true;
            case "tab": vk = 0x09; return true;
            case "esc" or "escape": vk = 0x1B; return true;
            case "backspace": vk = 0x08; return true;
            case "delete" or "del": vk = 0x2E; return true;
            case "insert" or "ins": vk = 0x2D; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "pageup" or "pgup": vk = 0x21; return true;
            case "pagedown" or "pgdn": vk = 0x22; return true;
            case "left" or "arrowleft": vk = 0x25; return true;
            case "up" or "arrowup": vk = 0x26; return true;
            case "right" or "arrowright": vk = 0x27; return true;
            case "down" or "arrowdown": vk = 0x28; return true;
        }
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
        }
        if (key.Length is 2 or 3 && (key[0] is 'f' or 'F') && int.TryParse(key[1..], out int fn)
            && fn is >= 1 and <= 24)
        {
            vk = 0x6F + fn;
            return true;
        }
        if (key.StartsWith("num", StringComparison.OrdinalIgnoreCase) && key.Length == 4
            && char.IsDigit(key[3]))
        {
            vk = 0x60 + (key[3] - '0');
            return true;
        }
        return false;
    }

    /// <summary>Modificadores primero (Ctrl, Shift, Alt, Win) y lo demás en su orden de pulsación.</summary>
    private static List<int> Sort(IEnumerable<int> virtualKeys)
    {
        var modifiers = new List<int>();
        var rest = new List<int>();
        foreach (int vk in virtualKeys)
        {
            if (vk == VkCtrl) { if (!modifiers.Contains(VkCtrl)) modifiers.Add(VkCtrl); }
            else if (vk == VkShift) { if (!modifiers.Contains(VkShift)) modifiers.Add(VkShift); }
            else if (vk == VkAlt) { if (!modifiers.Contains(VkAlt)) modifiers.Add(VkAlt); }
            else if (vk == VkWin) { if (!modifiers.Contains(VkWin)) modifiers.Add(VkWin); }
            else if (!rest.Contains(vk)) rest.Add(vk);
        }
        var ordered = new List<int>();
        foreach (int modifier in (int[])[VkCtrl, VkShift, VkAlt, VkWin])
        {
            if (modifiers.Contains(modifier)) ordered.Add(modifier);
        }
        ordered.AddRange(rest);
        return ordered;
    }
}
