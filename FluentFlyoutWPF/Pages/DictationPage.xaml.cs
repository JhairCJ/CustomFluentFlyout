// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes;
using FluentFlyoutWPF.Classes.Dictation;
using FluentFlyoutWPF.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FluentFlyoutWPF.Pages;

/// <summary>
/// Voice dictation settings (spec 006): turning it on, its hotkey - one key or a
/// combination -, the language and the local models.
///
/// <para>Models are downloaded once; dictation then runs on the machine, with no
/// network (RF-8). The list mixes the downloadable catalog with local files, so a
/// hand-added model (or one copied from another machine) shows up the same way.</para>
/// </summary>
public partial class DictationPage : Page
{
    private const string CudaDownloadsUrl = "https://www.nvidia.com/drivers/";
    private const string NemoRuntimeGuideUrl = "https://github.com/NVIDIA/NeMo-Speech.cpp#installation";
    private const string NemoRuntimeInstallCommand = "irm https://github.com/NVIDIA/NeMo-Speech.cpp/raw/main/scripts/install.ps1 | iex";

    private readonly ObservableCollection<DictationModelRow> _rows = [];
    private bool _loading;
    private bool _runtimeOperation;
    private CancellationTokenSource? _whisperDownloadCancellation;

    public DictationPage()
    {
        InitializeComponent();
        WhisperCudaRuntime.CompletePendingRemoval();
        DataContext = SettingsManager.Current;
        ModelList.ItemsSource = _rows;

        _loading = true;
        LanguageCombo.SelectedIndex = SettingsManager.Current.DictationLanguage switch
        {
            "es" => 1,
            "en" => 2,
            _ => 0,
        };
        _loading = false;

        Loaded += DictationPage_Loaded;
        Unloaded += DictationPage_Unloaded;
        RefreshModels();
        UpdateCrispAsrRuntimeStatus();
    }

