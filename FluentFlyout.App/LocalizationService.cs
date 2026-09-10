using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;

namespace FluentFlyout.App;

/// <summary>
/// WinUI-safe localization service supporting .resw resources and English fallback.
/// </summary>
public sealed class LocalizationService
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string stringsDirectory;

    public LocalizationService(string? stringsDirectory = null)
    {
        this.stringsDirectory = stringsDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "Strings");
        CurrentCulture = "en-US";
    }

    public event EventHandler? LanguageChanged;

    public string CurrentCulture { get; private set; }

    public IReadOnlyList<string> SupportedCultures => ["en-US"];

    public void SetCulture(string culture)
    {
        var normalized = string.IsNullOrWhiteSpace(culture) ? "en-US" : culture.Trim();
        if (string.Equals(CurrentCulture, normalized, StringComparison.OrdinalIgnoreCase))
            return;

        CurrentCulture = normalized;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key, string? culture = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        var selectedCulture = culture ?? CurrentCulture;
        var resources = cache.GetOrAdd(selectedCulture, LoadCulture);
        if (resources.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            return value;

        // Fallback to en-US
        if (!string.Equals(selectedCulture, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            var enResources = cache.GetOrAdd("en-US", LoadCulture);
            if (enResources.TryGetValue(key, out var enValue) && !string.IsNullOrEmpty(enValue))
                return enValue;
        }

        return key;
    }

    private IReadOnlyDictionary<string, string> LoadCulture(string culture)
    {
        var path = Path.Combine(stringsDirectory, culture, "Resources.resw");
        if (!File.Exists(path))
        {
            // Try base directory or project relative path
            path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Strings", culture, "Resources.resw");
        }
        if (!File.Exists(path))
        {
            // Try looking in AppData / current folder
            path = Path.Combine(Environment.CurrentDirectory, "FluentFlyout.App", "Strings", culture, "Resources.resw");
        }

        if (File.Exists(path))
        {
            try
            {
                var doc = XDocument.Load(path);
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var data in doc.Descendants("data"))
                {
                    var name = data.Attribute("name")?.Value;
                    var val = data.Element("value")?.Value;
                    if (name != null && val != null)
                    {
                        dict[name] = val;
                    }
                }
                return dict;
            }
            catch
            {
                // Fallback if parsing fails
            }
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
