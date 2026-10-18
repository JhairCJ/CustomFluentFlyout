using System.ComponentModel;
using System.IO;
using System.Xml.Serialization;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Models;

namespace FluentFlyoutWPF.ViewModels;

public partial class UserSettings
{
    private static readonly SemaphoreSlim _shelfOperation = new(1, 1);
    private static CancellationTokenSource? _shelfCancellation;
    private bool _shelfBatch;
    private string _islandShelfStatus = "";

    [XmlIgnore]
    public string IslandShelfStatus
    {
        get => _islandShelfStatus;
        private set => SetProperty(ref _islandShelfStatus, value);
    }

    [XmlIgnore]
    public bool IslandShelfBusy => _shelfOperation.CurrentCount == 0;

    [XmlIgnore]
    public bool IslandShelfCanCancel => _shelfCancellation != null;

    private void NotifyIslandShelfBusy()
    {
        OnPropertyChanged(nameof(IslandShelfBusy));
        OnPropertyChanged(nameof(IslandShelfCanCancel));
        if (!ReferenceEquals(this, SettingsManager.Current))
        {
            SettingsManager.Current.OnPropertyChanged(nameof(IslandShelfBusy));
            SettingsManager.Current.OnPropertyChanged(nameof(IslandShelfCanCancel));
        }
    }

    public void CancelIslandShelfOperation() => _shelfCancellation?.Cancel();

    public Task<int> AddIslandShelfPathsAsync(IEnumerable<string> paths) =>
        RunShelfBatchAsync(paths.Select(path => new IslandShelfItem { Path = path, Origin = path }).ToArray(), false);

    public async Task<bool> RemoveIslandShelfItemAsync(IslandShelfItem item) =>
        await RunShelfBatchAsync([item], true) == 1;

    public Task<int> ClearIslandShelfAsync() => RunShelfBatchAsync(IslandShelfItems.ToArray(), true);

    private async Task<int> RunShelfBatchAsync(IReadOnlyList<IslandShelfItem> items, bool returning)
    {
        if (!await _shelfOperation.WaitAsync(0)) return 0;
        int completed = 0;
        var errors = new List<string>();
        _shelfCancellation = new CancellationTokenSource();
        _shelfBatch = true;
        NotifyIslandShelfBusy();
        try
        {
            foreach (var item in items)
            {
                if (_shelfCancellation.IsCancellationRequested) break;
                if (!returning && IslandShelfItems.Count >= IslandShelfItem.MaxItems)
                {
                    errors.Add(IslandStrings.Format("IslandShelfMax", "Max {0} items on the shelf.", IslandShelfItem.MaxItems));
                    break;
                }
                if (!returning && IslandShelfItems.Any(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase))) continue;
                IslandShelfStatus = IslandStrings.Format("IslandShelfMoving", "Moving {0}…", item.Name);
                string folder = returning ? Path.GetDirectoryName(item.Origin) ?? "" : IslandShelfFolder;
                var result = await Task.Run(() => IslandShelfTransfer.Move(item.Path, folder, item.Origin, returning, IslandShelfFolder));
                if (result.Item != null)
                {
                    if (returning) IslandShelfItems.Remove(item);
                    else IslandShelfItems.Add(result.Item);
                    completed++;
                }
                else errors.Add($"{item.Name}: {IslandStrings.Get(result.Error ?? "IslandShelfParkFailed", "Could not move this item. Check its location and permissions.")}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Island shelf batch failed");
            errors.Add(IslandStrings.Get("IslandShelfParkFailed", "Could not move this item. Check its location and permissions."));
        }
        finally
        {
            bool cancelled = _shelfCancellation.IsCancellationRequested;
            _shelfCancellation.Dispose();
            _shelfCancellation = null;
            _shelfBatch = false;
            NotifyIslandShelfBusy();
            IslandShelfError = string.Join(Environment.NewLine, errors);
            IslandShelfStatus = cancelled
                ? IslandStrings.Get("IslandShelfCancelled", "Stopped. Completed moves have been kept.")
                : IslandStrings.Format("IslandShelfCompleted", "{0} items moved.", completed);
            SettingsManager.SaveSettings();
            var parkedItems = IslandShelfItems.Select(item => new IslandShelfItem { Path = item.Path, Origin = item.Origin, IsFolder = item.IsFolder }).ToArray();
            try { await Task.Run(() => IslandShelfTransfer.RetirePersistedJournals(IslandShelfFolder, parkedItems)); }
            catch (Exception ex) { Logger.Debug(ex, "Shelf journal cleanup deferred"); }
            Island?.RefreshShelfContent();
            _shelfOperation.Release();
            NotifyIslandShelfBusy();
            if (!ReferenceEquals(this, SettingsManager.Current)) _ = SettingsManager.Current.RecoverIslandShelfAsync();
        }
        return completed;
    }

    public async Task RecoverIslandShelfAsync()
    {
        if (!await _shelfOperation.WaitAsync(0)) return;
        _shelfBatch = true;
        NotifyIslandShelfBusy();
        try
        {
            var pending = await Task.Run(() => IslandShelfTransfer.Pending(IslandShelfFolder));
            // Only the latest intent for a parked path describes its current ownership.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in pending)
            {
                var operation = entry.Operation;
                string parked = operation.Returning ? operation.Source : operation.Target;
                if (!seen.Add(parked)) continue;
                bool exists = operation.Returning ? entry.SourceExists : entry.TargetExists;
                var existing = IslandShelfItems.FirstOrDefault(item => string.Equals(item.Path, parked, StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    var recovered = new IslandShelfItem { Path = parked, Origin = operation.Origin, IsFolder = operation.IsFolder };
                    if (existing == null) IslandShelfItems.Add(recovered);
                    else if (existing.Origin != recovered.Origin || existing.IsFolder != recovered.IsFolder)
                        IslandShelfItems[IslandShelfItems.IndexOf(existing)] = recovered;
                }
                else if (existing != null) IslandShelfItems.Remove(existing);
                if (entry.SourceExists && entry.TargetExists)
                    IslandShelfError = IslandStrings.Get("IslandShelfRecoveryAmbiguous", "An interrupted move needs review. Both locations were preserved.");
                else if (!entry.SourceExists && !entry.TargetExists)
                    IslandShelfError = IslandStrings.Get("IslandShelfRecoveryMissing", "An interrupted move cannot be located. Review its original folder and the shelf folder.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Island shelf recovery failed");
            IslandShelfError = IslandStrings.Get("IslandShelfRecoveryFailed", "Could not finish shelf recovery. The parked files were preserved.");
        }
        finally
        {
            _shelfBatch = false;
            SettingsManager.SaveSettings();
            var parkedItems = IslandShelfItems.Select(item => new IslandShelfItem { Path = item.Path, Origin = item.Origin, IsFolder = item.IsFolder }).ToArray();
            try { await Task.Run(() => IslandShelfTransfer.RetirePersistedJournals(IslandShelfFolder, parkedItems)); }
            catch (Exception ex) { Logger.Debug(ex, "Shelf journal cleanup deferred"); }
            Island?.RefreshShelfContent();
            _shelfOperation.Release();
            NotifyIslandShelfBusy();
        }
    }
}
