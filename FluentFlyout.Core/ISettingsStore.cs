namespace FluentFlyout.Core;

public interface ISettingsStore
{
    UserSettings Current { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CancellationToken cancellationToken = default);

    Task ExportAsync(string path, CancellationToken cancellationToken = default);

    Task ImportAsync(string path, CancellationToken cancellationToken = default);
}