    private void DictationPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.Dictation.Changed += Dictation_Changed;
        DictationModelStore.DownloadStateChanged += DictationDownloadStateChanged;
        SettingsManager.Current.PropertyChanged += Settings_PropertyChanged;
        FluentFlyout.Classes.LocalizationManager.LanguageChanged += RefreshLocalizedState;
        UpdateGpuRuntimeStatus();
        UpdateCrispAsrRuntimeStatus();
    }

    private void DictationPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // The page (or the settings window) is closed with the box maybe still focused: dictation
        // has to start listening again no matter what.
        EndHotkeyCapture();
        if (Application.Current.MainWindow is MainWindow mainWindow)
            mainWindow.Dictation.Changed -= Dictation_Changed;
        DictationModelStore.DownloadStateChanged -= DictationDownloadStateChanged;
        SettingsManager.Current.PropertyChanged -= Settings_PropertyChanged;
        FluentFlyout.Classes.LocalizationManager.LanguageChanged -= RefreshLocalizedState;
    }

    private void RefreshLocalizedState()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RefreshLocalizedState);
            return;
        }
        RefreshModels();
        UpdateCrispAsrRuntimeStatus();
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.UserSettings.DictationModel)
            or nameof(ViewModels.UserSettings.DictationModelDevices)
            or nameof(ViewModels.UserSettings.DictationUseGpu))
        {
            if (Dispatcher.CheckAccess()) RefreshModels();
            else _ = Dispatcher.BeginInvoke(RefreshModels);
        }
    }

    private void DictationDownloadStateChanged(string fileName)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => DictationDownloadStateChanged(fileName));
            return;
        }

        RefreshModels();
    }

    private void Dictation_Changed()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(Dictation_Changed);
            return;
        }

        UpdateGpuRuntimeStatus();
        UpdateRuntimeControls();
    }

    private void UpdateGpuRuntimeStatus()
    {
        var service = Dictation;
        foreach (var row in _rows)
        {
            row.DeviceText = DeviceLabel(SettingsManager.Current.GetDictationDevice(row.FileName), row.FileName);
            if (row.Active && service?.AccelerationRestartRequired == true)
                row.DeviceText += " · " + IslandStrings.Get("DictationDeviceRestartShort", "Restart to apply");
        }
        CudaDriverButton.Visibility = Visibility.Collapsed;
        if (service == null || string.IsNullOrWhiteSpace(SettingsManager.Current.DictationModel))
        {
            GpuRuntimeStatus.Text = "";
            return;
        }

        string selected = DeviceLabel(service.RequestedDevice, SettingsManager.Current.DictationModel, service.RequestedCudaVersion);
        bool usesWhisper = (DictationModelStore.Find(DictationDevices.ModelKey(SettingsManager.Current.DictationModel))?.Backend
            ?? DictationModelBackend.Whisper) == DictationModelBackend.Whisper;
        if (usesWhisper && service.RequestedDevice == DictationDevice.DedicatedGpu && !WhisperCudaRuntime.IsInstalled)
        {
            GpuRuntimeStatus.Text = IslandStrings.Get("DictationWhisperCudaMissing", "Download Whisper NVIDIA CUDA from Runtimes to use the dedicated GPU.");
        }
        else if (service.AccelerationRestartRequired)
        {
            GpuRuntimeStatus.Text = IslandStrings.Get("DictationDeviceRestartRequired",
                "CPU runtime already loaded. Restart the app to enable NVIDIA CUDA for this model.");
            CudaDriverButton.Visibility = Visibility.Collapsed;
        }
        else if (service.ModelLoaded)
        {
            string actual = DeviceLabel(service.LoadedDevice, SettingsManager.Current.DictationModel, service.LoadedCudaVersion);
            if (!string.IsNullOrWhiteSpace(service.LoadedDeviceName)) actual += $" · {service.LoadedDeviceName}";
            GpuRuntimeStatus.Text = IslandStrings.Format("DictationDeviceLoaded",
                "Selected: {0}. Model loaded: {1}.", selected, actual);
        }
        else
        {
            GpuRuntimeStatus.Text = IslandStrings.Format("DictationDeviceNotLoaded",
                "Selected: {0}. Model is currently released.", selected);
        }
    }

    private static string DeviceLabel(DictationDevice device, string model, DictationCudaVersion? cudaVersion = null) => device switch
    {
        DictationDevice.IntegratedGpu => IslandStrings.Get("DictationDeviceIntegrated", "Integrated GPU (Vulkan)"),
        DictationDevice.DedicatedGpu when DictationModelStore.Find(DictationDevices.ModelKey(model))?.Backend == DictationModelBackend.CrispAsr =>
            IslandStrings.Format("DictationDeviceDedicatedVersion", "Dedicated NVIDIA GPU (CUDA {0})",
                (cudaVersion ?? SettingsManager.Current.GetDictationCudaVersion(model)) == DictationCudaVersion.Cuda13 ? 13 : 12),
        DictationDevice.DedicatedGpu => IslandStrings.Get("DictationDeviceDedicated", "Dedicated NVIDIA GPU (CUDA)"),
        _ => IslandStrings.Get("DictationDeviceCpu", "Processor (CPU)"),
    };

    private async void ModelSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not DictationModelRow { Active: true } row) return;
        var selected = SettingsManager.Current.GetDictationDevice(row.FileName);
        bool usesCrisp = DictationModelStore.Find(row.FileName)?.Backend == DictationModelBackend.CrispAsr;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem
        {
            Header = IslandStrings.Get("DictationDeviceMenuTitle", "Run this model on"),
            IsEnabled = false,
        });
        menu.Items.Add(new Separator());
        MenuItem AddDevice(DictationDevice device, DictationCudaVersion cudaVersion = DictationCudaVersion.Cuda12)
        {
            var item = new MenuItem
            {
                Header = DeviceLabel(device, row.FileName, cudaVersion), IsCheckable = true,
                IsChecked = device == selected && (!usesCrisp || device != DictationDevice.DedicatedGpu
                    || SettingsManager.Current.GetDictationCudaVersion(row.FileName) == cudaVersion),
            };
            item.Click += (_, _) =>
            {
                SettingsManager.Current.SetDictationDevice(row.FileName, device, device == DictationDevice.DedicatedGpu ? cudaVersion : null);
                ModelsStatus.Text = "";
            };
            menu.Items.Add(item);
            return item;
        }
        AddDevice(DictationDevice.Cpu);
        var gpuItems = new List<(DictationDevice Device, DictationCudaVersion Version, MenuItem Item)>();
        if (usesCrisp) gpuItems.Add((DictationDevice.IntegratedGpu, DictationCudaVersion.Cuda12, AddDevice(DictationDevice.IntegratedGpu)));
        gpuItems.Add((DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda12, AddDevice(DictationDevice.DedicatedGpu)));
        if (usesCrisp)
        {
            gpuItems.Add((DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13,
                AddDevice(DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13)));
            foreach (var (_, _, item) in gpuItems) item.IsEnabled = false;
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;

        if (!usesCrisp) return;
        foreach (var (device, cudaVersion, item) in gpuItems)
        {
            try
            {
                var adapter = await CrispAsrRuntime.GetDeviceAsync(device, cudaVersion: cudaVersion);
                item.IsEnabled = adapter != null;
                item.Header = adapter != null ? $"{DeviceLabel(device, row.FileName, cudaVersion)} · {adapter.Name}"
                    : device == DictationDevice.IntegratedGpu
                        ? IslandStrings.Get("DictationDeviceIntegratedUnavailable", "Integrated GPU unavailable (check the Vulkan runtime and driver)")
                        : IslandStrings.Format("DictationDeviceDedicatedVersionUnavailable", "NVIDIA GPU unavailable (download CUDA {0} and check your driver)", cudaVersion == DictationCudaVersion.Cuda13 ? 13 : 12);
            }
            catch (Exception)
            {
                item.Header = IslandStrings.Get("DictationDeviceDetectionFailed", "Could not detect GPUs. Reopen this menu to retry.");
            }
        }
    }

    private void DownloadCudaDrivers_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = CudaDownloadsUrl,
            UseShellExecute = true,
        });
    }

    private void InstallNemoRuntime_Click(object sender, RoutedEventArgs e) =>
        PrepareExternalRuntimeInstall(NemoRuntimeGuideUrl, NemoRuntimeInstallCommand);

    private async void DownloadWhisperCudaRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (_runtimeOperation || CrispAsrRuntime.IsInstalling || WhisperCudaRuntime.IsInstalling) return;
        using var cancellation = new CancellationTokenSource();
        _whisperDownloadCancellation = cancellation;
        try
        {
            _runtimeOperation = true;
            UpdateRuntimeControls();
            RuntimesStatus.Text = "";
            WhisperCudaRuntimeButton.Visibility = Visibility.Collapsed;
            WhisperCudaRuntimeCancelButton.Visibility = Visibility.Visible;
            WhisperCudaRuntimeProgress.Value = 0;
            WhisperCudaRuntimeProgress.Visibility = Visibility.Visible;
            var progress = new Progress<double>(value =>
            {
                if (_whisperDownloadCancellation != cancellation) return;
                WhisperCudaRuntimeProgress.Value = value * 100;
                WhisperCudaRuntimeStatus.Text = IslandStrings.Format("DictationRuntimeCrispAsrDownloadingPercent", "Downloading… {0}%", (int)(value * 100));
            });
            await WhisperCudaRuntime.InstallAsync(progress, cancellation.Token);
            RuntimesStatus.Text = IslandStrings.Get("DictationWhisperCudaReady", "Whisper CUDA installed. Restart the app before using the NVIDIA GPU.");
        }
        catch (OperationCanceledException)
        {
            RuntimesStatus.Text = IslandStrings.Get("DictationRuntimeDownloadCancelled", "Download cancelled. You can retry.");
        }
        catch (Exception ex)
        {
            RuntimesStatus.Text = IslandStrings.Format("DictationRuntimeCrispAsrFailed", "Could not install runtime: {0}", ex.Message);
        }
        finally
        {
            _whisperDownloadCancellation = null;
            _runtimeOperation = false;
            WhisperCudaRuntimeCancelButton.Visibility = Visibility.Collapsed;
            WhisperCudaRuntimeProgress.Visibility = Visibility.Collapsed;
            UpdateCrispAsrRuntimeStatus();
            UpdateGpuRuntimeStatus();
        }
    }

    private void CancelWhisperCudaRuntime_Click(object sender, RoutedEventArgs e) => _whisperDownloadCancellation?.Cancel();

    private async void UninstallWhisperCudaRuntime_Click(object sender, RoutedEventArgs e)
    {
        await RemoveRuntimeAsync(async () =>
        {
            await WhisperCudaRuntime.UninstallAsync();
            var settings = SettingsManager.Current;
            var models = DictationModelStore.Catalog.Where(model => model.Backend == DictationModelBackend.Whisper)
                .Select(model => model.FileName).Append(settings.DictationModel).Distinct();
            foreach (string model in models)
                if ((DictationModelStore.Find(DictationDevices.ModelKey(model))?.Backend ?? DictationModelBackend.Whisper) == DictationModelBackend.Whisper
                    && settings.GetDictationDevice(model) == DictationDevice.DedicatedGpu)
                    settings.SetDictationDevice(model, DictationDevice.Cpu);
        });
        if (WhisperCudaRuntime.RemovalPending)
            RuntimesStatus.Text = IslandStrings.Get("DictationWhisperCudaRemovalPending", "Restart the app to finish uninstalling.");
    }

    private async void DownloadCrispAsrRuntime_Click(object sender, RoutedEventArgs e) =>
        await DownloadCrispRuntimeAsync(DictationDevice.IntegratedGpu);

    private async void DownloadCrispAsrCudaRuntime_Click(object sender, RoutedEventArgs e) =>
        await DownloadCrispRuntimeAsync(DictationDevice.DedicatedGpu);

    private async void DownloadCrispAsrCuda13Runtime_Click(object sender, RoutedEventArgs e) =>
        await DownloadCrispRuntimeAsync(DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13);

    private async Task DownloadCrispRuntimeAsync(DictationDevice device,
        DictationCudaVersion cudaVersion = DictationCudaVersion.Cuda12)
    {
        if (_runtimeOperation || CrispAsrRuntime.IsInstalling || WhisperCudaRuntime.IsInstalling) return;
        var status = device != DictationDevice.DedicatedGpu ? CrispAsrRuntimeStatus
            : cudaVersion == DictationCudaVersion.Cuda13 ? CrispAsrCuda13RuntimeStatus : CrispAsrCudaRuntimeStatus;
        try
        {
            _runtimeOperation = true;
            UpdateRuntimeControls();
            RuntimesStatus.Text = "";
            var progress = new Progress<double>(value => status.Text = IslandStrings.Format(
                "DictationRuntimeCrispAsrDownloadingPercent", "Downloading… {0}%", (int)(value * 100)));
            await CrispAsrRuntime.InstallAsync(device, progress, cudaVersion: cudaVersion);
            RuntimesStatus.Text = IslandStrings.Get("DictationRuntimeCrispAsrReady", "Runtime installed");
        }
        catch (Exception ex)
        {
            RuntimesStatus.Text = IslandStrings.Format("DictationRuntimeCrispAsrFailed", "Could not install runtime: {0}", ex.Message);
        }
        finally
        {
            _runtimeOperation = false;
            UpdateCrispAsrRuntimeStatus();
            Dictation?.RefreshResourcePolicy();
        }
    }

    private async void UninstallCrispAsrRuntime_Click(object sender, RoutedEventArgs e) =>
        await UninstallCrispRuntimeAsync(DictationDevice.IntegratedGpu);

    private async void UninstallCrispAsrCudaRuntime_Click(object sender, RoutedEventArgs e) =>
        await UninstallCrispRuntimeAsync(DictationDevice.DedicatedGpu);

    private async void UninstallCrispAsrCuda13Runtime_Click(object sender, RoutedEventArgs e) =>
        await UninstallCrispRuntimeAsync(DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13);

    private Task UninstallCrispRuntimeAsync(DictationDevice device,
        DictationCudaVersion cudaVersion = DictationCudaVersion.Cuda12) => RemoveRuntimeAsync(async () =>
    {
        await CrispAsrRuntime.UninstallAsync(device, cudaVersion);
        ApplyRemovedRuntimePreferences(device, cudaVersion);
    });

    private async void UninstallNemoRuntime_Click(object sender, RoutedEventArgs e) =>
        await RemoveRuntimeAsync(NemoSpeechRuntime.UninstallAsync);

    private async Task RemoveRuntimeAsync(Func<Task> remove)
    {
        if (_runtimeOperation || CrispAsrRuntime.IsInstalling || WhisperCudaRuntime.IsInstalling) return;
        if (Dictation?.Active == true)
        {
            RuntimesStatus.Text = IslandStrings.Get("DictationRuntimeBusy", "Finish dictating before removing a runtime.");
            return;
        }
        try
        {
            _runtimeOperation = true;
            UpdateRuntimeControls();
            RuntimesStatus.Text = IslandStrings.Get("DictationRuntimeRemoving", "Removing runtime…");
            if (Dictation is { } service) await service.RunRuntimeMaintenanceAsync(remove);
            else await remove();
            RuntimesStatus.Text = IslandStrings.Get("DictationRuntimeRemoved", "Runtime removed. Downloaded speech models were kept.");
        }
        catch (Exception ex)
        {
            RuntimesStatus.Text = IslandStrings.Format("DictationRuntimeRemoveFailed", "Could not remove runtime: {0}", ex.Message);
        }
        finally
        {
            _runtimeOperation = false;
            UpdateCrispAsrRuntimeStatus();
            RefreshModels();
        }
    }

    private void ApplyRemovedRuntimePreferences(DictationDevice removedDevice, DictationCudaVersion removedVersion)
    {
        var settings = SettingsManager.Current;
        foreach (var model in DictationModelStore.Catalog.Where(model => model.Backend == DictationModelBackend.CrispAsr))
        {
            DictationDevice current = settings.GetDictationDevice(model.FileName);
            if (removedDevice == DictationDevice.IntegratedGpu && current == DictationDevice.IntegratedGpu)
                settings.SetDictationDevice(model.FileName, DictationDevice.Cpu);
            else if (removedDevice == DictationDevice.DedicatedGpu && current == DictationDevice.DedicatedGpu
                && settings.GetDictationCudaVersion(model.FileName) == removedVersion)
            {
                var alternative = removedVersion == DictationCudaVersion.Cuda13 ? DictationCudaVersion.Cuda12 : DictationCudaVersion.Cuda13;
                if (CrispAsrRuntime.IsInstalledFor(DictationDevice.DedicatedGpu, alternative))
                    settings.SetDictationDevice(model.FileName, DictationDevice.DedicatedGpu, alternative);
                else settings.SetDictationDevice(model.FileName, DictationDevice.Cpu);
            }
        }
    }

    private void UpdateRuntimeControls()
    {
        bool available = !_runtimeOperation && !CrispAsrRuntime.IsInstalling && !WhisperCudaRuntime.IsInstalling && Dictation?.Active != true;
        foreach (var button in new[] { NemoRuntimeButton, NemoRuntimeRemoveButton,
            CrispAsrRuntimeButton, CrispAsrRuntimeRemoveButton, CrispAsrCudaRuntimeButton,
            CrispAsrCudaRuntimeRemoveButton, CrispAsrCuda13RuntimeButton, CrispAsrCuda13RuntimeRemoveButton,
            WhisperCudaRuntimeButton, WhisperCudaRuntimeRemoveButton })
            button.IsEnabled = available;
        WhisperCudaRuntimeButton.IsEnabled &= WhisperCudaRuntime.IsSupported && !WhisperCudaRuntime.RemovalPending;
        WhisperCudaRuntimeRemoveButton.IsEnabled &= !WhisperCudaRuntime.RemovalPending;
    }

    private void UpdateCrispAsrRuntimeStatus()
    {
        void UpdateRow(Button download, Button remove, TextBlock status, bool installed, string? version = null)
        {
            download.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
            remove.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
            status.Text = installed
                ? version == null ? IslandStrings.Get("DictationRuntimeInstalled", "Installed")
                    : IslandStrings.Format("DictationRuntimeInstalledVersion", "Installed · {0}", version)
                : IslandStrings.Get("DictationRuntimeNotInstalled", "Not installed");
        }
        UpdateRow(WhisperCudaRuntimeButton, WhisperCudaRuntimeRemoveButton, WhisperCudaRuntimeStatus,
            WhisperCudaRuntime.IsInstalled, WhisperCudaRuntime.Version);
        if (WhisperCudaRuntime.RemovalPending)
            WhisperCudaRuntimeStatus.Text = IslandStrings.Get("DictationWhisperCudaRemovalPending", "Restart the app to finish uninstalling.");
        else if (!WhisperCudaRuntime.IsSupported)
            WhisperCudaRuntimeStatus.Text = IslandStrings.Get("DictationWhisperCudaUnsupported", "Available on Windows x64 with a compatible NVIDIA GPU and driver.");
        UpdateRow(NemoRuntimeButton, NemoRuntimeRemoveButton, NemoRuntimeStatus, NemoSpeechRuntime.IsInstalled);
        NemoRuntimeButton.Content = IslandStrings.Get("DictationRuntimeNemoInstall", "Installation guide");
        UpdateRow(CrispAsrRuntimeButton, CrispAsrRuntimeRemoveButton, CrispAsrRuntimeStatus,
            CrispAsrRuntime.IsInstalled, CrispAsrRuntime.Version);
        UpdateRow(CrispAsrCudaRuntimeButton, CrispAsrCudaRuntimeRemoveButton, CrispAsrCudaRuntimeStatus,
            CrispAsrRuntime.IsInstalledFor(DictationDevice.DedicatedGpu), CrispAsrRuntime.Version);
        UpdateRow(CrispAsrCuda13RuntimeButton, CrispAsrCuda13RuntimeRemoveButton, CrispAsrCuda13RuntimeStatus,
            CrispAsrRuntime.IsInstalledFor(DictationDevice.DedicatedGpu, DictationCudaVersion.Cuda13), CrispAsrRuntime.Version);
        UpdateRuntimeControls();
    }

    private void PrepareExternalRuntimeInstall(string guideUrl, string command)
    {
        try
        {
            Clipboard.SetText(command);
            RuntimesStatus.Text = IslandStrings.Get(
                "DictationRuntimeCommandCopied",
                "Command copied; paste it into PowerShell or Terminal");
        }
        catch (Exception ex)
        {
            RuntimesStatus.Text = IslandStrings.Format(
                "DictationRuntimeCopyFailed",
                "Could not copy the command: {0}",
                ex.Message);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = guideUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            RuntimesStatus.Text = IslandStrings.Format(
                "DictationRuntimeOpenGuideFailed",
                "Could not open the guide: {0}",
                ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Atajo
    // ------------------------------------------------------------------

    /// <summary>Hotkey keys the user is holding right now in the box.</summary>
    private readonly HashSet<int> _capturedKeys = [];
    private bool _hotkeyCapturing;

    /// <summary>Dictation service (it lives in the main window), to tell it about the capture.</summary>
    private static DictationService? Dictation =>
        Application.Current.MainWindow is MainWindow mainWindow ? mainWindow.Dictation : null;

    /// <summary>
    /// Hotkey capture: while the box has focus, every key going down is ACCUMULATED with
    /// the ones already held and the setting saves the whole combination ("Ctrl",
    /// "Ctrl+Shift+M"...). The combination used to depend on whatever WPF's keyboard
    /// state reported at that instant: if it did not report modifiers, every key replaced
    /// the previous one and the hotkey stayed a single key.
    /// </summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; // la tecla es del atajo, no de la navegación de la ventana
        BeginHotkeyCapture();
        int vk = DictationHotkey.Normalize(VirtualKeyOf(e));
        if (vk == 0) return;
        _capturedKeys.Add(vk);
        ApplyCapturedHotkey();
    }

    /// <summary>
    /// Releasing a key does not change the setting: it stays at the last whole combination,
    /// which is the one the user will hold to dictate. It only stops counting for whatever
    /// is pressed afterwards (Ctrl + M then N ends up as "Ctrl+N", not "Ctrl+M+N").
    /// </summary>
    private void HotkeyBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        _capturedKeys.Remove(DictationHotkey.Normalize(VirtualKeyOf(e)));
    }

    private static int VirtualKeyOf(KeyEventArgs e) =>
        KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);

    /// <summary>With the box focused the hotkey can already be defined: the whole dictation service is stopped.</summary>
    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => BeginHotkeyCapture();

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndHotkeyCapture();

    /// <summary>
    /// While the hotkey is being defined dictation does not listen: pressing Ctrl in the box
    /// must not open the microphone, start anything, or cut a dictation in progress.
    /// </summary>
    private void BeginHotkeyCapture()
    {
        if (_hotkeyCapturing) return;
        _hotkeyCapturing = true;
        _capturedKeys.Clear();
        if (Dictation is { } dictation) dictation.HotkeyCaptureActive = true;
    }

    private void EndHotkeyCapture()
    {
        if (!_hotkeyCapturing) return;
        _hotkeyCapturing = false;
        _capturedKeys.Clear();
        if (Dictation is { } dictation) dictation.HotkeyCaptureActive = false;
    }

    /// <summary>
    /// Saves the hotkey with what the user is holding RIGHT NOW (the box shows the setting,
    /// so it is visible immediately). An unreadable setting is not saved: the previous one stays.
    /// </summary>
    private void ApplyCapturedHotkey()
    {
        string text = DictationHotkey.Format(_capturedKeys);
        if (DictationHotkey.IsValid(text)) SettingsManager.Current.DictationHotkey = text;
    }

    // ------------------------------------------------------------------
    // Idioma
    // ------------------------------------------------------------------

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        SettingsManager.Current.DictationLanguage = LanguageCombo.SelectedIndex switch
        {
            1 => "es",
            2 => "en",
            _ => "auto",
        };
    }

    // ------------------------------------------------------------------
    // Modelos
    // ------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the list: catalog (downloaded or not) plus the loose files in the folder.
    /// The active model is marked "In use", and only download what is missing and delete
    /// what is present are offered.
    /// </summary>
    private void RefreshModels()
    {
        string configured = SettingsManager.Current.DictationModel;
        string? activePath = DictationModelStore.ResolveActivePath(configured);

        var rows = new List<DictationModelRow>();
        foreach (var model in DictationModelStore.Catalog)
        {
            var row = new DictationModelRow(
                FileName: model.FileName,
                Name: model.Name,
                Subtitle: ModelSubtitle(model),
                FromCatalog: true,
                Installed: DictationModelStore.IsInstalled(model.FileName),
                Active: IsActive(configured, activePath, model.FileName));
            if (DictationModelStore.IsDownloading(model.FileName))
            {
                row.Busy = true;
                row.Subtitle = IslandStrings.Get("DictationModelDownloading", "Downloading…");
            }
            rows.Add(row);
        }
        foreach (string file in DictationModelStore.InstalledFiles())
        {
            if (DictationModelStore.Find(file) != null) continue; // ya está en el catálogo
            rows.Add(new DictationModelRow(
                FileName: file,
                Name: Path.GetFileNameWithoutExtension(file),
                Subtitle: IslandStrings.Get("DictationModelAddedByHand", "Model added by hand"),
                FromCatalog: false,
                Installed: true,
                Active: IsActive(configured, activePath, file)));
        }

        _rows.Clear();
        foreach (var row in rows) _rows.Add(row);
        UpdateGpuRuntimeStatus();
    }

    /// <summary>
    /// Is it the active model? The file name counts and so does its full path: a
    /// hand-added model is saved by path, not by name.
    /// </summary>
    private static bool IsActive(string configured, string? activePath, string fileName) =>
        string.Equals(configured, fileName, StringComparison.OrdinalIgnoreCase)
        || (activePath != null
            && string.Equals(Path.GetFileName(activePath), fileName, StringComparison.OrdinalIgnoreCase));

    private static string ModelSubtitle(DictationModelInfo model)
    {
        string recommendation = model.Recommended
            ? $" · {IslandStrings.Get("DictationModelRecommended", "Recommended")}"
            : "";
        string runtime = model.Backend == DictationModelBackend.CrispAsr
            ? $" · {IslandStrings.Get("DictationModelCrispRuntime", "Requires CrispASR")}"
            : string.IsNullOrWhiteSpace(model.Runtime) ? "" : $" · {model.Runtime}";
        return $"{model.Size} · {model.Language}{runtime}{recommendation}";
    }

    private void UseModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DictationModelRow row) return;
        SettingsManager.Current.DictationModel = row.FileName;
        ModelsStatus.Text = "";
        RefreshModels();
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DictationModelRow row) return;
        if (DictationModelStore.Find(row.FileName) is not { } model) return;
        if (DictationModelStore.IsDownloading(row.FileName)) return;

        try
        {
            row.Busy = true;
            row.Subtitle = IslandStrings.Get("DictationModelDownloading", "Downloading…");
            var progress = new Progress<double>(value =>
            {
                row.Progress = value * 100;
                row.Subtitle = IslandStrings.Format("DictationModelDownloadingPercent", "Downloading… {0}%",
                    (int)(value * 100));
            });
            await DictationModelStore.DownloadAsync(model, progress);
            row.Busy = false;
            row.Installed = true;
            ModelsStatus.Text = IslandStrings.Get("DictationModelReady", "Model ready");
            // The first one downloaded becomes the active one: without a model there is no dictation.
            if (string.IsNullOrWhiteSpace(SettingsManager.Current.DictationModel))
                SettingsManager.Current.DictationModel = row.FileName;
            RefreshModels();
        }
        catch (Exception ex)
        {
            row.Busy = false;
            ModelsStatus.Text = IslandStrings.Format("DictationModelDownloadFailed", "Download failed: {0}", ex.Message);
            RefreshModels();
        }
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DictationModelRow row) return;
        DictationModelStore.Delete(row.FileName);
        if (string.Equals(SettingsManager.Current.DictationModel, row.FileName, StringComparison.OrdinalIgnoreCase))
            SettingsManager.Current.DictationModel = "";
        ModelsStatus.Text = "";
        RefreshModels();
    }

    /// <summary>
    /// Add a model from disk: it is copied into the models folder (that is where dictation
    /// looks for them) and becomes active, which is what the user expects when picking one.
    /// </summary>
    private async void AddLocalModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = IslandStrings.Get("DictationModelAddLocal", "Add local model"),
            Filter = IslandStrings.Get("DictationModelFileFilter",
                "Whisper/Parakeet model (*.bin;*.gguf)|*.bin;*.gguf|All files (*.*)|*.*"),
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            string path = await DictationModelStore.AddLocalAsync(dialog.FileName);
            SettingsManager.Current.DictationModel = Path.GetFileName(path);
            ModelsStatus.Text = IslandStrings.Get("DictationModelReady", "Model ready");
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = IslandStrings.Format("DictationModelCopyFailed", "Could not add the model: {0}", ex.Message);
        }
        RefreshModels();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DictationModelStore.ModelsFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = DictationModelStore.ModelsFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ModelsStatus.Text = ex.Message;
        }
    }

    private void OpenTranscriptions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DictationTranscriptHistory.Folder);
            if (Application.Current.MainWindow is MainWindow mainWindow)
                mainWindow.Dictation.CleanupTranscriptHistory();
            Process.Start(new ProcessStartInfo
            {
                FileName = DictationTranscriptHistory.Folder,
                UseShellExecute = true,
            });
            HistoryStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            HistoryStatus.Text = ex.Message;
            HistoryStatus.Visibility = Visibility.Visible;
        }
    }

    /// <summary>A row of the model list: what the XAML template draws.</summary>
    public partial class DictationModelRow : ObservableObject
    {
        public DictationModelRow(string FileName, string Name, string Subtitle, bool FromCatalog,
            bool Installed, bool Active)
        {
            this.FileName = FileName;
            this.Name = Name;
            this.Subtitle = Subtitle;
            this.FromCatalog = FromCatalog;
            this.Installed = Installed;
            this.Active = Active;
        }

        public string FileName { get; }
        public string Name { get; }
        public bool FromCatalog { get; }

        [ObservableProperty]
        public partial string Subtitle { get; set; }

        [ObservableProperty]
        public partial string DeviceText { get; set; } = "";

        [ObservableProperty]
        public partial bool Installed { get; set; }

        [ObservableProperty]
        public partial bool Active { get; set; }

        [ObservableProperty]
        public partial bool Busy { get; set; }

        /// <summary>Download progress (0-100).</summary>
        [ObservableProperty]
        public partial double Progress { get; set; }

        public FontWeight NameWeight => Active ? FontWeights.SemiBold : FontWeights.Normal;

        public bool CanUse => Installed && !Active && !Busy;
        public bool CanDownload => FromCatalog && !Installed && !Busy;
        public bool CanDelete => Installed && !Busy;

        partial void OnActiveChanged(bool value) => OnPropertyChanged(nameof(NameWeight));
        partial void OnInstalledChanged(bool value) => NotifyActions();
        partial void OnBusyChanged(bool value) => NotifyActions();

        private void NotifyActions()
        {
            OnPropertyChanged(nameof(CanUse));
            OnPropertyChanged(nameof(CanDownload));
            OnPropertyChanged(nameof(CanDelete));
        }
    }
}
