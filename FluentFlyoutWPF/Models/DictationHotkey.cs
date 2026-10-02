// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FluentFlyoutWPF.Models;

/// <summary>
/// Dictation hotkey (spec 006): one key OR a combination, saved as text
/// ("Ctrl", "Ctrl+Shift+M") and decided here as PURE logic - parsing, formatting and the
/// three rules the keyboard hook uses - so it can be checked without opening the
/// window or simulating keys (it is compiled into <c>.selfcheck</c>).
///
/// <para>Rules:</para>
/// <list type="bullet">
/// <item><b>Triggers</b> when the key going down is part of the hotkey and all of the
/// hotkey's keys are already held: the order in which the combination is pressed does not
/// matter.</item>
/// <item><b>Ends</b> when any key of the hotkey is released: releasing Ctrl in
/// "Ctrl+Shift+M" ends dictation exactly like releasing M does.</item>
/// <item><b>Cancels</b> only on Escape: a key outside the hotkey pressed while
/// recording is IGNORED - holding the hotkey and brushing another key must not take the
/// phrase being dictated down with it. Escape IS a decision: it drops the audio on
/// purpose.</item>
/// </list>
/// </summary>
public static class DictationHotkey
{
    /// <summary>Default hotkey: plain Ctrl (the easiest key to hold without typing).</summary>
    public const string Default = "Ctrl";

    public const int VkShift = 0x10;
    public const int VkCtrl = 0x11;
    public const int VkAlt = 0x12;
    public const int VkWin = 0x5B;
    /// <summary>VK_ESCAPE: the only key that aborts dictation on purpose.</summary>
    public const int VkEscape = 0x1B;

    /// <summary>
    /// Unifies the two ways Windows names a modifier: the low-level keyboard hook and WPF
    /// deliver the physical side (VK_LCONTROL 0xA2, VK_RALT 0xA5...) while the saved
    /// hotkey says "Ctrl". Without this a Ctrl hotkey would NEVER trigger, because the key
    /// arriving from the hook is not the one written in the settings.
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
    /// Reads a saved hotkey and returns its key codes, without duplicates and with the
    /// modifiers first. An old or empty setting breaks nothing: without codes the
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

    /// <summary>Writes a hotkey to save and show it ("Ctrl+Shift+M").</summary>
    public static string Format(IEnumerable<int> virtualKeys) =>
        string.Join('+', Sort(virtualKeys).Select(Name));

    /// <summary>Does the saved text form a usable hotkey (at least one key)?</summary>
    public static bool IsValid(string? text) => Parse(text).Count > 0;

    /// <summary>
    /// Does this key, going down, COMPLETE the hotkey? Only then does recording start:
    /// pressing a combination modifier early starts nothing.
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

    /// <summary>Does releasing this key end dictation? (any key of the hotkey)</summary>
    public static bool Ends(IReadOnlyList<int> hotkey, int vk) => hotkey.Contains(vk);

    /// <summary>
    /// Does pressing this key while recording cancel dictation? Only Escape. Any other
    /// key outside the hotkey is silently ignored (RF-4 MODIFIED): holding the hotkey and
    /// brushing another key must not discard what is being dictated, and the hotkey in
    /// front keeps working the same (Ctrl+C still copies, it never turns into text).
    /// </summary>
    public static bool Cancels(int vk) => vk == VkEscape;

    /// <summary>Display name of a key code (the one saved in the settings).</summary>
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

    /// <summary>Is the name typed by the user a known key?</summary>
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

    /// <summary>Modifiers first (Ctrl, Shift, Alt, Win) and the rest in their press order.</summary>
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
