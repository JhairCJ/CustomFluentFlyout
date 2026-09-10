using System.Xml;
using System.Xml.Serialization;
using FluentFlyout.Core;

namespace FluentFlyout.Platform.Windows;

/// <summary>Persists the Core settings model in the legacy AppData XML location.</summary>
public sealed class XmlSettingsStore : ISettingsStore
{
    private static readonly XmlSerializer Serializer = new(typeof(UserSettings));
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private UserSettings current = new();

    public XmlSettingsStore(string? applicationDataPath = null)
    {
        string appData = applicationDataPath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        SettingsFilePath = Path.Combine(appData, "FluentFlyout", "settings.xml");
        BackupFilePath = SettingsFilePath + ".bak";
        TemporaryFilePath = SettingsFilePath + ".tmp";
    }

    public string SettingsFilePath { get; }

    public string BackupFilePath { get; }

    public string TemporaryFilePath { get; }

    public UserSettings Current => current;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        UserSettings? settings = await TryLoadAsync(SettingsFilePath, cancellationToken).ConfigureAwait(false)
            ?? await TryLoadAsync(BackupFilePath, cancellationToken).ConfigureAwait(false);

        current = Normalize(settings ?? new UserSettings());
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) => SaveAsync(current, cancellationToken);

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAtomicallyAsync(settings, SettingsFilePath, BackupFilePath, TemporaryFilePath, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        UserSettings? settings = await TryLoadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (settings is null)
            throw new InvalidDataException("The selected settings file does not contain valid FluentFlyout settings.");

        current = Normalize(settings);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserSettings> ImportAndApplyAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await ImportAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return current;
    }

    public Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        ExportAsync(current, destinationPath, cancellationToken);

    public Task ExportAsync(UserSettings settings, string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return SaveAtomicallyAsync(settings, destinationPath, destinationPath + ".bak", destinationPath + ".tmp", cancellationToken);
    }

    public Task ExportAsync(string destinationPath, UserSettings settings, CancellationToken cancellationToken = default) =>
        ExportAsync(settings, destinationPath, cancellationToken);

    private static async Task<UserSettings?> TryLoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit });
            cancellationToken.ThrowIfCancellationRequested();
            return Serializer.Deserialize(reader) as UserSettings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or XmlException)
        {
            return null;
        }
    }

    private static async Task SaveAtomicallyAsync(
        UserSettings settings,
        string destinationPath,
        string backupPath,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("The destination must include a directory.", nameof(destinationPath));

        Directory.CreateDirectory(directory);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings { Async = true, Indent = true }))
            {
                Serializer.Serialize(writer, settings);
                await writer.FlushAsync().ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destinationPath))
            {
                try
                {
                    File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(destinationPath, backupPath, overwrite: true);
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static UserSettings Normalize(UserSettings settings)
    {
        settings.AllowedApps ??= [];
        settings.BlockedApps ??= [];
        if (settings.SchemaVersion <= 0)
            settings.SchemaVersion = UserSettings.CurrentSchemaVersion;

        return settings;
    }
}
