namespace FluentFlyout.Core;

public sealed record SearchIndexEntry(
    string Id,
    string Title,
    IReadOnlyList<string> Terms);
