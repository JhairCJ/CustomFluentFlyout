using Microsoft.UI.Xaml;

namespace FluentFlyout.App;

public sealed class WindowRegistry
{
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Window> All => _windows;

    public void Register(string key, Window window)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(window);
        _windows[key] = window;
        window.Closed += (_, _) =>
        {
            if (_windows.TryGetValue(key, out var registered) && ReferenceEquals(registered, window))
                _windows.Remove(key);
        };
    }

    public bool TryGet(string key, out Window? window) => _windows.TryGetValue(key, out window);

    public void Remove(string key) => _windows.Remove(key);
}
