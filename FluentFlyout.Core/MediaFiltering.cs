namespace FluentFlyout.Core;

public static class MediaFiltering
{
    public static string NormalizeApplicationName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.Trim();
    }

    public static bool IsSessionAllowed(UserSettings settings, MediaSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (snapshot is null)
        {
            return false;
        }

        if (!settings.AppFilteringEnabled)
        {
            return true;
        }

        var tokens = settings.AppFilteringMode == 0
            ? settings.BlockedApps
            : settings.AllowedApps;

        var matches = tokens?.Any(token => MatchesSnapshot(snapshot, token)) == true;
        return settings.AppFilteringMode == 0 ? !matches : matches;
    }

    public static IReadOnlyList<string> BuildSearchTerms(MediaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTerm(terms, snapshot.SessionId);
        AddTerm(terms, snapshot.AppId);
        AddTerm(terms, snapshot.DisplayName);
        AddTerm(terms, snapshot.Title);
        AddTerm(terms, snapshot.Artist);
        return terms.ToArray();
    }

    private static bool MatchesSnapshot(MediaSnapshot snapshot, string? token)
    {
        var normalizedToken = NormalizeApplicationName(token ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return false;
        }

        return Contains(snapshot.SessionId, normalizedToken)
            || Contains(snapshot.AppId, normalizedToken)
            || Contains(snapshot.DisplayName, normalizedToken)
            || Contains(snapshot.Title, normalizedToken)
            || Contains(snapshot.Artist, normalizedToken);
    }

    private static bool Contains(string? source, string token)
    {
        return !string.IsNullOrWhiteSpace(source)
            && source.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddTerm(HashSet<string> terms, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        terms.Add(NormalizeApplicationName(value));
    }
}
