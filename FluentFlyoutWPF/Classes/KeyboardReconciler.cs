namespace FluentFlyoutWPF.Classes;

/// <summary>Both delivery orders are allowed; only a confirmed unmatched physical state can recover a hook.</summary>
internal sealed class KeyboardReconciler
{
    private readonly Dictionary<int, (bool Down, long Tick)> _hook = [], _raw = [];
    internal void Hook(int key, bool down, long tick) => _hook[key] = (down, tick);
    internal void Raw(int key, bool down, long tick) => _raw[key] = (down, tick);
    internal void Clear() { _hook.Clear(); _raw.Clear(); }
    internal IReadOnlyList<(int Key, bool Down)> Missing(long now)
    {
        var missing = new List<(int, bool)>();
        foreach (var (key, state) in _raw.ToArray())
        {
            if (now - state.Tick < 250) continue;
            _raw.Remove(key);
            if (_hook.TryGetValue(key, out var hook) && hook.Down == state.Down && hook.Tick >= state.Tick - 150) continue;
            missing.Add((key, state.Down));
        }
        return missing;
    }
}
