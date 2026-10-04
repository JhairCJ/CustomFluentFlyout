// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.Models;

namespace FluentFlyoutWPF.Classes.Dictation;

/// <summary>Pure physical-key routing. Each swallowed down has a swallowed up.</summary>
internal sealed class DictationKeyRouter
{
    internal readonly record struct Key(int VirtualKey, int ScanCode, bool Extended, bool Down);
    internal readonly record struct Result(bool Consume = false, bool Completed = false,
        bool ForeignChord = false, bool Cancel = false, IReadOnlyList<Key>? Replay = null);

    private readonly HashSet<int> _physicalDown = [];
    private readonly Dictionary<int, Key> _captured = [];
    private bool _modifierGesture;
    private bool _bypassUntilRelease;
    private bool _escapeCaptured;

    public bool IsDown(int normalizedKey)
    {
        foreach (int vk in _physicalDown)
            if (DictationHotkey.Normalize(vk) == normalizedKey) return true;
        return false;
    }

    public bool InputHeld => _captured.Count > 0 || _physicalDown.Any(IsModifier);

    public void Reset()
    {
        _physicalDown.Clear();
        _captured.Clear();
        _modifierGesture = false;
        _bypassUntilRelease = false;
        _escapeCaptured = false;
    }

    public static bool IsModifier(int vk) => DictationHotkey.Normalize(vk) is
        DictationHotkey.VkCtrl or DictationHotkey.VkShift or DictationHotkey.VkAlt or DictationHotkey.VkWin;

    public Result Route(Key key, IReadOnlyList<int> hotkey, bool enabled, bool cancelOnEscape)
    {
        bool wasComplete = hotkey.Count > 0 && hotkey.All(IsDown);
        bool repeat = key.Down && !_physicalDown.Add(key.VirtualKey);
        if (!key.Down) _physicalDown.Remove(key.VirtualKey);
        if (_physicalDown.Count == 0) _bypassUntilRelease = false;
        int normalized = DictationHotkey.Normalize(key.VirtualKey);

        if (normalized == DictationHotkey.VkEscape)
        {
            if (key.Down && cancelOnEscape)
            {
                bool cancel = !_escapeCaptured;
                _escapeCaptured = true;
                return new(Consume: true, Cancel: cancel);
            }
            if (_escapeCaptured)
            {
                if (!key.Down) _escapeCaptured = false;
                return new(Consume: true);
            }
        }

        // Drain captured keys even if settings changed or dictation was disabled.
        // Prefix modifiers already delivered to the target keep their normal ups.
        if (!key.Down) return new(Consume: _captured.Remove(key.VirtualKey));
        if (repeat) return new(Consume: _captured.ContainsKey(key.VirtualKey));

        // Ctrl+Shift can be dictation OR the beginning of Ctrl+Shift+C. Only the
        // completing key was held back, so replay it ahead of C and then pass the
        // remaining physical releases through. Earlier prefix modifiers are untouched.
        if (_modifierGesture && _captured.Count > 0 && !hotkey.Contains(normalized))
        {
            Key[] replay = [.. _captured.Values, key];
            _captured.Clear();
            _bypassUntilRelease = true;
            return new(Consume: true, ForeignChord: true, Replay: replay);
        }

        if (!enabled || _bypassUntilRelease || wasComplete || hotkey.Count == 0
            || !hotkey.Contains(normalized) || !hotkey.All(IsDown)
            || _physicalDown.Any(vk => !hotkey.Contains(DictationHotkey.Normalize(vk)))) return default;

        _modifierGesture = hotkey.All(IsModifier);
        _captured[key.VirtualKey] = key;
        return new(Consume: true, Completed: true);
    }
}
